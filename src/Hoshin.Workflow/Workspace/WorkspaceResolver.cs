using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Workspace;

public sealed record ResolveOptions(string? Master = null, string? Workspace = null);

public sealed record Resolution(
    string? Workspace,
    string? Master,
    bool MasterIsCopy,
    List<string> Problems,
    WorkspaceLayout Layout);

/// <summary>Resolves the workspace and master repository from the current directory and optional flags.</summary>
public interface IWorkspaceResolver
{
    Resolution Resolve(string cwd, ResolveOptions options, IReadOnlyList<string>? markers = null);
    string MasterFolderName(string workspace, string master, IReadOnlyList<string>? markers = null);
}

public sealed class WorkspaceResolver(IGitWorktrees gitWorktrees, IGitRunner gitRunner, MarkerReader markerReader) : IWorkspaceResolver
{
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitRunner _gitRunner = gitRunner ?? throw new ArgumentNullException(nameof(gitRunner));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public Resolution Resolve(string cwd, ResolveOptions options, IReadOnlyList<string>? markers = null)
    {
        var markerNames = markers ?? MarkerConstants.DefaultMarkers;
        if (options.Workspace is not null && !PathHelpers.IsDirectory(options.Workspace))
        {
            return Refused(null, $"`{Path.GetFullPath(options.Workspace)}` does not exist.");
        }

        var workspaceOption = options.Workspace is null ? null : PathHelpers.RealPath(options.Workspace);
        var sessionTop = _gitWorktrees.TopLevel(cwd);

        if (options.Master is not null)
        {
            return WithMaster(options.Master, workspaceOption, sessionTop, markerNames);
        }

        if (sessionTop is not null && HoldsMarker(sessionTop, markerNames))
        {
            if (LayoutOf(sessionTop, markerNames) == WorkspaceLayout.SingleRepo)
            {
                return WithMaster(sessionTop, workspaceOption, sessionTop, markerNames);
            }

            var dirs = _gitWorktrees.GitDirs(sessionTop);
            if (dirs is not null && !PathHelpers.PathsEqual(dirs.GitDir, dirs.Common))
            {
                return WithMaster(sessionTop, workspaceOption, sessionTop, markerNames);
            }
        }

        var here = PathHelpers.RealPath(cwd);
        var startedInWorkspace = sessionTop is not null && !PathHelpers.PathsEqual(here, sessionTop) && MasterFolders(here, markerNames).Count > 0;
        var workspace = workspaceOption ?? (sessionTop is null || startedInWorkspace ? here : Path.GetDirectoryName(sessionTop)!);
        var folders = MasterFolders(workspace, markerNames).Select(m => m.Folder).ToList();
        if (folders.Count == 0)
        {
            return Refused(workspace, NoMaster(workspace));
        }

        if (folders.Count > 1)
        {
            return Refused(workspace, SeveralMasters(folders, markerNames));
        }

        var master = folders[0];
        return new Resolution(workspace, master, MasterIsCopy: false, [], LayoutOf(master, markerNames));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private Resolution WithMaster(string path, string? workspaceOption, string? sessionTop, IReadOnlyList<string> markerNames)
    {
        if (!PathHelpers.IsDirectory(path))
        {
            return Refused(workspaceOption, $"`{Path.GetFullPath(path)}` does not exist.");
        }

        var master = PathHelpers.RealPath(path);
        var top = _gitWorktrees.TopLevel(master);
        var dirs = top is not null && PathHelpers.PathsEqual(top, master) ? _gitWorktrees.GitDirs(master) : null;
        if (dirs is null)
        {
            return Refused(workspaceOption, $"`{master}` is not the root of a git repository.");
        }

        if (!HoldsMarker(master, markerNames))
        {
            return Refused(workspaceOption, $"`{master}` holds no {string.Join(" or ", markerNames)}, so it is not a master repository or a copy of one.");
        }

        if (LayoutOf(master, markerNames) == WorkspaceLayout.SingleRepo)
        {
            var singleWs = workspaceOption ?? RepositoryWorkspace(dirs.Common);
            if (singleWs is null)
            {
                return Refused(
                    null,
                    $"Cannot tell where the main checkout of `{master}` is: its git directory, `{dirs.Common}`, is kept apart from any checkout. Pass --workspace <path>.",
                    WorkspaceLayout.SingleRepo);
            }

            return new Resolution(singleWs, master, !PathHelpers.PathsEqual(dirs.GitDir, dirs.Common), [], WorkspaceLayout.SingleRepo);
        }

        var sessionCommon = sessionTop is null ? null : _gitWorktrees.GitDirs(sessionTop)?.Common;
        var holdsMaster = sessionTop is not null && PathHelpers.IsSubPathOf(master, sessionTop);
        var workspace = workspaceOption;
        if (workspace is null && sessionTop is not null && sessionCommon is not null && !PathHelpers.PathsEqual(sessionCommon, dirs.Common) && !holdsMaster)
        {
            workspace = Path.GetDirectoryName(sessionTop);
        }

        if (workspace is null)
        {
            workspace = RepositoryWorkspace(dirs.Common);
            if (workspace is null)
            {
                return Refused(
                    null,
                    $"Cannot tell where the main checkout of `{master}` is: its git directory, `{dirs.Common}`, is kept apart from any checkout. Pass --workspace <path>.");
            }
        }

        var folders = MasterFolders(workspace, markerNames);
        var same = folders.Where(f => f.Common is not null && PathHelpers.PathsEqual(f.Common, dirs.Common)).ToList();
        var others = folders.Where(f => f.Common is null || !PathHelpers.PathsEqual(f.Common, dirs.Common)).ToList();

        if (same.Count == 0)
        {
            if (others.Count == 0)
            {
                return Refused(workspace, NoMaster(workspace));
            }

            if (others.Count > 1)
            {
                return Refused(workspace, SeveralMasters(others.Select(f => f.Folder).ToList(), markerNames));
            }

            return Refused(
                workspace,
                $"`{master}` is not a worktree of the master repository in `{workspace}` (`{others[0].Folder}`); they are different git repositories.");
        }

        if (others.Count > 0)
        {
            var all = same.Select(f => f.Folder).Concat(others.Select(f => f.Folder)).ToList();
            return Refused(workspace, SeveralMasters(all, markerNames));
        }

        return new Resolution(workspace, master, !PathHelpers.PathsEqual(dirs.GitDir, dirs.Common), [], WorkspaceLayout.Workspace);
    }

    public string MasterFolderName(string workspace, string master, IReadOnlyList<string>? markers = null)
    {
        var markerNames = markers ?? MarkerConstants.DefaultMarkers;
        var dirs = _gitWorktrees.GitDirs(master);
        var own = PathHelpers.RealPath(master);
        if (dirs is null)
        {
            return Path.GetFileName(own);
        }

        var same = MasterFolders(PathHelpers.RealPath(workspace), markerNames)
            .Where(f => f.Common is not null && PathHelpers.PathsEqual(f.Common, dirs.Common))
            .ToList();
        var main = same.Select(f => f.Folder).FirstOrDefault(folder =>
        {
            var d = _gitWorktrees.GitDirs(folder);
            return d is not null && PathHelpers.PathsEqual(d.GitDir, dirs.Common);
        });
        var named = main ?? same.Select(f => f.Folder).FirstOrDefault(folder => !PathHelpers.PathsEqual(folder, own)) ?? same.Select(f => f.Folder).FirstOrDefault();
        return Path.GetFileName(named ?? own);
    }

    private string? RepositoryWorkspace(string common)
    {
        var configured = _gitRunner.Out(common, ["config", "--get", "core.worktree"]);
        if (!string.IsNullOrEmpty(configured))
        {
            var resolved = Path.IsPathRooted(configured) ? configured : Path.Combine(common, configured);
            return Path.GetDirectoryName(PathHelpers.RealPath(resolved));
        }

        if (_gitRunner.Out(common, ["config", "--get", "--bool", "core.bare"]) == "true")
        {
            return Path.GetDirectoryName(common);
        }

        return Path.GetFileName(common) == ".git" ? Path.GetDirectoryName(Path.GetDirectoryName(common)!) : null;
    }

    private List<(string Folder, string? Common)> MasterFolders(string workspace, IReadOnlyList<string> markerNames)
    {
        string[] entries;
        try
        {
            entries = Directory.GetDirectories(workspace);
        }
        catch
        {
            return [];
        }

        Array.Sort(entries, (a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.Ordinal));
        var result = new List<(string Folder, string? Common)>();
        foreach (var entry in entries)
        {
            var folder = PathHelpers.RealPath(entry);
            if (HoldsMarker(folder, markerNames))
            {
                result.Add((folder, _gitWorktrees.GitDirs(folder)?.Common));
            }
        }

        return result;
    }

    private static bool HoldsMarker(string folder, IReadOnlyList<string> markerNames) =>
        markerNames.Any(m => File.Exists(Path.Combine(folder, m)));

    private WorkspaceLayout LayoutOf(string folder, IReadOnlyList<string> markerNames)
    {
        try
        {
            var file = _markerReader.MarkerFileIn(folder, markerNames);
            return file is not null && _markerReader.ReadMarker(file).Layout == WorkspaceLayout.SingleRepo
                ? WorkspaceLayout.SingleRepo
                : WorkspaceLayout.Workspace;
        }
        catch
        {
            return WorkspaceLayout.Workspace;
        }
    }

    private static string NoMaster(string workspace) =>
        $"No master repository found in `{workspace}`. An engineer needs to run the hoshin setup skill first.";

    private static string SeveralMasters(List<string> folders, IReadOnlyList<string> markerNames) =>
        $"Found more than one master repository ({string.Join(", ", folders.Select(f => $"`{f}`"))}). A workspace has exactly one. Remove the extra {string.Join(" or ", markerNames.Select(m => $"`{m}`"))} files.";

    private static Resolution Refused(string? workspace, string problem, WorkspaceLayout layout = WorkspaceLayout.Workspace) =>
        new(workspace, null, false, [problem], layout);
}
