using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record CommitRequest(
    string Kind,
    string? System,
    string? Change,
    string? Repo,
    string? Task,
    string? Type,
    string? Subject,
    string? CoAuthor,
    ResolveOptions ResolveOptions,
    IReadOnlyList<string> Paths,
    string Cwd);

public sealed record CommitOutcome(
    bool Committed,
    string Repository,
    string? Sha,
    string? Header,
    List<string> Paths,
    string? Reason,
    List<string> Problems);

/// <summary>Performs all skill-requested commits with document checks, conventional headers, and trailers.</summary>
public sealed class Committer(
    IGitClient? git = null,
    IWorkspaceResolver? resolver = null,
    ChangeLocator? locator = null,
    AppService? apps = null,
    DocumentCheckService? checker = null,
    ProgressService? progress = null,
    ReviewReader? reviewReader = null)
{
    public static Committer Default { get; } = new();

    public const string Trailer = "Generated-By: hoshin";

    public static readonly Regex HeaderRegex = new(@"^(feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(\([^)\s]+\))?!?: \S.*$", RegexOptions.Compiled);
    private static readonly Regex CoAuthorRegex = new(@"^[^<>\r\n]*[^<>\s][^<>\r\n]* <[^<>\s@]+@[^<>\s@]+>$", RegexOptions.Compiled);
    private static readonly Regex TaskIdRegex = new(@"^T\d+$", RegexOptions.Compiled);
    private static readonly Regex SystemHooksRel = new(@"^intent/[^/]+/hooks\.json$", RegexOptions.Compiled);
    private static readonly Regex BuildLogRel = new(@"/build/[^/]+\.md$", RegexOptions.Compiled);

    private sealed record KindRule(string Needs, string Check, string? Subject = null);

    private static readonly Dictionary<string, KindRule> Kinds = new(StringComparer.Ordinal)
    {
        ["setup"] = new("system", "none"),
        ["instructions"] = new("none", "none"),
        ["context"] = new("system", "none"),
        ["intent-draft"] = new("change", "none", "draft intent"),
        ["intent-reopen"] = new("change", "none", "reopen intent"),
        ["spec"] = new("change", "spec", "write spec"),
        ["spec-reopen"] = new("change", "spec", "reopen spec"),
        ["plan"] = new("change", "plan", "write plan"),
        ["build-log"] = new("change", "build"),
        ["review"] = new("change", "review"),
        ["fix-draft"] = new("change", "plan", "draft fix"),
        ["fix-reopen"] = new("change", "plan", "reopen fix"),
        ["accept-intent"] = new("change", "none", "accept intent"),
        ["accept-spec"] = new("change", "plan", "accept spec"),
        ["accept-plan"] = new("change", "plan", "accept plan"),
        ["reject-intent"] = new("change", "none", "reject intent"),
        ["reject-fix"] = new("change", "none", "reject fix"),
        ["work"] = new("work", "none"),
    };

    private sealed record TargetInfo(string Repository, string System, string Scope, string? Folder, string? Change);

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly IWorkspaceResolver _resolver = resolver ?? new WorkspaceResolver(git);
    private readonly ChangeLocator _locator = locator ?? new ChangeLocator(git);
    private readonly AppService _apps = apps ?? new AppService(git);
    private readonly DocumentCheckService _checker = checker ?? new DocumentCheckService(git: git);
    private readonly ProgressService _progress = progress ?? ProgressService.Default;
    private readonly ReviewReader _review = reviewReader ?? new ReviewReader();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public CommitOutcome Commit(CommitRequest req)
    {
        if (!Kinds.TryGetValue(req.Kind, out var rule))
        {
            throw new CouldNotRunException($"unknown --kind {req.Kind}: one of {string.Join(", ", Kinds.Keys)}");
        }

        if (req.Paths.Count == 0)
        {
            throw new CouldNotRunException("name the paths to commit after --");
        }

        if (rule.Needs == "work")
        {
            CheckWorkOptions(req);
        }

        if (req.CoAuthor is not null && !CoAuthorRegex.IsMatch(req.CoAuthor))
        {
            throw new CouldNotRunException($"--co-author must be \"Name <email>\" on one line, not {System.Text.Json.JsonSerializer.Serialize(req.CoAuthor)}");
        }

        var t = ResolveTarget(req, rule);
        var given = InRepository(t.Repository, req.Paths, req.Cwd).Distinct(StringComparer.Ordinal).ToList();
        var paths = _git.ChangedPaths(t.Repository, given);
        if (paths.Count == 0)
        {
            return new CommitOutcome(false, t.Repository, null, null, [], "nothing changed", []);
        }

        var checkedFiles = req.Kind == "setup"
            ? SetupFiles(t.Repository, paths)
            : t.Folder is not null
                ? DocumentsToCheck(t.Folder, rule.Check)
                : [];
        if (checkedFiles.Count > 0)
        {
            var problems = _checker.CheckFiles(checkedFiles);
            if (problems.Count > 0)
            {
                return new CommitOutcome(false, t.Repository, null, null, paths, "check failed", problems.Select(ProblemFormatter.Format).ToList());
            }
        }

        var paragraphs = BuildMessage(req, rule, t, paths);
        if (req.CoAuthor is not null)
        {
            paragraphs[^1] += $"\nCo-Authored-By: {req.CoAuthor}";
        }

        var header = paragraphs[0];
        if (!HeaderRegex.IsMatch(header))
        {
            throw new CouldNotRunException($"the header \"{header}\" is not a Conventional Commits header; this is a bug in the hoshin program, or --subject is not a subject");
        }

        if (_git.CurrentBranch(t.Repository) is null)
        {
            return new CommitOutcome(false, t.Repository, null, header, paths, "git refused", [$"HEAD is detached in {t.Repository}: check out a branch before committing"]);
        }

        var committed = _git.CommitPaths(t.Repository, paths, paragraphs);
        if (!committed.Ok)
        {
            return new CommitOutcome(false, t.Repository, null, header, paths, "git refused", [committed.Error ?? "git commit failed"]);
        }

        return new CommitOutcome(true, t.Repository, committed.Sha, header, paths, null, []);
    }

    private static string Need(string? value, string option, string kind)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new CouldNotRunException($"--kind {kind} needs {option}");
        }

        return value;
    }

    private static void CheckWorkOptions(CommitRequest req)
    {
        var type = Need(req.Type, "--type <type>", req.Kind);
        Need(req.Subject, "--subject <text>", req.Kind);
        if (!ChangeRecordStore.CommitTypes.Contains(type))
        {
            throw new CouldNotRunException($"--type must be one of {string.Join(", ", ChangeRecordStore.CommitTypes)}");
        }

        if (req.Task is not null && !TaskIdRegex.IsMatch(req.Task))
        {
            throw new CouldNotRunException($"--task must be a plan task, T<n>, not {req.Task}");
        }
    }

    private TargetInfo ResolveTarget(CommitRequest req, KindRule rule)
    {
        var resolution = _resolver.Resolve(req.Cwd, req.ResolveOptions);
        if (resolution.Master is null || resolution.Workspace is null)
        {
            throw new CouldNotRunException(resolution.Problems.Count > 0 ? string.Join('\n', resolution.Problems) : "no master repository");
        }

        var master = resolution.Master;
        var workspace = resolution.Workspace;

        if (rule.Needs is "system" or "none")
        {
            var systems = MarkerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName)).Systems.Keys.ToList();
            var system = rule.Needs == "system"
                ? Need(req.System, "--system <system>", req.Kind)
                : systems.FirstOrDefault() ?? throw new CouldNotRunException("the marker lists no systems");
            if (!systems.Contains(system))
            {
                throw new CouldNotRunException($"the marker lists no system {system}");
            }

            return new TargetInfo(PathHelpers.RealPath(master), system, ScopeRules.SystemScope(master, system), null, null);
        }

        var change = Need(req.Change, "--change <change>", req.Kind);
        var found = _locator.FindChange(master, workspace, change, req.System);
        var folder = Path.Combine(found.Master, "intent", found.System, change);
        var scope = ScopeRules.ChangeScope(found.Master, found.System, folder);
        if (rule.Needs == "change")
        {
            return new TargetInfo(PathHelpers.RealPath(found.Master), found.System, scope, folder, change);
        }

        var repo = Need(req.Repo, "--repo <repo>", req.Kind);
        var repos = _locator.SystemRepos(found.Master, workspace, found.System);
        if (!repos.Contains(repo))
        {
            throw new CouldNotRunException($"{repo} is not a repository of system {found.System} ({(repos.Count > 0 ? string.Join(", ", repos) : "none")})");
        }

        var single = _apps.IsSingleRepo(found.Master);
        var path = single ? found.Master : Path.Combine(workspace, repo);
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
        {
            throw new CouldNotRunException(single ? $"{found.Master} is not a git repository" : $"{repo} is not checked out in {workspace}");
        }

        return new TargetInfo(PathHelpers.RealPath(path), found.System, scope, folder, change);
    }

    private static List<string> InRepository(string repository, IReadOnlyList<string> paths, string cwd)
    {
        static bool Outside(string rel) =>
            rel.Length == 0 || rel == "." || rel == ".." ||
            rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            rel.StartsWith("../", StringComparison.Ordinal) ||
            Path.IsPathRooted(rel);

        var result = new List<string>();
        foreach (var p in paths)
        {
            var fromCwd = Path.GetFullPath(p, cwd);
            var rel = Path.GetRelativePath(repository, PathHelpers.RealishPath(fromCwd));
            if (Outside(rel) && !Path.IsPathRooted(p) && !File.Exists(fromCwd) && !Directory.Exists(fromCwd))
            {
                var fromRepo = Path.GetFullPath(p, repository);
                if (File.Exists(fromRepo) || Directory.Exists(fromRepo))
                {
                    rel = Path.GetRelativePath(repository, PathHelpers.RealishPath(fromRepo));
                }
            }

            if (Outside(rel))
            {
                throw new CouldNotRunException($"{p} is not inside {repository}, where --kind commits");
            }

            result.Add(rel.Replace('\\', '/'));
        }

        return result;
    }

    private static List<string> DocumentsToCheck(string folder, string check)
    {
        if (check == "none")
        {
            return [];
        }

        string[] order = ["spec", "plan", "build", "review"];
        var upTo = Array.IndexOf(order, check);
        var files = new List<string> { Path.Combine(folder, "spec.md") };
        if (upTo >= 1)
        {
            files.Add(Path.Combine(folder, "plan.md"));
        }

        var build = Path.Combine(folder, "build");
        if (upTo >= 2 && Directory.Exists(build))
        {
            files.AddRange(Directory.GetFiles(build, "*.md")
                .Where(f => Path.GetFileName(f) != "review.md")
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal));
        }

        if (upTo >= 3)
        {
            files.Add(Path.Combine(build, "review.md"));
        }

        return files.Where(File.Exists).ToList();
    }

    private static List<string> SetupFiles(string repository, List<string> paths) =>
        paths
            .Where(p => p == MarkerConstants.FileName || SystemHooksRel.IsMatch(p))
            .Select(p => Path.Combine(repository, p.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();

    private List<string> AddedEntries(TargetInfo t, List<string> paths)
    {
        var logs = paths.Where(p => BuildLogRel.IsMatch(p) && !p.EndsWith("/build/review.md", StringComparison.Ordinal));
        var added = new List<string>();
        foreach (var p in logs)
        {
            var file = Path.Combine(t.Repository, p.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(file))
            {
                continue;
            }

            var before = _git.ShowFile(t.Repository, "HEAD", p);
            var had = before is null ? new HashSet<string>(StringComparer.Ordinal) : _progress.EntriesOf(before, file);
            foreach (var task in _progress.EntriesOf(File.ReadAllText(file), file))
            {
                if (!had.Contains(task))
                {
                    added.Add(task);
                }
            }
        }

        return added;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private List<string> BuildMessage(CommitRequest req, KindRule rule, TargetInfo t, List<string> paths)
    {
        List<string> Docs(string subject) => [$"docs({t.Scope}): {subject} {t.Change}", Trailer];

        if (rule.Subject is not null)
        {
            return Docs(rule.Subject);
        }

        switch (req.Kind)
        {
            case "setup":
                return [$"chore({t.Scope}): set up hoshin for {t.System}", Trailer];

            case "instructions":
                return [$"chore({t.Scope}): refresh workspace instructions", Trailer];

            case "context":
            {
                var prefix = $"intent/{t.System}/context/";
                var repos = paths
                    .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.EndsWith(".md", StringComparison.Ordinal) && !p[prefix.Length..].Contains('/'))
                    .Select(p => p[prefix.Length..^3])
                    .ToList();
                if (repos.Count == 0)
                {
                    throw new CouldNotRunException($"--kind context commits intent/{t.System}/context/<repo>.md files, and no path given is one");
                }

                var verb = paths.All(p => _git.ExistsAt(t.Repository, "HEAD", p)) ? "refresh" : "build";
                if (repos.Count <= 3)
                {
                    return [$"docs({t.Scope}): {verb} context for {string.Join(", ", repos)}", Trailer];
                }

                return [$"docs({t.Scope}): {verb} context for {repos.Count} repositories", string.Join(", ", repos), Trailer];
            }

            case "build-log":
            {
                var tasks = AddedEntries(t, paths);
                if (tasks.Count == 0)
                {
                    throw new CouldNotRunException("--kind build-log commits new build log entries, and the build logs given add none against HEAD");
                }

                return Docs($"log {string.Join(", ", tasks)} of");
            }

            case "review":
            {
                var file = Path.Combine(t.Folder!, "build", "review.md");
                if (!File.Exists(file))
                {
                    throw new CouldNotRunException($"{file} does not exist");
                }

                var result = _review.Read(File.ReadAllText(file), file);
                var n = result.Document.Reviews.Count;
                if (n == 0)
                {
                    throw new CouldNotRunException($"{file} has no ## Review section");
                }

                return Docs($"record review {n} of");
            }

            default:
            {
                var trailers = req.Task is null ? Trailer : $"{Trailer}\nPlan-Task: {req.Task}";
                return [$"{req.Type}({t.Scope}): {req.Subject}", trailers];
            }
        }
    }
}
