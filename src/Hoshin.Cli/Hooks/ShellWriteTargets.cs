using System.Text.RegularExpressions;

namespace Hoshin.Cli.Hooks;

/// <summary>Extracts file paths written by shell redirections (<c>&gt;</c>, <c>&gt;&gt;</c>) and <c>tee</c> commands (T06 / S7).</summary>
public static partial class ShellWriteTargets
{
    [GeneratedRegex(@"(?:^|[^<>&])(?:[0-9]*)>{1,2}(?!&)\s*(?:'([^']+)'|""([^""]+)""|([^\s;&|<>]+))")]
    private static partial Regex RedirectTargetRegex();

    [GeneratedRegex(@"\btee\b((?:\s+(?:'[^']+'|""[^""]+""|[^\s;&|<>]+))+)")]
    private static partial Regex TeeCommandRegex();

    [GeneratedRegex(@"'([^']+)'|""([^""]+)""|([^\s]+)")]
    private static partial Regex ShellTokenRegex();

    public static IReadOnlyList<string> Extract(string normalizedCommand)
    {
        if (string.IsNullOrWhiteSpace(normalizedCommand))
        {
            return [];
        }

        var targets = new List<string>();
        foreach (Match m in RedirectTargetRegex().Matches(normalizedCommand))
        {
            var path = FirstNonEmpty(m.Groups[1], m.Groups[2], m.Groups[3]);
            if (path is not null && !IsNullDevice(path))
            {
                targets.Add(path);
            }
        }

        foreach (Match m in TeeCommandRegex().Matches(normalizedCommand))
        {
            var argSlice = m.Groups[1].Value;
            foreach (Match tok in ShellTokenRegex().Matches(argSlice))
            {
                var value = FirstNonEmpty(tok.Groups[1], tok.Groups[2], tok.Groups[3]);
                if (value is null || value.StartsWith('-') || IsNullDevice(value))
                {
                    continue;
                }

                targets.Add(value);
            }
        }

        return targets;
    }

    private static string? FirstNonEmpty(Group g1, Group g2, Group g3)
    {
        if (g1.Success && g1.Value.Length > 0)
        {
            return g1.Value;
        }

        if (g2.Success && g2.Value.Length > 0)
        {
            return g2.Value;
        }

        return g3.Success && g3.Value.Length > 0 ? g3.Value : null;
    }

    private static bool IsNullDevice(string path) =>
        path.Equals("/dev/null", StringComparison.OrdinalIgnoreCase)
        || path.Equals("NUL", StringComparison.OrdinalIgnoreCase);
}
