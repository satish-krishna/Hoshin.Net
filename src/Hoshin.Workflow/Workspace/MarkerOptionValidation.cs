using System.Text.Json;

namespace Hoshin.Workflow.Workspace;

/// <summary>Validates optional <c>.hoshin.json</c> sections (<c>env</c>, <c>build</c>, and <c>testPatterns</c>).</summary>
internal static class MarkerOptionValidation
{
    public static void Validate(JsonElement raw, List<string> problems)
    {
        ValidateEnvConfig(raw, problems);
        ValidateBuildConfig(raw, problems);
        ValidateTestPatterns(raw, problems);
    }

    private static void ValidateEnvConfig(JsonElement raw, List<string> problems)
    {
        if (!raw.TryGetProperty("env", out var envProp))
        {
            return;
        }

        if (envProp.ValueKind != JsonValueKind.Object)
        {
            problems.Add("env must be an object");
            return;
        }

        foreach (var prop in envProp.EnumerateObject())
        {
            if (prop.Name != "allow")
            {
                problems.Add($"env: unknown field {prop.Name}");
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Array ||
                prop.Value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
            {
                problems.Add("env.allow must be an array of non-empty variable names");
            }
        }
    }

    private static void ValidateBuildConfig(JsonElement raw, List<string> problems)
    {
        if (!raw.TryGetProperty("build", out var buildProp))
        {
            return;
        }

        if (buildProp.ValueKind != JsonValueKind.Object)
        {
            problems.Add("build must be an object");
            return;
        }

        foreach (var prop in buildProp.EnumerateObject())
        {
            if (prop.Name is not ("testTimeoutSeconds" or "checkTimeoutSeconds" or "maxAttempts"))
            {
                problems.Add($"build: unknown field {prop.Name}");
                continue;
            }

            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var val) || val < 1)
            {
                problems.Add($"build.{prop.Name} must be a positive integer");
            }
        }
    }

    private static void ValidateTestPatterns(JsonElement raw, List<string> problems)
    {
        if (!raw.TryGetProperty("testPatterns", out var prop))
        {
            return;
        }

        if (prop.ValueKind != JsonValueKind.Array ||
            prop.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())))
        {
            problems.Add("testPatterns must be an array of non-empty strings");
        }
    }
}
