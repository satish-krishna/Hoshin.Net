using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record StateOptions(string? Master = null, string? Workspace = null, string? System = null, string? Change = null, bool Open = false);

public sealed record PlacedChange(string Change, string Top, bool InCopy);

public sealed record Placement(
    string System,
    string Master,
    string Workspace,
    bool MasterIsCopy,
    List<string> Copies,
    Dictionary<string, SystemRead> Reads,
    List<PlacedChange> Placed);

/// <summary>Discovers changes of a system across master and worktree copies, and resolves which repository holds each.</summary>
public sealed class ChangePlacer(
    IGitClient git,
    IWorkspaceResolver resolver,
    ChangeLocator locator,
    MarkerReader markerReader)
{
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ChangeLocator _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public Placement PlaceChanges(string cwd, StateOptions options, Dictionary<string, GitDirsInfo?>? dirsCache = null)
    {
        dirsCache ??= new Dictionary<string, GitDirsInfo?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var resolution = _resolver.Resolve(cwd, new ResolveOptions(options.Master, options.Workspace));
        if (resolution.Master is null || resolution.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', resolution.Problems));
        }

        var master = resolution.Master;
        var workspace = resolution.Workspace;
        var systems = _markerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName)).Systems.Keys.ToList();
        string system;
        if (options.System is not null)
        {
            if (!systems.Contains(options.System))
            {
                throw new CouldNotRunException($"the marker lists no system {options.System}");
            }

            system = options.System;
        }
        else if (options.Change is not null)
        {
            system = _locator.FindChange(master, workspace, options.Change).System;
        }
        else if (systems.Count == 1)
        {
            system = systems[0];
        }
        else
        {
            throw new CouldNotRunException($"the marker lists {(systems.Count == 0 ? "no systems" : $"several systems ({string.Join(", ", systems)})")}; pass --system <system>");
        }

        var copies = CopiesOf(workspace, master, dirsCache);
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var repos = new List<string>();
        var seenRepos = new HashSet<string>(cmp);
        foreach (var r in new[] { master }.Concat(copies))
        {
            if (seenRepos.Add(r))
            {
                repos.Add(r);
            }
        }

        var reads = new Dictionary<string, SystemRead>(cmp);
        var holds = new Dictionary<string, HashSet<string>>(cmp);
        foreach (var top in repos)
        {
            var sr = new SystemRead(top, system, _git);
            reads[top] = sr;
            holds[top] = ChangesIn(top, system, sr);
        }

        var names = options.Change is not null
            ? [options.Change]
            : holds.Values.SelectMany(s => s).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var placed = new List<PlacedChange>();
        foreach (var change in names)
        {
            var order = _locator.ChangeHolders(master, workspace, change)
                .Concat(repos)
                .Distinct(cmp)
                .Where(top => holds.ContainsKey(top))
                .ToList();
            var top = order.FirstOrDefault(t => holds[t].Contains(change));
            if (top is null)
            {
                throw new CouldNotRunException($"no change {change} in system {system} in {master} or a copy of it");
            }

            var inCopy = PathHelpers.PathsEqual(top, master) ? resolution.MasterIsCopy : true;
            placed.Add(new PlacedChange(change, top, inCopy));
        }

        return new Placement(system, master, workspace, resolution.MasterIsCopy, copies, reads, placed);
    }

    public static (string? Title, string Track) TitleAndTrack(string? working, string? head, string file, string headName)
    {
        string? FromIntent(string field)
        {
            var val = working is null ? null : FrontMatter.TryGetScalar(working, file, field);
            return val ?? (head is null ? null : FrontMatter.TryGetScalar(head, headName, field));
        }

        var title = FromIntent("title");
        var track = FromIntent("track") == "fix" ? "fix" : "full";
        return (title, track);
    }

    private GitDirsInfo? DirsOf(Dictionary<string, GitDirsInfo?> cache, string dir)
    {
        if (!cache.TryGetValue(dir, out var info))
        {
            info = _git.GitDirs(dir);
            cache[dir] = info;
        }

        return info;
    }

    private static HashSet<string> ChangesIn(string top, string system, SystemRead read)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var baseDir = Path.Combine(top, "intent", system);
        if (PathHelpers.IsDirectory(baseDir))
        {
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                if (File.Exists(Path.Combine(dir, "intent.md")))
                {
                    names.Add(Path.GetFileName(dir));
                }
            }
        }

        var prefix = $"intent/{system}/";
        foreach (var path in read.HeadBlobs.Keys)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = path[prefix.Length..].Split('/');
            if (rest.Length == 2 && rest[1] == "intent.md")
            {
                names.Add(rest[0]);
            }
        }

        return names;
    }

    private List<string> CopiesOf(string workspace, string master, Dictionary<string, GitDirsInfo?> dirsCache)
    {
        var common = DirsOf(dirsCache, master)?.Common;
        var folder = ChangeLocator.CopiesFolder(workspace);
        if (common is null || !PathHelpers.IsDirectory(folder))
        {
            return [];
        }

        var copies = new List<string>();
        foreach (var entry in Directory.GetDirectories(folder).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var dir = PathHelpers.RealPath(entry);
            if (!PathHelpers.IsDirectory(dir))
            {
                continue;
            }

            var prefix = _git.Out(dir, ["rev-parse", "--show-prefix"]);
            var d = DirsOf(dirsCache, dir);
            if (prefix == "" && d is not null && PathHelpers.PathsEqual(d.Common, common))
            {
                copies.Add(dir);
            }
        }

        return copies;
    }
}
