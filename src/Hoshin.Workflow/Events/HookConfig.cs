using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Events;

public sealed record HookDefinition(
    string Id,
    string Event,
    string When,
    List<string> Run,
    List<string>? Repos,
    List<string>? Apps,
    int Timeout,
    bool Blocking,
    string OnFailure,
    string Layer);

public sealed record HookConfig(
    string? Revision,
    List<HookDefinition> Hooks,
    int GateRetries,
    List<string> Problems);

/// <summary>Parses and merges base-branch hook configurations from <c>.hoshin.json</c> and <c>intent/&lt;system&gt;/hooks.json</c>.</summary>
public sealed class HookConfigService(IGitClient? git = null)
{
    public static HookConfigService Default { get; } = new();

    public static readonly string[] Events =
        ["change.start", "doc.accept", "build.start", "task.done", "build.finish", "review.finish", "change.done"];

    public static readonly string[] PerRepositoryEvents = ["build.start", "task.done", "build.finish"];

    public static readonly string[] DeprecatedHookEvents = ["change.start", "doc.accept", "task.done", "review.finish"];

    private static readonly Dictionary<string, string> DefaultOnFailure = new(StringComparer.Ordinal)
    {
        ["change.start"] = "stop",
        ["doc.accept"] = "stop",
        ["build.start"] = "stop",
        ["task.done"] = "fix",
        ["build.finish"] = "fix",
        ["review.finish"] = "stop",
        ["change.done"] = "stop",
    };

    private static readonly HashSet<string> HookFields = ["id", "when", "run", "repos", "apps", "timeout", "blocking", "onFailure"];
    private static readonly Regex HookIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;

    public HookConfig ReadFromBaseBranch(string master, string system)
    {
        var baseBranch = _git.BaseBranch(master);
        var revision = baseBranch is null ? null : _git.BaseRevision(master, baseBranch);
        if (revision is null)
        {
            return new HookConfig(null, [], 3, []);
        }

        var markerText = _git.ShowFile(master, revision, MarkerConstants.FileName);
        var systemText = _git.ShowFile(master, revision, $"intent/{system}/hooks.json");
        var parsed = ParseConfig(markerText, systemText, system);
        return parsed with { Revision = revision };
    }

