using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Workspace;

public static class MarkerConstants
{
    public const string FileName = ".hoshin.json";
    public const string LegacyFileName = ".ai-sdlc.json";
    public static readonly string[] DefaultMarkers = [FileName];
    public static readonly string[] AnyMarkers = [FileName, LegacyFileName];
}

public enum WorkspaceLayout
{
    Workspace,
    SingleRepo,
}

public static class WorkspaceLayoutExtensions
{
    public static string ToWireName(this WorkspaceLayout layout) =>
        layout == WorkspaceLayout.SingleRepo ? "single-repo" : "workspace";
}

public sealed record MarkerSystem(List<string> Repos, Dictionary<string, string> Apps);

public sealed record Marker(WorkspaceLayout Layout, Dictionary<string, MarkerSystem> Systems);

/// <summary>Finds, reads, and validates <c>.hoshin.json</c> markers.</summary>
public static class MarkerReader
{
    private static readonly Regex NameRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex DrivePrefix = new(@"^[A-Za-z]:", RegexOptions.Compiled);
    public static readonly Regex CommitScopeRegex = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly HashSet<string> MarkerFields = ["version", "layout", "systems", "hooks", "gateRetries"];
    private static readonly HashSet<string> SystemFields = ["repos", "apps", "commitScope", "naming"];

