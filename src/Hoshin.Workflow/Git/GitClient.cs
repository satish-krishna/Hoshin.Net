using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Workflow.Git;

public sealed record GitResult(bool Ok, int? Status, string Stdout, string Stderr);

public sealed record GitCommitInfo(string Sha, string Subject, List<string> GeneratedBy)
{
    public bool GeneratedByHoshin => GeneratedBy.Any(v => v is "hoshin" or "ai-sdlc");
}

public sealed record GitWorktree(string Path, string? Branch, bool Prunable);

public sealed record GitFileEntry(string Path, string Mode);

public sealed record GitDirsInfo(string GitDir, string Common);

public sealed record GitCommitOutcome(bool Ok, string? Sha, string? Error, string Output);

public sealed record TrailerCommit(string Sha, List<string> Values);

/// <summary>Composite abstraction for all local Git operations used by Hoshin.Workflow (prefer narrow role interfaces).</summary>
public interface IGitClient : IGitRunner, IGitQuery, IGitStatus, IGitBranches, IGitWorktrees, IGitCommitter
{
}

/// <summary>Default <see cref="IGitClient"/> shelling out to the <c>git</c> CLI via <see cref="ICommandRunner"/>.</summary>
public sealed class ProcessGitClient(ICommandRunner runner) : IGitClient
{
    public static readonly string[] BaseOptions = ["-c", "core.quotepath=off", "--no-optional-locks"];
    public static readonly string[] DiffOptions = ["--no-ext-diff", "--no-textconv"];

    private static readonly Regex LsTreeLine = new(@"^(\d+) blob [0-9a-f]+\t(.+)$", RegexOptions.Compiled);
    private static readonly Regex IndexLockRegex = new(@"index\.lock'?:? File exists|Unable to create '[^']*index\.lock'", RegexOptions.Compiled);
    private static readonly Regex DigitsRegex = new(@"^\d+$", RegexOptions.Compiled);

    private const int LockWaitMs = 2000;
    private const int LockRetries = 5;

    private readonly ICommandRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public GitResult Run(string cwd, IReadOnlyList<string> args, string? input = null)
    {
        var argv = new List<string>(1 + BaseOptions.Length + args.Count) { "git" };
        argv.AddRange(BaseOptions);
        argv.AddRange(args);
        var env = BuildGitEnvironment(args);
        var r = _runner.Run(new CommandRequest(cwd, argv, StandardInput: input, ExtraEnvironment: env, ClearEnvironment: true));
        if (!r.Started)
        {
            throw new CouldNotRunException($"cannot run git: {r.StartError}");
        }

        return new GitResult(r.Ok, r.ExitCode, r.Stdout, r.Stderr);
    }

    public byte[] RunBytes(string cwd, IReadOnlyList<string> args, string? input = null)
    {
        var argv = new List<string>(1 + BaseOptions.Length + args.Count) { "git" };
        argv.AddRange(BaseOptions);
        argv.AddRange(args);
        var env = BuildGitEnvironment(args);
        var r = _runner.Run(new CommandRequest(cwd, argv, StandardInput: input, ExtraEnvironment: env, CaptureRawBytes: true, ClearEnvironment: true));
        if (!r.Started || !r.Ok)
        {
            var detail = !string.IsNullOrWhiteSpace(r.Stderr) ? r.Stderr.Trim() : r.StartError ?? "unknown error";
            throw new CouldNotRunException($"git {(args.Count > 0 ? args[0] : "")} failed in {cwd}: {detail}");
        }

        return r.RawStdout ?? [];
    }

