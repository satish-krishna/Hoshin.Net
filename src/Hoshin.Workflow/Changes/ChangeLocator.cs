using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Changes;

public sealed record LocatedChange(string Master, string System);

/// <summary>Locates changes, copies, and system repositories across a workspace.</summary>
public sealed class ChangeLocator(IGitClient? git = null, AppService? apps = null)
{
    public static ChangeLocator Default { get; } = new();

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly AppService _apps = apps ?? new AppService(git);

    public static string CopiesFolder(string workspace) => Path.Combine(workspace, ".hoshin", "copies");

    public List<string> SystemRepos(string master, string workspace, string system)
    {
        var marker = MarkerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName));
        if (!marker.Systems.TryGetValue(system, out var entry))
        {
            throw new CouldNotRunException($"the marker lists no system {system}");
        }

        if (_apps.IsSingleRepo(master))
        {
            return entry.Apps.Keys.ToList();
        }

        if (entry.Repos.Count > 0)
        {
            return [.. entry.Repos];
        }

        return Directory.GetDirectories(workspace)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) &&
                !name!.StartsWith('.') &&
                (Directory.Exists(Path.Combine(workspace, name, ".git")) || File.Exists(Path.Combine(workspace, name, ".git"))) &&
                !File.Exists(Path.Combine(workspace, name, MarkerConstants.FileName)))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public List<string> ChangeHolders(string master, string workspace, string change)
    {
        var own = Path.Combine(CopiesFolder(workspace), change);
        var holders = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var candidate in new[] { own, master })
        {
            if (PathHelpers.IsDirectory(candidate))
            {
                var full = PathHelpers.RealPath(candidate);
                if (seen.Add(full))
                {
                    holders.Add(full);
                }
            }
        }

        return holders;
    }

    public List<string> SystemsHolding(string master, string change)
    {
        var intent = Path.Combine(master, "intent");
        if (!PathHelpers.IsDirectory(intent))
        {
            return [];
        }

        return Directory.GetDirectories(intent)
            .Select(Path.GetFileName)
            .Where(s => !string.IsNullOrEmpty(s) && PathHelpers.IsDirectory(Path.Combine(intent, s!, change)))
            .Select(s => s!)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    public LocatedChange FindChange(string master, string workspace, string change, string? system = null)
    {
        var candidates = ChangeHolders(master, workspace, change);
        foreach (var m in candidates)
        {
            var systems = SystemsHolding(m, change);
            if (system is not null)
            {
                systems = systems.Where(s => s == system).ToList();
            }

            if (systems.Count == 1)
            {
                return new LocatedChange(m, systems[0]);
            }

            if (systems.Count > 1)
            {
                throw new CouldNotRunException($"{change} is a change of several systems ({string.Join(", ", systems)}); pass --system");
            }
        }

        throw new CouldNotRunException(
            $"no change {change}{(system is not null ? $" in system {system}" : "")} in {master}{(candidates.Count > 1 ? " or its copy" : "")}");
    }

    public LocatedChange FindChangeLedger(string master, string workspace, string change, string? system = null)
    {
        try
        {
            return FindChange(master, workspace, change, system);
        }
        catch (CouldNotRunException)
        {
            var names = MarkerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName)).Systems.Keys
                .Where(s => system is null || s == system)
                .ToList();
            foreach (var holder in ChangeHolders(master, workspace, change))
            {
                var held = names.Where(s =>
                {
                    try
                    {
                        var folder = _git.GitPath(holder, Path.Combine("hoshin", s, change), common: true);
                        return Directory.Exists(folder);
                    }
                    catch
                    {
                        return false;
                    }
                }).ToList();

                if (held.Count == 1)
                {
                    return new LocatedChange(holder, held[0]);
                }

                if (held.Count > 1)
                {
                    throw new CouldNotRunException($"{change} is a change of several systems ({string.Join(", ", held)}); pass --system");
                }
            }

            throw;
        }
    }
}
