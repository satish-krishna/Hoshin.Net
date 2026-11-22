using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Classifies repository-relative paths as test vs. production files using glob patterns and extracts
/// verified test file paths from a task's <c>test</c> argv.
/// </summary>
public static class TestPathClassifier
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly RegexOptions GlobRegexOptions =
        OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;

    private static readonly SearchValues<char> DisallowedTokenChars =
        SearchValues.Create(['*', '?', '"', '<', '>', '|', .. Path.GetInvalidPathChars()]);

    public static bool IsTestPath(
        string relativePath,
        IReadOnlyList<string>? testPatterns = null,
        IReadOnlyList<string>? taskTestFiles = null)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        if (taskTestFiles is not null)
        {
            foreach (var f in taskTestFiles)
            {
                var candidate = f.Replace('\\', '/').TrimStart('/');
                if (candidate.Length > 0 && string.Equals(normalized, candidate, PathComparison))
                {
                    return true;
                }
            }
        }

        var patterns = testPatterns is { Count: > 0 } ? testPatterns : WorkflowPlan.DefaultTestPatterns;
        foreach (var pattern in patterns)
        {
            if (MatchesGlob(normalized, pattern))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesGlob(string normalizedPath, string pattern)
    {
        var norm = normalizedPath.Replace('\\', '/').TrimStart('/');
        var p = pattern.Replace('\\', '/').TrimStart('/');

        if (p.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = p[..^3];
            if (!prefix.Contains('*') && !prefix.Contains('?'))
            {
                return norm.StartsWith(prefix + "/", PathComparison) ||
                       norm.Contains("/" + prefix + "/", PathComparison);
            }
        }

        var regex = BuildGlobRegex(p);
        return Regex.IsMatch(norm, regex, GlobRegexOptions, TimeSpan.FromSeconds(1));
    }

    public static List<string> ExtractTestFilesFromArgv(
        string[] argv,
        string? repoRoot = null,
        IReadOnlyList<string>? testPatterns = null)
    {
        var files = new List<string>();
        foreach (var token in argv.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith('-'))
            {
                continue;
            }

            if (!TryNormalizeRepoRelative(token, repoRoot, out var rel, out var fullPath))
            {
                continue;
            }

            if (HasKnownTestSuffix(rel) || (fullPath is not null && File.Exists(fullPath) && IsTestPath(rel, testPatterns)))
            {
                files.Add(rel);
            }
        }

        return files;
    }

    private static string BuildGlobRegex(string pattern)
    {
        var sb = new StringBuilder();
        sb.Append(pattern.Contains('/') ? "^" : "(?:^|/)");
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                if (i + 2 < pattern.Length && pattern[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i++;
                }
            }
            else if (pattern[i] == '*')
            {
                sb.Append("[^/]*");
            }
            else if (pattern[i] == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(pattern[i].ToString()));
            }
        }

        sb.Append('$');
        return sb.ToString();
    }

    private static bool TryNormalizeRepoRelative(string token, string? repoRoot, out string relative, out string? fullPath)
    {
        relative = "";
        fullPath = null;
        if (token.AsSpan().IndexOfAny(DisallowedTokenChars) >= 0)
        {
            return false;
        }

        if (repoRoot is not null)
        {
            var rootFull = Path.GetFullPath(repoRoot);
            var candidate = Path.GetFullPath(token, rootFull);
            var rel = Path.GetRelativePath(rootFull, candidate).Replace('\\', '/');
            if (rel is "." or ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            {
                return false;
            }

            relative = rel;
            fullPath = candidate;
            return true;
        }

        var norm = token.Replace('\\', '/');
        while (norm.StartsWith("./", StringComparison.Ordinal))
        {
            norm = norm[2..];
        }

        if (norm.Length == 0 || norm is "." or ".." || norm.StartsWith("../", StringComparison.Ordinal) || norm.StartsWith('/') || (norm.Length >= 2 && norm[1] == ':'))
        {
            return false;
        }

        relative = norm;
        return true;
    }

    private static bool HasKnownTestSuffix(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return (name.Contains(".test.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".test.", StringComparison.OrdinalIgnoreCase))
            || (name.Contains(".spec.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".spec.", StringComparison.OrdinalIgnoreCase))
            || name.EndsWith("Tests.cs", StringComparison.Ordinal)
            || name.EndsWith("Test.cs", StringComparison.Ordinal)
            || (name.StartsWith("test_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            || name.EndsWith("_test.go", StringComparison.OrdinalIgnoreCase);
    }
}