    private static Dictionary<string, string> BuildGitEnvironment(IReadOnlyList<string> args)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in AllowListedCommandRunner.AllowedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                env[name] = value;
            }
        }

        var trace2 = Environment.GetEnvironmentVariable("GIT_TRACE2_EVENT");
        if (trace2 is not null)
        {
            env["GIT_TRACE2_EVENT"] = trace2;
        }

        env["GIT_TERMINAL_PROMPT"] = "0";
        if (IsPushSubcommand(args))
        {
            foreach (var (k, v) in AllowListedCommandRunner.CollectPushEnvironment())
            {
                env[k] = v;
            }
        }

        return env;
    }

    private static bool IsPushSubcommand(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith('-'))
            {
                continue;
            }

            return string.Equals(arg, "push", StringComparison.Ordinal);
        }

        return false;
    }

    public string? Out(string cwd, IReadOnlyList<string> args)
    {
        var r = Run(cwd, args);
        return r.Ok ? r.Stdout.Trim() : null;
    }

    public string Must(string cwd, IReadOnlyList<string> args, string what)
    {
        var r = Run(cwd, args);
        if (!r.Ok)
        {
            var detail = string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr;
            throw new CouldNotRunException($"{what} failed in {cwd}: {detail.Trim()}");
        }

        return r.Stdout.Trim();
    }

    public string? TopLevel(string dir)
    {
        var top = Out(dir, ["rev-parse", "--show-toplevel"]);
        return top is null ? null : PathHelpers.RealPath(top);
    }

    public string GitPath(string dir, string path, bool common = false)
    {
        var output = Must(dir, common ? ["rev-parse", "--git-common-dir"] : ["rev-parse", "--git-dir"], "git rev-parse");
        var baseDir = Path.IsPathRooted(output) ? output : Path.GetFullPath(Path.Combine(dir, output));
        return Path.GetFullPath(Path.Combine(baseDir, path));
    }

    public GitDirsInfo? GitDirs(string dir)
    {
        var raw = Out(dir, ["rev-parse", "--absolute-git-dir", "--git-common-dir"]);
        if (raw is null)
        {
            return null;
        }

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        if (lines.Length != 2 || string.IsNullOrEmpty(lines[0]) || string.IsNullOrEmpty(lines[1]) ||
            lines[0].StartsWith('-') || lines[1].StartsWith('-'))
        {
            return null;
        }

        var gitDir = PathHelpers.RealPath(lines[0]);
        var commonRaw = Path.IsPathRooted(lines[1]) ? lines[1] : Path.Combine(dir, lines[1]);
        var common = PathHelpers.RealPath(commonRaw);
        return new GitDirsInfo(gitDir, common);
    }

    public string? CurrentBranch(string dir) => Out(dir, ["symbolic-ref", "--quiet", "--short", "HEAD"]);

    public string? CommitOf(string dir, string rev) => Out(dir, ["rev-parse", "--verify", "--quiet", $"{rev}^{{commit}}"]);

    public bool HasLocalBranch(string dir, string name) => CommitOf(dir, $"refs/heads/{name}") is not null;

    public bool HasRemoteBranch(string dir, string name) => CommitOf(dir, $"refs/remotes/origin/{name}") is not null;

    public string? BaseBranch(string dir)
    {
        var remoteHead = Out(dir, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"]);
        if (!string.IsNullOrEmpty(remoteHead))
        {
            return remoteHead.StartsWith("origin/", StringComparison.Ordinal) ? remoteHead["origin/".Length..] : remoteHead;
        }

        foreach (var name in new[] { "main", "master" })
        {
            if (HasLocalBranch(dir, name) || HasRemoteBranch(dir, name))
            {
                return name;
            }
        }

        return null;
    }

    public string? BaseRevision(string dir, string baseBranch)
    {
        if (HasLocalBranch(dir, baseBranch))
        {
            return $"refs/heads/{baseBranch}";
        }

        if (HasRemoteBranch(dir, baseBranch))
        {
            return $"refs/remotes/origin/{baseBranch}";
        }

        return null;
    }

    public string? ShowFile(string dir, string rev, string path)
    {
        var r = Run(dir, ["show", $"{rev}:{path}"]);
        return r.Ok ? r.Stdout : null;
    }

    public List<GitFileEntry> ListFiles(string dir, string rev, string path)
    {
        var output = Out(dir, ["ls-tree", "-r", "--full-tree", rev, "--", path]);
        if (string.IsNullOrEmpty(output))
        {
            return [];
        }

        var entries = new List<GitFileEntry>();
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var m = LsTreeLine.Match(line);
            if (m.Success)
            {
                entries.Add(new GitFileEntry(m.Groups[2].Value, m.Groups[1].Value));
            }
        }

        return entries;
    }

    public List<string> DirtyPaths(string dir, IReadOnlyList<string>? exclude = null)
    {
        var args = new List<string> { "status", "--porcelain", "--", "." };
        if (exclude is not null)
        {
            foreach (var p in exclude)
            {
                args.Add($":(exclude){p}");
            }
        }

        var r = Run(dir, args);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git status failed in {dir}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        return r.Stdout.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.Length > 3)
            .Select(line => line[3..])
            .ToList();
    }

    public List<string> BranchNames(string dir)
    {
        var output = Out(dir, ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes/origin"]) ?? "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var raw in output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw;
            if (name.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                name = name["refs/heads/".Length..];
            }
            else if (name.StartsWith("refs/remotes/origin/", StringComparison.Ordinal))
            {
                name = name["refs/remotes/origin/".Length..];
            }

            if (name != "HEAD" && seen.Add(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    public List<GitCommitInfo> CommitsBeyondBase(string dir, string branch, string baseBranch)
    {
        var notRefs = new[] { $"refs/heads/{baseBranch}", $"refs/remotes/origin/{baseBranch}" }
            .Where(r => CommitOf(dir, r) is not null)
            .ToList();
        var args = new List<string> { "log", "--format=%H%x1f%s%x1f%(trailers:key=Generated-By,valueonly,separator=%x1d)%x1e", branch };
        if (notRefs.Count > 0)
        {
            args.Add("--not");
            args.AddRange(notRefs);
        }

        var output = Must(dir, args, "git log");
        var commits = new List<GitCommitInfo>();
        foreach (var raw in output.Split('\x1e'))
        {
            var record = raw.Trim();
            if (record.Length == 0)
            {
                continue;
            }

            var parts = record.Split('\x1f');
            var sha = parts.Length > 0 ? parts[0] : "";
            var subject = parts.Length > 1 ? parts[1] : "";
            var trailers = parts.Length > 2
                ? parts[2].Split('\x1d').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
                : [];
            commits.Add(new GitCommitInfo(sha, subject, trailers));
        }

        return commits;
    }

    public bool IsAncestor(string dir, string ancestor, string descendant) =>
        Run(dir, ["merge-base", "--is-ancestor", ancestor, descendant]).Ok;

    public string MainCheckout(string dir)
    {
        var output = Must(dir, ["worktree", "list", "--porcelain"], "git worktree list");
        var first = output.Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.StartsWith("worktree ", StringComparison.Ordinal));
        if (first is null)
        {
            throw new CouldNotRunException($"git worktree list printed no worktree in {dir}");
        }

        return PathHelpers.RealPath(first["worktree ".Length..]);
    }

    public List<GitWorktree> Worktrees(string dir)
    {
        var output = Must(dir, ["worktree", "list", "--porcelain"], "git worktree list");
        var normalized = output.Replace("\r\n", "\n");
        var list = new List<GitWorktree>();
        foreach (var block in normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var pathLine = lines.FirstOrDefault(l => l.StartsWith("worktree ", StringComparison.Ordinal));
            if (pathLine is null || lines.Contains("bare"))
            {
                continue;
            }

            var branchLine = lines.FirstOrDefault(l => l.StartsWith("branch refs/heads/", StringComparison.Ordinal));
            var branch = branchLine is null ? null : branchLine["branch refs/heads/".Length..];
            var prunable = lines.Any(l => l.StartsWith("prunable", StringComparison.Ordinal));
            list.Add(new GitWorktree(PathHelpers.RealPath(pathLine["worktree ".Length..]), branch, prunable));
        }

        return list;
    }

    public string? CheckedOutIn(string dir, string branch)
    {
        var output = Out(dir, ["worktree", "list", "--porcelain"]) ?? "";
        string? path = null;
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = PathHelpers.RealPath(line["worktree ".Length..]);
            }

            if (line == $"branch refs/heads/{branch}")
            {
                return path;
            }
        }

        return null;
    }

    public List<string> ChangedPaths(string dir, IReadOnlyList<string> paths)
    {
        var args = new List<string> { "--literal-pathspecs", "status", "--porcelain", "-z", "--untracked-files=all", "--" };
        args.AddRange(paths);
        var r = Run(dir, args);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git status failed in {dir}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        var listed = new List<string>();
        var fields = r.Stdout.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var entry = fields[i];
            if (entry.Length < 4)
            {
                continue;
            }

            listed.Add(entry[3..]);
            if (entry[0] is 'R' or 'C' && i + 1 < fields.Length)
            {
                listed.Add(fields[++i]);
            }
        }

        return paths.Where(p =>
        {
            var prefix = p.EndsWith('/') ? p : p + "/";
            return listed.Any(l => l == p || l.StartsWith(prefix, StringComparison.Ordinal));
        }).ToList();
    }

    public bool ExistsAt(string dir, string rev, string path) =>
        Run(dir, ["--literal-pathspecs", "cat-file", "-e", $"{rev}:{path}"]).Ok;

    public GitCommitOutcome CommitPaths(string dir, IReadOnlyList<string> paths, IReadOnlyList<string> paragraphs)
    {
        GitCommitOutcome Fail(GitResult r) =>
            new(false, null, (string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim(), r.Stdout + r.Stderr);

        var addArgs = new List<string> { "--literal-pathspecs", "add", "-A", "--" };
        addArgs.AddRange(paths);
        var added = RunWaitingForLock(dir, addArgs);
        if (!added.Ok)
        {
            return Fail(added);
        }

        var commitArgs = new List<string> { "--literal-pathspecs", "commit", "--quiet" };
        foreach (var p in paragraphs)
        {
            commitArgs.Add("-m");
            commitArgs.Add(p);
        }

        commitArgs.Add("--");
        commitArgs.AddRange(paths);
        var committed = RunWaitingForLock(dir, commitArgs);
        if (!committed.Ok)
        {
            return Fail(committed);
        }

        return new GitCommitOutcome(true, Must(dir, ["rev-parse", "HEAD"], "git rev-parse"), null, committed.Stdout + committed.Stderr);
    }

    private GitResult RunWaitingForLock(string dir, IReadOnlyList<string> args)
    {
        var r = Run(dir, args);
        for (var i = 0; i < LockRetries && !r.Ok && IndexLockRegex.IsMatch(r.Stderr + r.Stdout); i++)
        {
            Thread.Sleep(LockWaitMs);
            r = Run(dir, args);
        }

        return r;
    }

    public string? MergeBase(string dir, string a, string b) => Out(dir, ["merge-base", a, b]);

    public int? CountCommits(string dir, string from, string to, IReadOnlyList<string> paths)
    {
        var args = new List<string> { "rev-list", "--count", $"{from}..{to}", "--" };
        args.AddRange(paths);
        var output = Out(dir, args);
        return output is null || !DigitsRegex.IsMatch(output) ? null : int.Parse(output, CultureInfo.InvariantCulture);
    }

    public List<TrailerCommit> CommitsWithTrailer(string dir, string from, string to, string key, IReadOnlyList<string>? paths = null)
    {
        var args = new List<string> { "log", "--reverse", $"--format=%H%x1f%(trailers:key={key},valueonly,separator=%x1d)%x1e", $"{from}..{to}" };
        if (paths is { Count: > 0 })
        {
            args.Add("--");
            args.AddRange(paths);
        }

        var output = Must(dir, args, "git log");
        var commits = new List<TrailerCommit>();
        foreach (var raw in output.Split('\x1e'))
        {
            var record = raw.Trim();
            if (record.Length == 0)
            {
                continue;
            }

            var parts = record.Split('\x1f');
            var sha = parts.Length > 0 ? parts[0] : "";
            var values = parts.Length > 1
                ? parts[1].Split('\x1d').Select(t => t.Trim()).Where(t => t.Length > 0).ToList()
                : [];
            commits.Add(new TrailerCommit(sha, values));
        }

        return commits;
    }

    public string? LastCommitTouching(string dir, string range, IReadOnlyList<string> pathspec)
    {
        var args = new List<string> { "log", "-1", "--format=%H", range, "--" };
        args.AddRange(pathspec);
        var r = Run(dir, args);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git log failed in {dir}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        var sha = r.Stdout.Trim();
        return sha.Length == 0 ? null : sha;
    }

    public List<string> TrackedChanges(string dir)
    {
        var r = Run(dir, ["status", "--porcelain", "--untracked-files=no"]);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git status failed in {dir}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        return r.Stdout.Replace("\r\n", "\n").Split('\n')
            .Where(line => line.Length > 3)
            .Select(line => line[3..])
            .ToList();
    }

    public List<string> ChangedFilesBetween(string dir, string from, string to, IReadOnlyList<string> pathspec)
    {
        var args = new List<string> { "diff", "--name-only", "-z", from, to, "--" };
        args.AddRange(pathspec);
        var r = Run(dir, args);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git diff failed in {dir}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        return r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public HashSet<string> ReportedPaths(string top, IReadOnlyList<string> paths)
    {
        var args = new List<string> { "status", "--porcelain=v1", "-z", "--untracked-files=all", "--" };
        args.AddRange(paths);
        var r = Run(top, args);
        if (!r.Ok)
        {
            throw new CouldNotRunException($"git status failed in {top}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        var fields = r.Stdout.Split('\0');
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            if (field.Length < 4)
            {
                continue;
            }

            reported.Add(field[3..]);
            if (field[0] is 'R' or 'C' && i + 1 < fields.Length)
            {
                reported.Add(fields[++i]);
            }
        }

        return reported;
    }

    public bool DiffersFromHead(string top, string path) => Differs(top, ["HEAD"], path);

    public bool Differs(string top, IReadOnlyList<string> revs, string path)
    {
        var args = new List<string> { "diff", "--quiet" };
        args.AddRange(revs);
        args.AddRange(DiffOptions);
        args.Add("--");
        args.Add(path);
        var r = Run(top, args);
        if (r.Ok)
        {
            return false;
        }

        if (r.Status == 1)
        {
            return true;
        }

        throw new CouldNotRunException($"git diff failed in {top}: {(string.IsNullOrEmpty(r.Stderr) ? r.Stdout : r.Stderr).Trim()}");
    }

    public string? CodeHead(string dir, IReadOnlyList<string> excludeRelativePaths)
    {
        var args = new List<string> { "log", "-1", "--format=%H", "--", "." };
        foreach (var path in excludeRelativePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                args.Add($":(exclude){path}");
            }
        }

        return Out(dir, args);
    }

    public string? LastCommitForPath(string dir, string relativePath) =>
        Out(dir, ["log", "-1", "--format=%H", "--", relativePath]);

    public string? RemoteUrl(string dir, string remote) =>
        Out(dir, ["remote", "get-url", remote]);

    public string PorcelainStatusZ(string dir)
    {
        var r = Run(dir, ["status", "--porcelain=v1", "-z", "-uall"]);
        return r.Ok ? r.Stdout : "";
    }

    public string? UpstreamRemote(string dir)
    {
        var upstream = Out(dir, ["rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}"]);
        if (string.IsNullOrWhiteSpace(upstream))
        {
            return null;
        }

        var slash = upstream.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? upstream[..slash] : null;
    }

    public GitResult EnsureDetachedWorktree(string repoRoot, string worktreeDir, string rev)
    {
        var gitMarker = Path.Combine(worktreeDir, ".git");
        if (Directory.Exists(worktreeDir) && (File.Exists(gitMarker) || Directory.Exists(gitMarker)))
        {
            var checkout = Run(worktreeDir, ["checkout", "--detach", "--force", rev]);
            if (checkout.Ok)
            {
                Run(worktreeDir, ["clean", "-fdx", "--quiet"]);
                return checkout;
            }

            Run(repoRoot, ["worktree", "remove", "--force", worktreeDir]);
            Run(repoRoot, ["worktree", "prune"]);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreeDir)!);
        if (Directory.Exists(worktreeDir))
        {
            Directory.Delete(worktreeDir, recursive: true);
        }

        return Run(repoRoot, ["worktree", "add", "--detach", "--force", worktreeDir, rev]);
    }

    public GitResult CleanDetachedWorktree(string worktreeDir, string rev)
    {
        var reset = Run(worktreeDir, ["reset", "--hard", "--quiet", rev]);
        Run(worktreeDir, ["clean", "-fdx", "--quiet"]);
        return reset;
    }

    public GitResult ResetMixed(string dir, string rev) =>
        Run(dir, ["reset", "--mixed", "--quiet", rev]);

    public GitResult Push(string dir, string remote, string refSpec) =>
        Run(dir, ["push", remote, refSpec]);
}