    public static HookConfig ParseConfig(string? markerText, string? systemText, string system)
    {
        var problems = new List<string>();
        JsonElement? markerObj = null;
        if (markerText is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(markerText);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    markerObj = doc.RootElement.Clone();
                }
                else
                {
                    problems.Add(".hoshin.json: the marker is not a JSON object");
                }
            }
            catch (Exception e)
            {
                problems.Add($".hoshin.json: the marker is not JSON: {e.Message}");
            }
        }

        var systemFile = $"intent/{system}/hooks.json";
        JsonElement? systemHooks = null;
        if (systemText is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(systemText);
                var raw = doc.RootElement;
                if (raw.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{systemFile}: not a JSON object");
                }
                else
                {
                    foreach (var prop in raw.EnumerateObject())
                    {
                        if (prop.Name is not ("version" or "hooks"))
                        {
                            problems.Add($"{systemFile}: unknown field {prop.Name}");
                        }
                    }

                    if (!raw.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var ver) || ver != 1)
                    {
                        problems.Add($"{systemFile}: version must be 1");
                    }

                    if (raw.TryGetProperty("hooks", out var h))
                    {
                        systemHooks = h.Clone();
                    }
                }
            }
            catch (Exception e)
            {
                problems.Add($"{systemFile}: not JSON: {e.Message}");
            }
        }

        var layoutApps = LayoutAppsOf(markerObj, system);
        JsonElement? globalHooks = markerObj is { } mo && mo.TryGetProperty("hooks", out var mh) ? mh : null;

        var (globalList, globalProblems) = ParseHooks(globalHooks, "global", MarkerConstants.FileName, layoutApps, system);
        var (ownList, ownProblems) = ParseHooks(systemHooks, "system", systemFile, layoutApps, system);
        var (mergedList, mergeProblems) = MergeLayers(globalList, ownList, systemFile);
        var gateRetries = ReadGateRetries(markerObj, problems);

        problems.AddRange(globalProblems);
        problems.AddRange(ownProblems);
        problems.AddRange(mergeProblems);
        return new HookConfig(null, mergedList, gateRetries, problems);
    }

    public static List<string> Deprecations(IEnumerable<HookDefinition> hooks) =>
        hooks
            .Where(h => DeprecatedHookEvents.Contains(h.Event))
            .Select(h => $"hook {h.Id} runs on {h.Event}: hooks on {h.Event} are deprecated and the next major release removes them; the event itself stays")
            .ToList();

    public static List<HookDefinition> HooksFor(HookConfig config, string eventName, string when, string repo) =>
        config.Hooks
            .Where(h => h.Event == eventName && h.When == when &&
                (h.Repos is null || h.Repos.Contains(repo)) &&
                (h.Apps is null || h.Apps.Contains(repo)))
            .ToList();

    private static List<string>? LayoutAppsOf(JsonElement? marker, string system)
    {
        if (marker is not { ValueKind: JsonValueKind.Object } m ||
            !m.TryGetProperty("layout", out var lp) ||
            lp.ValueKind != JsonValueKind.String ||
            lp.GetString() != "single-repo")
        {
            return null;
        }

        if (!m.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        static List<string> NamesOf(JsonElement s) =>
            s.ValueKind == JsonValueKind.Object && s.TryGetProperty("apps", out var a) && a.ValueKind == JsonValueKind.Object
                ? a.EnumerateObject().Select(p => p.Name).ToList()
                : [];

        if (systems.TryGetProperty(system, out var sys))
        {
            return NamesOf(sys);
        }

        return systems.EnumerateObject().SelectMany(p => NamesOf(p.Value)).ToList();
    }

    private static (List<HookDefinition> Hooks, List<string> Problems) ParseHooks(
        JsonElement? value,
        string layer,
        string where,
        List<string>? layoutApps,
        string system)
    {
        var hooks = new List<HookDefinition>();
        var problems = new List<string>();
        if (value is null)
        {
            return (hooks, problems);
        }

        var v = value.Value;
        if (v.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{where}: hooks must be an object of events");
            return (hooks, problems);
        }

        foreach (var evProp in v.EnumerateObject())
        {
            var ev = evProp.Name;
            if (!Events.Contains(ev))
            {
                problems.Add($"{where}: {ev} is not an event; the events are {string.Join(", ", Events)}");
                continue;
            }

            if (evProp.Value.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{where}: hooks.{ev} must be a list");
                continue;
            }

            var i = 0;
            foreach (var entry in evProp.Value.EnumerateArray())
            {
                var at = $"{where}: hooks.{ev}[{i}]";
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    problems.Add($"{at} must be an object");
                    i++;
                    continue;
                }

                foreach (var p in entry.EnumerateObject())
                {
                    if (!HookFields.Contains(p.Name))
                    {
                        problems.Add($"{at}: unknown field {p.Name}");
                    }
                }

                string? name = null;
                if (entry.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String && HookIdRegex.IsMatch(idProp.GetString()!))
                {
                    name = idProp.GetString()!;
                }
                else
                {
                    problems.Add($"{at}: id must be letters, digits, dots, hyphens or underscores, starting with a letter or digit");
                }

                var when = "before";
                if (entry.TryGetProperty("when", out var whenProp))
                {
                    if (whenProp.ValueKind == JsonValueKind.String && whenProp.GetString() is "before" or "after")
                    {
                        when = whenProp.GetString()!;
                    }
                    else
                    {
                        problems.Add($"{at}: when must be before or after");
                    }
                }

                List<string>? run = null;
                if (entry.TryGetProperty("run", out var runProp) && runProp.ValueKind == JsonValueKind.Array && runProp.GetArrayLength() > 0)
                {
                    var items = runProp.EnumerateArray().ToList();
                    if (items.All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length > 0))
                    {
                        run = items.Select(x => x.GetString()!).ToList();
                    }
                    else
                    {
                        problems.Add($"{at}: run must be a list of the program and its arguments, as non-empty strings");
                    }
                }
                else
                {
                    problems.Add($"{at}: run must be a list of the program and its arguments, as non-empty strings");
                }

                List<string>? repos = null;
                var hasRepos = entry.TryGetProperty("repos", out var reposProp);
                if (hasRepos)
                {
                    if (reposProp.ValueKind == JsonValueKind.Array && reposProp.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length > 0))
                    {
                        repos = reposProp.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }
                    else
                    {
                        problems.Add($"{at}: repos must be a list of repository names");
                    }
                }

                List<string>? apps = null;
                var hasApps = entry.TryGetProperty("apps", out var appsProp);
                if (hasApps)
                {
                    if (appsProp.ValueKind == JsonValueKind.Array && appsProp.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Length > 0))
                    {
                        apps = appsProp.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }
                    else
                    {
                        problems.Add($"{at}: apps must be a list of app names");
                    }
                }

                var hookId = name ?? $"hooks.{ev}[{i}]";
                if (layoutApps is not null && hasRepos)
                {
                    problems.Add($"{where}: hook {hookId} uses repos; use apps in the single-repo layout");
                }

                if (layoutApps is null && hasApps)
                {
                    problems.Add($"{where}: hook {hookId} uses apps, which only the single-repo layout has");
                }

                if (layoutApps is not null && hasApps && appsProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in appsProp.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } a && !layoutApps.Contains(a))
                        {
                            problems.Add($"{where}: hook {hookId}: apps names {a}, which is not an app of system {system}");
                        }
                    }
                }

                var timeout = 600;
                if (entry.TryGetProperty("timeout", out var tProp))
                {
                    if (tProp.ValueKind == JsonValueKind.Number && tProp.TryGetInt32(out var tv) && tv is >= 1 and <= 86400)
                    {
                        timeout = tv;
                    }
                    else
                    {
                        problems.Add($"{at}: timeout must be a whole number of seconds from 1 to 86400");
                    }
                }

                bool? blocking = null;
                if (entry.TryGetProperty("blocking", out var bProp))
                {
                    if (bProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        blocking = bProp.GetBoolean();
                    }
                    else
                    {
                        problems.Add($"{at}: blocking must be true or false");
                    }
                }

                if (blocking == true && when == "after")
                {
                    problems.Add($"{at}: an after hook cannot block; it runs once the event's own step is done");
                }

                string? onFailure = null;
                if (entry.TryGetProperty("onFailure", out var ofProp))
                {
                    if (ofProp.ValueKind == JsonValueKind.String && ofProp.GetString() is "fix" or "stop")
                    {
                        onFailure = ofProp.GetString()!;
                    }
                    else
                    {
                        problems.Add($"{at}: onFailure must be fix or stop");
                    }
                }

                if (name is not null && run is not null)
                {
                    hooks.Add(new HookDefinition(
                        name,
                        ev,
                        when,
                        run,
                        repos,
                        apps,
                        timeout,
                        when == "after" ? false : blocking != false,
                        onFailure ?? DefaultOnFailure[ev],
                        layer));
                }

                i++;
            }
        }

        return (hooks, problems);
    }

    private static (List<HookDefinition> Hooks, List<string> Problems) MergeLayers(
        List<HookDefinition> global,
        List<HookDefinition> system,
        string where)
    {
        var seen = new HashSet<string>(global.Select(h => h.Id), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var h in system)
        {
            if (!seen.Add(h.Id))
            {
                problems.Add($"{where}: hook id {h.Id} is already used; hook ids are unique across .hoshin.json and every hooks.json, and a system's hooks only add to the global ones");
            }
        }

        var globalSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in global)
        {
            if (!globalSeen.Add(h.Id))
            {
                problems.Add($".hoshin.json: hook id {h.Id} is used twice");
            }
        }

        return ([.. global, .. system], problems);
    }

    private static int ReadGateRetries(JsonElement? marker, List<string> problems)
    {
        if (marker is not { ValueKind: JsonValueKind.Object } m || !m.TryGetProperty("gateRetries", out var v))
        {
            return 3;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n is >= 0 and <= 20)
        {
            return n;
        }

        problems.Add(".hoshin.json: gateRetries must be a whole number from 0 to 20");
        return 3;
    }
}
