using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Workspace;

public sealed record RepoStaleness(
    string Repo,
    string Path,
    bool CheckedOut,
    string Context,
    bool Exists,
    string? SourceCommit,
    int? Behind,
    bool Stale,
    string Reason);

public sealed record StaleResult(string System, List<RepoStaleness> Repos, List<string> Problems);

public sealed record StaleOptions(string System, List<string>? Repos = null, string? Master = null, string? Workspace = null);

/// <summary>Computes staleness of each repository/app context file against git commits.</summary>
public sealed class StaleChecker(
    IGitClient git,
    IWorkspaceResolver resolver,
    ChangeLocator locator,
    AppService apps)
{
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ChangeLocator _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

    public StaleResult Check(string cwd, StaleOptions options)
    {
        var resolution = _resolver.Resolve(cwd, new ResolveOptions(options.Master, options.Workspace));
        if (resolution.Master is null || resolution.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', resolution.Problems));
        }

        var master = resolution.Master;
        var workspace = resolution.Workspace;
        var all = _locator.SystemRepos(master, workspace, options.System);
        var requested = options.Repos ?? [];
        var unknown = requested.Where(r => !all.Contains(r)).ToList();
        if (unknown.Count > 0)
        {
            throw new CouldNotRunException(
                $"{string.Join(", ", unknown)} {(unknown.Count == 1 ? "is not a repository" : "are not repositories")} of system {options.System} ({(all.Count > 0 ? string.Join(", ", all) : "none")})");
        }

        var repos = requested.Count == 0 ? all : all.Where(requested.Contains).ToList();
        var folders = _apps.IsSingleRepo(master) ? _apps.AppFolders(master, options.System) : null;
        var results = repos
            .Select(repo => CheckOne(master, workspace, options.System, repo, folders?.GetValueOrDefault(repo)))
            .ToList();
        return new StaleResult(options.System, results, []);
    }

    private RepoStaleness CheckOne(string master, string workspace, string system, string repo, string? appFolder)
    {
        var root = appFolder is null ? Path.Combine(workspace, repo) : master;
        var path = appFolder is null ? root : (appFolder == "." ? master : Path.GetFullPath(Path.Combine(master, appFolder)));
        var context = Path.Combine(master, "intent", system, "context", $"{repo}.md");
        var gitMarker = Path.Combine(root, ".git");
        var checkedOut = Directory.Exists(gitMarker) || File.Exists(gitMarker);
        var exists = File.Exists(context);
        var sourceCommit = exists ? SourceCommitOf(context) : null;

        RepoStaleness Entry(int? behind, bool stale, string reason) =>
            new(repo, path, checkedOut, context, exists, sourceCommit, behind, stale, reason);

        if (!exists)
        {
            return Entry(null, false, "missing");
        }

        if (!checkedOut)
        {
            return Entry(null, false, "not-checked-out");
        }

        if (sourceCommit is null)
        {
            return Entry(null, true, "no-source-commit");
        }

        var commit = sourceCommit.StartsWith('-') ? null : _git.CommitOf(root, sourceCommit);
        if (commit is null)
        {
            return Entry(null, true, "unknown-commit");
        }

        var pathspec = appFolder is null
            ? new List<string>(["."]).Concat(OwnFiles.ExcludingOwnFiles()).ToList()
            : _apps.AppPathspec(appFolder);
        var behind = _git.CountCommits(root, commit, "HEAD", pathspec);
        if (behind is null)
        {
            return Entry(null, true, "unknown-commit");
        }

        return behind > 0 ? Entry(behind, true, "behind") : Entry(0, false, "current");
    }

    private static string? SourceCommitOf(string file)
    {
        try
        {
            var raw = FrontMatter.TryGetScalar(File.ReadAllText(file), file, "source_commit");
            var trimmed = raw?.Trim();
            return string.IsNullOrEmpty(trimmed) ? null : trimmed;
        }
        catch
        {
            return null;
        }
    }
}
