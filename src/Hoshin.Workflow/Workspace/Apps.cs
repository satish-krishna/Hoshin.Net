using System.Diagnostics.CodeAnalysis;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Workspace;

public sealed record RepositoryRef(string Repo, string Path);

/// <summary>Single-repo app mapping, heads, touched-app detection, and uncommitted filtering.</summary>
public sealed class AppService(IGitClient? git = null)
{
    public static AppService Default { get; } = new();

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method on injected service.")]
    public bool IsSingleRepo(string master)
    {
        var file = Path.Combine(master, MarkerConstants.FileName);
        return File.Exists(file) && MarkerReader.ReadMarker(file).Layout == WorkspaceLayout.SingleRepo;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method on injected service.")]
    public Dictionary<string, string> AppFolders(string master, string system)
    {
        var marker = MarkerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName));
        return marker.Systems.TryGetValue(system, out var entry)
            ? new Dictionary<string, string>(entry.Apps, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method on injected service.")]
    public List<string> AppPathspec(string folder) => [folder, .. OwnFiles.ExcludingOwnFiles()];

    public List<string> UncommittedInApp(string root, string folder)
    {
        var all = OwnFiles.UncommittedFiles(_git, root);
        return folder == "."
            ? all
            : all.Where(p => p == folder || p.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
    }

    public string RepoPathOf(string master, string workspace, string system, string name)
    {
        if (!IsSingleRepo(master))
        {
            return Path.Combine(workspace, name);
        }

        if (!AppFolders(master, system).TryGetValue(name, out var folder))
        {
            throw new CouldNotRunException($"system {system} has no app {name}");
        }

        return folder == "." ? master : Path.GetFullPath(Path.Combine(master, folder));
    }

    public List<RepositoryRef> ProductRepositories(string master, string workspace, IReadOnlyList<string> repos)
    {
        if (IsSingleRepo(master))
        {
            return [];
        }

        return repos
            .Select(r => new RepositoryRef(r, Path.Combine(workspace, r)))
            .Where(r => Directory.Exists(r.Path))
            .ToList();
    }

    public string AppHead(string root, string rev, string folder, string baseBranch)
    {
        var baseRev = _git.BaseRevision(root, baseBranch) ?? baseBranch;
        var fork = _git.MergeBase(root, baseRev, rev)
            ?? throw new CouldNotRunException($"{baseBranch} and {rev} have no common commit in {root}");
        return _git.LastCommitTouching(root, $"{fork}..{rev}", AppPathspec(folder)) ?? fork;
    }

    public string? CurrentAppHead(string root, string master, string system, string app)
    {
        if (!AppFolders(master, system).TryGetValue(app, out var folder))
        {
            return null;
        }

        var baseBranch = _git.BaseBranch(root);
        if (baseBranch is null || _git.BaseRevision(root, baseBranch) is null || _git.CommitOf(root, "HEAD") is null)
        {
            return null;
        }

        return AppHead(root, "HEAD", folder, baseBranch);
    }

    public List<string> TouchedApps(string root, string system, string master, string baseBranch, string rev)
    {
        var baseRev = _git.BaseRevision(root, baseBranch) ?? baseBranch;
        var fork = _git.MergeBase(root, baseRev, rev)
            ?? throw new CouldNotRunException($"{baseBranch} and {rev} have no common commit in {root}");
        return AppFolders(master, system)
            .Where(kv => _git.ChangedFilesBetween(root, fork, rev, AppPathspec(kv.Value)).Count > 0)
            .Select(kv => kv.Key)
            .ToList();
    }
}
