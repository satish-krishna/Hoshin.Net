using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Workspace;

public sealed record NamingSetting(string? Branch, string? PullRequestTitle);

public sealed record NamingRead(bool Declared, NamingSetting? Setting);

public sealed record NamingValues(string Type, string Scope, string Change, string? Title = null);

public sealed record RenderedNames(string Branch, string? PullRequestTitle);

/// <summary>Team branch/PR naming rules, Git branch-name validation, and commit scope validation.</summary>
public static class NamingService
{
    private static readonly HashSet<string> KnownParts = ["type", "scope", "slug", "change", "title"];
    private static readonly NamingValues SampleValues = new("feat", "PROJ-123", "2026-01-01-sample");

    private static readonly Regex ControlChars = new(@"[\x00-\x1f\x7f]", RegexOptions.Compiled);
    private static readonly Regex BadBranchChars = new(@"[~^:?*\[\\]", RegexOptions.Compiled);
    private static readonly Regex Parens = new(@"[()]", RegexOptions.Compiled);
    private static readonly Regex StartsAlnum = new(@"^[A-Za-z0-9]", RegexOptions.Compiled);
    private static readonly Regex ScopeChars = new(@"^[A-Za-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex DatePrefix = new(@"^\d{4}-\d{2}-\d{2}-", RegexOptions.Compiled);
    private static readonly Regex PlaceholderPattern = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex HasUniquePart = new(@"\{(slug|change|scope)\}", RegexOptions.Compiled);

    public static NamingRead ReadNaming(string markerPath, string system)
    {
        string text;
        try
        {
            text = File.ReadAllText(markerPath);
        }
        catch (Exception e)
        {
            throw new CouldNotRunException($"{markerPath}: cannot read the marker: {e.Message}");
        }

        return NamingFromText(text, system, markerPath);
    }

    public static NamingRead NamingFromText(string text, string system, string markerPath)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (Exception e)
        {
            throw new CouldNotRunException($"{markerPath}: cannot read the marker: {e.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("systems", out var systems) ||
                systems.ValueKind != JsonValueKind.Object ||
                !systems.TryGetProperty(system, out var entry) ||
                entry.ValueKind != JsonValueKind.Object)
            {
                throw new CouldNotRunException($"{markerPath}: the marker lists no system \"{system}\"");
            }

            if (!entry.TryGetProperty("naming", out var namingProp))
            {
                return new NamingRead(false, null);
            }

            string? branch = null;
            string? prTitle = null;
            if (namingProp.ValueKind == JsonValueKind.Object)
            {
                if (namingProp.TryGetProperty("branch", out var b) && b.ValueKind == JsonValueKind.String)
                {
                    branch = b.GetString();
                }

                if (namingProp.TryGetProperty("pullRequestTitle", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    prTitle = t.GetString();
                }
            }

            return new NamingRead(true, new NamingSetting(branch, prTitle));
        }
    }

    public static List<string> CheckBranchName(string name)
    {
        if (name.Length == 0)
        {
            return ["a branch name cannot be empty"];
        }

        if (name.StartsWith('-'))
        {
            return ["a branch name cannot start with -"];
        }

        if (name.StartsWith("change/pending-", StringComparison.Ordinal))
        {
            return ["names starting \"change/pending-\" are reserved for the tool driving the build"];
        }

        var problems = new List<string>();
        if (name.Contains(' '))
        {
            problems.Add("a branch name cannot contain a space");
        }

        if (ControlChars.IsMatch(name))
        {
            problems.Add("a branch name cannot contain a control character");
        }

        if (BadBranchChars.IsMatch(name))
        {
            problems.Add("a branch name cannot contain any of ~ ^ : ? * [ \\");
        }

        if (name.Contains("..", StringComparison.Ordinal))
        {
            problems.Add("a branch name cannot contain \"..\"");
        }

        if (name.Contains("@{", StringComparison.Ordinal))
        {
            problems.Add("a branch name cannot contain \"@{\"");
        }

        if (name == "HEAD")
        {
            problems.Add("a branch name cannot be \"HEAD\"");
        }

        if (name == "@")
        {
            problems.Add("a branch name cannot be \"@\"");
        }

        var segments = name.Split('/');
        if (segments.Any(s => s.Length == 0))
        {
            problems.Add("a branch name cannot contain an empty path segment");
        }

        if (segments.Any(s => s.StartsWith('.')))
        {
            problems.Add("a branch name cannot have a path segment starting with \".\"");
        }

        if (segments.Any(s => s.EndsWith(".lock", StringComparison.Ordinal)))
        {
            problems.Add("a branch name cannot end in \".lock\"");
        }

        if (name.EndsWith('.'))
        {
            problems.Add("a branch name cannot end in \".\"");
        }

        return problems;
    }

    public static List<string> CheckScope(string scope)
    {
        if (scope.Length == 0)
        {
            return ["a scope cannot be empty"];
        }

        if (scope.Length > 64)
        {
            return ["a scope can be at most 64 characters"];
        }

        if (scope.Contains(' '))
        {
            return ["a scope cannot contain a space"];
        }

        if (Parens.IsMatch(scope))
        {
            return ["a scope cannot contain parentheses"];
        }

        if (!StartsAlnum.IsMatch(scope))
        {
            return ["a scope must start with a letter or a digit"];
        }

        if (!ScopeChars.IsMatch(scope))
        {
            return ["a scope can hold only letters, digits, \"-\", \"_\" and \".\""];
        }

        if (scope.Contains("..", StringComparison.Ordinal))
        {
            return ["a scope cannot contain \"..\""];
        }

        if (scope == "HEAD")
        {
            return ["a scope cannot be \"HEAD\""];
        }

        if (scope.EndsWith('.'))
        {
            return ["a scope cannot end in \".\""];
        }

        if (scope.EndsWith(".lock", StringComparison.Ordinal))
        {
            return ["a scope cannot end in \".lock\""];
        }

        return [];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public static List<string> CheckNaming(NamingSetting setting)
    {
        var problems = new List<string>();
        if (string.IsNullOrEmpty(setting.Branch) || string.IsNullOrEmpty(setting.PullRequestTitle))
        {
            problems.Add("a naming setting needs both a branch pattern and a pull request title pattern");
        }

        var unknown = new List<string>();
        var unknownSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pattern in new[] { setting.Branch ?? "", setting.PullRequestTitle ?? "" })
        {
            foreach (Match m in PlaceholderPattern.Matches(pattern))
            {
                var part = m.Groups[1].Value;
                if (!KnownParts.Contains(part) && unknownSet.Add(part))
                {
                    unknown.Add(part);
                }
            }
        }

        foreach (var part in unknown)
        {
            problems.Add($"\"{part}\" is not a known part of a name");
        }

        if (!string.IsNullOrEmpty(setting.Branch) && setting.Branch.Contains("{title}", StringComparison.Ordinal))
        {
            problems.Add("the title can only be used in the pull request title");
        }

        if (!string.IsNullOrEmpty(setting.Branch) && !HasUniquePart.IsMatch(setting.Branch))
        {
            problems.Add("the branch pattern needs {slug}, {change} or {scope}, so that each change gets its own branch");
        }

        if (!string.IsNullOrEmpty(setting.Branch))
        {
            var parts = PartsOf(SampleValues);
            parts["title"] = "title";
            var sample = Fill(setting.Branch, parts);
            foreach (var p in CheckBranchName(sample))
            {
                problems.Add($"branch pattern \"{setting.Branch}\": {p}");
            }
        }

        return problems;
    }

    public static RenderedNames RenderNaming(NamingSetting setting, NamingValues values)
    {
        var parts = PartsOf(values);
        return new RenderedNames(
            Fill(setting.Branch ?? "", parts),
            values.Title is null ? null : Fill(setting.PullRequestTitle ?? "", parts));
    }

    private static Dictionary<string, string> PartsOf(NamingValues values)
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = values.Type,
            ["scope"] = values.Scope,
            ["change"] = values.Change,
            ["slug"] = DatePrefix.Replace(values.Change, ""),
        };
        if (values.Title is not null)
        {
            parts["title"] = values.Title;
        }

        return parts;
    }

    private static string Fill(string pattern, Dictionary<string, string> parts) =>
        PlaceholderPattern.Replace(pattern, m => parts.TryGetValue(m.Groups[1].Value, out var val) ? val : m.Value);
}
