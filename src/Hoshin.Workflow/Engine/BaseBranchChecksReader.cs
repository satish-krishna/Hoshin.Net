using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Reads blocking <c>build.finish</c> checks strictly from the base branch ref (<c>git show &lt;base&gt;:&lt;path&gt;</c>),
/// never from the working tree, combining <c>.hoshin.json</c>, <c>intent/&lt;system&gt;/hooks.json</c> (via
/// <see cref="HookConfigService"/>), and any committed per-change <c>hooks.jsonc</c>/<c>hooks.json</c> (T05 / S6).
/// </summary>
public static class BaseBranchChecksReader
{
    public static List<string[]> Read(WorkflowRepo repo, string baseRef, string? systemName)
    {
        var checks = new List<string[]>();
        var effectiveRef = string.IsNullOrWhiteSpace(baseRef) ? "main" : baseRef;
        var resolvedSystem = ResolveSystemName(repo, systemName);

        var markerText = ShowAtRef(repo, effectiveRef, MarkerConstants.FileName);
        var systemText = string.IsNullOrWhiteSpace(resolvedSystem)
            ? null
            : ShowAtRef(repo, effectiveRef, $"intent/{resolvedSystem}/hooks.json");

        if (markerText is not null || systemText is not null)
        {
            var config = HookConfigService.ParseConfig(markerText, systemText, resolvedSystem ?? "");
            foreach (var hook in config.Hooks)
            {
                if (hook.Event == "build.finish" && hook.Blocking && hook.Run.Count > 0)
                {
                    checks.Add(hook.Run.ToArray());
                }
            }
        }

        ReadPerChangeHooksAtRef(repo, effectiveRef, checks);
        return checks;
    }

    private static void ReadPerChangeHooksAtRef(WorkflowRepo repo, string baseRef, List<string[]> checks)
    {
        foreach (var fileName in new[] { "hooks.jsonc", "hooks.json" })
        {
            var relPath = Path.GetRelativePath(repo.Root, Path.Combine(repo.ChangeFolder, fileName)).Replace('\\', '/');
            var text = ShowAtRef(repo, baseRef, relPath);
            if (text is null)
            {
                continue;
            }

            if (!Jsonc.TryParse(text, out var value, out var error))
            {
                throw new CouldNotRunException($"{fileName} on {baseRef} is not valid JSONC: {error}");
            }

            if (value is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("hooks", out var hooks))
            {
                continue;
            }

            ExtractBlockingBuildFinishChecks(hooks, checks);
        }
    }

    private static void ExtractBlockingBuildFinishChecks(JsonElement hooks, List<string[]> checks)
    {
        if (hooks.ValueKind == JsonValueKind.Array)
        {
            foreach (var hook in hooks.EnumerateArray())
            {
                if (Jsonc.String(hook, "event") == "build.finish" && IsBlocking(hook) && Jsonc.Argv(hook, "run") is { } run)
                {
                    checks.Add(run);
                }
            }

            return;
        }

        if (hooks.ValueKind == JsonValueKind.Object &&
            hooks.TryGetProperty("build.finish", out var buildFinish) &&
            buildFinish.ValueKind == JsonValueKind.Array)
        {
            foreach (var hook in buildFinish.EnumerateArray())
            {
                if (IsBlocking(hook) && Jsonc.Argv(hook, "run") is { } run)
                {
                    checks.Add(run);
                }
            }
        }
    }

    private static bool IsBlocking(JsonElement hook) =>
        !hook.TryGetProperty("blocking", out var b) || b.ValueKind != JsonValueKind.False;

    private static string? ShowAtRef(WorkflowRepo repo, string gitRef, string repoRelativePath)
    {
        var result = repo.Git("show", $"{gitRef}:{repoRelativePath}");
        return result.Ok ? result.Output : null;
    }

    private static string? ResolveSystemName(WorkflowRepo repo, string? explicitSystem)
    {
        var rel = Path.GetRelativePath(repo.Root, repo.ChangeFolder).Replace('\\', '/');
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("intent", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }

        return string.IsNullOrWhiteSpace(explicitSystem) ? null : explicitSystem;
    }
}
