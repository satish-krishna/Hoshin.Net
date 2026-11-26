using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Hooks;

/// <summary>The resolved workflow checkout holding the change folder together with the repository the session is in.</summary>
public sealed record ResolvedHookRun(
    WorkflowRepo WorkflowRepo,
    string? SessionRepoRoot,
    string? SessionRepoName,
    string? WorkspaceRoot,
    string? MasterMainRoot)
{
    public string? ProductRepoName =>
        !string.IsNullOrWhiteSpace(SessionRepoName) && !string.Equals(SessionRepoName, "master", StringComparison.Ordinal)
            ? SessionRepoName
            : null;
}

/// <summary>Single owner resolving the active run, change checkout (copy when present), and session repository for all hooks and the guard.</summary>
public interface IHookRunResolver
{
    ResolvedHookRun? Resolve(NormalizedHookEvent ev);

    WorkflowRepo? ResolveAt(string directory, string? explicitChangeFolder = null);
}

/// <summary>
/// Resolves hook events against the workspace's canonical <c>&lt;master-git-common-dir&gt;/hoshin/</c> directory so the
/// master checkout, every change copy, and every declared product repository share one <c>run.json</c>, <c>step.json</c>,
/// trace log, and ledger root, while pointing <see cref="WorkflowRepo.Root"/> at the checkout holding the change folder.
/// </summary>
public sealed class HookRunResolver(
    IWorkspaceResolver workspaceResolver,
    MarkerReader markerReader,
    IGitWorktrees gitWorktrees,
    IGitQuery gitQuery,
    IGitStatus gitStatus,
    IFileSystem fileSystem,
    LedgerStore ledgerStore) : IHookRunResolver
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly IWorkspaceResolver _workspaceResolver = workspaceResolver ?? throw new ArgumentNullException(nameof(workspaceResolver));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitStatus _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly LedgerStore _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));

    public ResolvedHookRun? Resolve(NormalizedHookEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var cwd = string.IsNullOrWhiteSpace(ev.Cwd) ? Directory.GetCurrentDirectory() : ev.Cwd;
        if (!_fs.DirectoryExists(cwd))
        {
            return null;
        }

        var sessionTop = _gitWorktrees.TopLevel(cwd);
        var sessionRoot = sessionTop is null ? null : Path.GetFullPath(sessionTop);

        if (TryResolveWorkspaceRun(cwd, sessionRoot) is { } workspaceRun)
        {
            return workspaceRun;
        }

        if (sessionRoot is null)
        {
            return null;
        }

        var standalone = WorkflowRepo.Find(sessionRoot, _gitWorktrees, _gitQuery, _gitStatus, _fs, _ledgerStore);
        return standalone is null
            ? null
            : new ResolvedHookRun(standalone, sessionRoot, null, null, null);
    }

    public WorkflowRepo? ResolveAt(string directory, string? explicitChangeFolder = null) =>
        WorkflowRepo.Find(directory, _gitWorktrees, _gitQuery, _gitStatus, _fs, _ledgerStore, explicitChangeFolder);

    private ResolvedHookRun? TryResolveWorkspaceRun(string cwd, string? sessionRoot)
    {
        var resolution = _workspaceResolver.Resolve(cwd, new ResolveOptions(null, null));
        if (resolution.Master is null)
        {
            return null;
        }

        var masterDirs = _gitWorktrees.GitDirs(resolution.Master);
        if (masterDirs is null)
        {
            return null;
        }

        var masterCommon = Path.GetFullPath(masterDirs.Common);
        var masterMain = _gitWorktrees.MainCheckout(resolution.Master);
        if (!TryClassifyWorkspaceSession(cwd, sessionRoot, resolution, masterCommon, masterMain, out var sessionRepoName))
        {
            return null;
        }

        var changeFolder = ChangeFolderResolver.Resolve(resolution.Master, masterCommon, explicitChangeFolder: null, _fs);
        var checkoutRoot = ResolveCheckoutHoldingChangeFolder(resolution.Master, masterCommon, changeFolder);
        var workflowRepo = new WorkflowRepo(checkoutRoot, masterCommon, changeFolder, _gitQuery, _gitStatus, _fs, _ledgerStore);
        return new ResolvedHookRun(workflowRepo, sessionRoot, sessionRepoName, resolution.Workspace, masterMain);
    }

    private bool TryClassifyWorkspaceSession(
        string cwd,
        string? sessionRoot,
        Resolution resolution,
        string masterCommon,
        string masterMain,
        out string? sessionRepoName)
    {
        sessionRepoName = null;
        if (sessionRoot is null)
        {
            return resolution.Workspace is not null
                && PathHelpers.PathsEqual(PathHelpers.RealPath(cwd), resolution.Workspace);
        }

        var sessionDirs = _gitWorktrees.GitDirs(sessionRoot);
        if (sessionDirs is not null && PathHelpers.PathsEqual(sessionDirs.Common, masterCommon))
        {
            sessionRepoName = "master";
            return true;
        }

        var markerPath = Path.Combine(masterMain, MarkerConstants.FileName);
        if (!_fs.FileExists(markerPath))
        {
            return false;
        }

        Marker? marker;
        try
        {
            marker = _markerReader.TryReadMarker(markerPath);
        }
        catch (CouldNotRunException)
        {
            return false;
        }

        if (marker is null)
        {
            return false;
        }

        var folderName = Path.GetFileName(sessionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var system in marker.Systems.Values)
        {
            var matched = system.Repos.FirstOrDefault(r => string.Equals(r, folderName, PathComparison));
            if (matched is not null)
            {
                sessionRepoName = matched;
                return true;
            }
        }

        return false;
    }

    private string ResolveCheckoutHoldingChangeFolder(string fallbackRoot, string commonDir, string changeFolder)
    {
        var probe = ExistingDirectoryOrAncestor(changeFolder);
        if (probe is not null)
        {
            var top = _gitWorktrees.TopLevel(probe);
            if (top is not null)
            {
                var dirs = _gitWorktrees.GitDirs(top);
                if (dirs is not null && PathHelpers.PathsEqual(dirs.Common, commonDir))
                {
                    return Path.GetFullPath(top);
                }
            }
        }

        return Path.GetFullPath(fallbackRoot);
    }

    private string? ExistingDirectoryOrAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (_fs.DirectoryExists(current))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}
