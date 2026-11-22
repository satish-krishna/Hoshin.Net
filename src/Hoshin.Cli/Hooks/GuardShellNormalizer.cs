using System.Text.RegularExpressions;

namespace Hoshin.Cli.Hooks;

/// <summary>Normalizes trivial shell obfuscations (empty quote pairs and line continuations) before guard matching (T04 / S3).</summary>
public static partial class GuardShellNormalizer
{
    [GeneratedRegex(@"\\\r?\n")]
    private static partial Regex LineContinuationRegex();

    public static string Normalize(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return "";
        }

        var normalized = LineContinuationRegex().Replace(command, "");
        string previous;
        do
        {
            previous = normalized;
            normalized = normalized
                .Replace("\"\"", "", StringComparison.Ordinal)
                .Replace("''", "", StringComparison.Ordinal);
        }
        while (!string.Equals(normalized, previous, StringComparison.Ordinal));

        return normalized;
    }
}
