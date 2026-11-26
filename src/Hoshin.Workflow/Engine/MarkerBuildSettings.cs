using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>Optional build configuration read from <c>.hoshin.json</c> (<c>testPatterns</c>, <c>env.allow</c>, and <c>build</c>).</summary>
internal sealed record MarkerBuildSettings(
    string[]? TestPatterns,
    string[]? EnvAllow,
    int TestTimeoutSeconds,
    int CheckTimeoutSeconds,
    int MaxAttempts,
    string[]? LinkDirs = null)
{
    public const int DefaultTestTimeoutSeconds = 300;
    public const int DefaultCheckTimeoutSeconds = 600;

    public static MarkerBuildSettings Empty { get; } = new(
        null,
        null,
        DefaultTestTimeoutSeconds,
        DefaultCheckTimeoutSeconds,
        WorkflowEngine.MaxAttempts,
        null);

    public static MarkerBuildSettings Read(string repoRoot, IFileSystem fs)
    {
        var markerPath = Path.Combine(repoRoot, MarkerConstants.FileName);
        if (!fs.FileExists(markerPath) ||
            !Jsonc.TryParse(fs.ReadAllText(markerPath), out var raw, out _) ||
            raw is not { ValueKind: JsonValueKind.Object } obj)
        {
            return Empty;
        }

        var patterns = Jsonc.Strings(obj, "testPatterns");
        string[]? envAllow = null;
        if (obj.TryGetProperty("env", out var envProp) && envProp.ValueKind == JsonValueKind.Object)
        {
            var allow = Jsonc.Strings(envProp, "allow").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (allow.Length > 0)
            {
                envAllow = allow;
            }
        }

        var testTimeout = DefaultTestTimeoutSeconds;
        var checkTimeout = DefaultCheckTimeoutSeconds;
        var maxAttempts = WorkflowEngine.MaxAttempts;
        string[]? linkDirs = null;
        if (obj.TryGetProperty("build", out var buildProp) && buildProp.ValueKind == JsonValueKind.Object)
        {
            testTimeout = PositiveInt(buildProp, "testTimeoutSeconds", testTimeout);
            checkTimeout = PositiveInt(buildProp, "checkTimeoutSeconds", checkTimeout);
            maxAttempts = PositiveInt(buildProp, "maxAttempts", maxAttempts);
            var configuredLinks = Jsonc.Strings(buildProp, "linkDirs").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            if (configuredLinks.Length > 0)
            {
                linkDirs = configuredLinks;
            }
        }

        return new MarkerBuildSettings(
            patterns.Count > 0 ? patterns.ToArray() : null,
            envAllow,
            testTimeout,
            checkTimeout,
            maxAttempts,
            linkDirs);
    }

    private static int PositiveInt(JsonElement obj, string name, int fallback) =>
        obj.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var val) && val >= 1
            ? val
            : fallback;
}