    public static string? MarkerFileIn(string folder, IReadOnlyList<string>? names = null)
    {
        foreach (var n in names ?? MarkerConstants.DefaultMarkers)
        {
            var candidate = Path.Combine(folder, n);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string? NormalizeFolder(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? NormalizeFolder(value.GetString()) : null;

    public static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        if (folder.Contains('\\') || folder.StartsWith('/') || DrivePrefix.IsMatch(folder))
        {
            return null;
        }

        var parts = folder.Split('/').Where(p => p.Length > 0 && p != ".").ToList();
        if (parts.Contains(".."))
        {
            return null;
        }

        return parts.Count == 0 ? "." : string.Join('/', parts);
    }

    private static bool Within(string inner, string outer) =>
        inner == outer || inner.StartsWith(outer + "/", StringComparison.Ordinal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public static List<string> MarkerProblems(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("systems", out var systemsProp) || systemsProp.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var problems = new List<string>();
        if (!raw.TryGetProperty("version", out var versionProp) || versionProp.ValueKind != JsonValueKind.Number || !versionProp.TryGetInt32(out var ver) || ver != 1)
        {
            var shown = raw.TryGetProperty("version", out var vp) ? vp.GetRawText() : "missing";
            problems.Add($"version must be 1, not {shown}");
        }

        foreach (var prop in raw.EnumerateObject())
        {
            if (!MarkerFields.Contains(prop.Name))
            {
                problems.Add($"unknown field {prop.Name}");
            }
        }

        foreach (var sysProp in systemsProp.EnumerateObject())
        {
            var name = sysProp.Name;
            var sys = sysProp.Value;
            if (sys.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"system {name}: must be an object");
                continue;
            }

            foreach (var p in sys.EnumerateObject())
            {
                if (!SystemFields.Contains(p.Name))
                {
                    problems.Add($"system {name}: unknown field {p.Name}");
                }
            }

            if (sys.TryGetProperty("commitScope", out var scopeProp))
            {
                if (scopeProp.ValueKind != JsonValueKind.String || !CommitScopeRegex.IsMatch(scopeProp.GetString()!))
                {
                    problems.Add($"system {name}: commitScope must be lowercase kebab case, like \"billing-api\", not {scopeProp.GetRawText()}");
                }
            }
        }

        var layoutStr = "workspace";
        if (raw.TryGetProperty("layout", out var layoutProp))
        {
            if (layoutProp.ValueKind != JsonValueKind.String || (layoutProp.GetString() != "workspace" && layoutProp.GetString() != "single-repo"))
            {
                problems.Add($"layout must be \"workspace\" or \"single-repo\", not {layoutProp.GetRawText()}");
                return problems;
            }

            layoutStr = layoutProp.GetString()!;
        }

        var systems = systemsProp.EnumerateObject().ToList();
        if (layoutStr == "workspace")
        {
            foreach (var sys in systems)
            {
                if (sys.Value.ValueKind == JsonValueKind.Object && sys.Value.TryGetProperty("apps", out _))
                {
                    problems.Add($"system {sys.Name}: apps is only for the \"single-repo\" layout; the default layout lists repos");
                }
            }

            return problems;
        }

        if (systems.Count != 1)
        {
            problems.Add($"the \"single-repo\" layout needs exactly one system, found {systems.Count}");
        }

        foreach (var sys in systems)
        {
            if (sys.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var at = $"system {sys.Name}";
            var body = sys.Value;
            if (body.TryGetProperty("repos", out _))
            {
                problems.Add($"{at}: repos is not allowed in the \"single-repo\" layout; list apps instead");
            }

            if (!body.TryGetProperty("apps", out var appsProp) || appsProp.ValueKind != JsonValueKind.Object || !appsProp.EnumerateObject().Any())
            {
                problems.Add($"{at}: apps must map at least one app name to a folder");
                continue;
            }

            var folders = new List<(string App, string Folder)>();
            foreach (var appProp in appsProp.EnumerateObject())
            {
                var app = appProp.Name;
                if (app == "master")
                {
                    problems.Add($"{at}: app master: the name master is reserved for the master repository");
                }
                else if (!NameRegex.IsMatch(app))
                {
                    problems.Add($"{at}: app \"{app}\": a name must start with a letter or a digit and hold only letters, digits, \".\", \"_\" and \"-\"");
                }

                var normal = NormalizeFolder(appProp.Value);
                if (normal is null)
                {
                    problems.Add($"{at}: app {app}: the folder {appProp.Value.GetRawText()} must be a relative path inside the repository, with \"/\" separators and no \"..\"");
                }
                else
                {
                    folders.Add((app, normal));
                }
            }

            if (folders.Count > 1)
            {
                foreach (var (app, folder) in folders)
                {
                    if (folder == ".")
                    {
                        problems.Add($"{at}: app {app}: the folder \".\" (the whole repository) is allowed only when it is the system's only app");
                    }
                }
            }

            foreach (var (app, folder) in folders)
            {
                if (folder == ".")
                {
                    continue;
                }

                foreach (var (other, otherFolder) in folders)
                {
                    if (other != app && otherFolder != "." && Within(folder, otherFolder))
                    {
                        problems.Add($"{at}: app {app}: the folder {folder} is {(folder == otherFolder ? "the same as" : "inside")} the folder {otherFolder} of app {other}");
                    }
                }
            }
        }

        return problems;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public static Marker ReadMarker(string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            throw new CouldNotRunException($"{path}: cannot read the marker: {e.Message}");
        }

        using (doc)
        {
            var raw = doc.RootElement;
            if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("systems", out var listed) || listed.ValueKind != JsonValueKind.Object)
            {
                throw new CouldNotRunException($"{path}: the marker lists no systems");
            }

            var layout = raw.TryGetProperty("layout", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() == "single-repo"
                ? WorkspaceLayout.SingleRepo
                : WorkspaceLayout.Workspace;

            var systems = new Dictionary<string, MarkerSystem>(StringComparer.Ordinal);
            foreach (var entry in listed.EnumerateObject())
            {
                var repos = new List<string>();
                var apps = new Dictionary<string, string>(StringComparer.Ordinal);
                if (entry.Value.ValueKind == JsonValueKind.Object)
                {
                    if (entry.Value.TryGetProperty("repos", out var rProp) && rProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in rProp.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String)
                            {
                                repos.Add(item.GetString()!);
                            }
                        }
                    }

                    if (layout == WorkspaceLayout.SingleRepo && entry.Value.TryGetProperty("apps", out var aProp) && aProp.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var appEntry in aProp.EnumerateObject())
                        {
                            var normal = NormalizeFolder(appEntry.Value);
                            if (normal is not null)
                            {
                                apps[appEntry.Name] = normal;
                            }
                        }
                    }
                }

                systems[entry.Name] = new MarkerSystem(repos, apps);
            }

            return new Marker(layout, systems);
        }
    }

    public static Marker? FindMarker(string file)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(file));
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, MarkerConstants.FileName);
            if (File.Exists(candidate))
            {
                return ReadMarker(candidate);
            }

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir)
            {
                return null;
            }

            dir = parent;
        }

        return null;
    }

    public static IReadOnlyList<string>? AllowedPlanRepos(Marker? marker, string system)
    {
        if (marker is null || !marker.Systems.TryGetValue(system, out var entry))
        {
            return null;
        }

        return marker.Layout == WorkspaceLayout.SingleRepo
            ? entry.Apps.Keys.ToList()
            : entry.Repos;
    }
}
