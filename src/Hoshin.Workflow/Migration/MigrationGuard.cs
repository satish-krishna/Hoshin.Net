using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

/// <summary>
/// Fail-closed validation helpers for YAML-to-JSONC migration: rejects unsupported YAML constructs,
/// handles quote-aware inline comments, and verifies converted JSONC preserves every source key.
/// </summary>
internal static class MigrationGuard
{
    public static void RejectDocumentSeparator(string yamlText, string context)
    {
        var lines = yamlText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed == "---" || trimmed.StartsWith("--- ", StringComparison.Ordinal))
            {
                throw new FormatException($"{context} line {i + 1}: leading YAML document separator (---) is not supported");
            }

            break;
        }
    }

    public static void RejectUnsupportedIndicator(string value, int lineNumber)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (value[0] == '&' && value.Length > 1 && !char.IsWhiteSpace(value[1]))
        {
            throw new FormatException($"YAML line {lineNumber}: anchors (&) are not supported");
        }

        if (value[0] == '*' && value.Length > 1 && !char.IsWhiteSpace(value[1]))
        {
            throw new FormatException($"YAML line {lineNumber}: aliases (*) are not supported");
        }

        if (value.StartsWith("!!", StringComparison.Ordinal) || value.StartsWith("!<", StringComparison.Ordinal))
        {
            throw new FormatException($"YAML line {lineNumber}: YAML tags (!!) are not supported");
        }
    }

    public static void ValidateSingleLineQuotedScalar(string value, int lineNumber)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (value[0] == '"')
        {
            var close = FindDoubleQuoteEnd(value);
            if (close < 0)
            {
                throw new FormatException($"YAML line {lineNumber}: multi-line or unclosed double-quoted scalar is not supported");
            }

            if (close != value.Length - 1)
            {
                throw new FormatException($"YAML line {lineNumber}: unexpected trailing text after double-quoted scalar");
            }
        }
        else if (value[0] == '\'')
        {
            var close = FindSingleQuoteEnd(value);
            if (close < 0)
            {
                throw new FormatException($"YAML line {lineNumber}: multi-line or unclosed single-quoted scalar is not supported");
            }

            if (close != value.Length - 1)
            {
                throw new FormatException($"YAML line {lineNumber}: unexpected trailing text after single-quoted scalar");
            }
        }
    }

    public static (string Value, string? Comment) SplitInlineCommentSafe(string text)
    {
        if (text.Length == 0)
        {
            return ("", null);
        }

        if (text[0] == '"')
        {
            var close = FindDoubleQuoteEnd(text);
            return close < 0 ? (text.TrimEnd(), null) : SplitAfterIndex(text, close + 1);
        }

        if (text[0] == '\'')
        {
            var close = FindSingleQuoteEnd(text);
            return close < 0 ? (text.TrimEnd(), null) : SplitAfterIndex(text, close + 1);
        }

        if (text[0] is '[' or '{')
        {
            return SplitInlineCommentInFlow(text);
        }

        return SplitAfterIndex(text, 0);
    }

    public static void VerifyChangeYamlKeySet(string sourceYaml, string convertedJsonc)
    {
        var expectedKeys = ExtractTopLevelYamlKeys(sourceYaml);
        VerifyJsoncHasKeys(convertedJsonc, expectedKeys, "change.jsonc");
    }

    public static void VerifyMarkdownFrontMatterKeySet(string originalMarkdown, string migratedMarkdown)
    {
        var origLines = Doc.Normalize(originalMarkdown).Split('\n');
        if (origLines.Length == 0 || origLines[0].Trim() != "---")
        {
            return;
        }

        var close = Array.FindIndex(origLines, 1, l => l.Trim() == "---");
        if (close < 0)
        {
            return;
        }

        var expectedKeys = ExtractTopLevelYamlKeys(string.Join('\n', origLines[1..close]));
        var doc = Doc.Split(migratedMarkdown);
        var fence = doc.Fences.FirstOrDefault(f => f.Open == 0 && Doc.IsFieldsBlock(f));
        if (fence is null || fence.Close < 0)
        {
            throw new FormatException("migration verification failed: migrated document is missing JSONC front matter");
        }

        VerifyJsoncHasKeys(doc.BlockOf(fence).Text, expectedKeys, "front matter");
    }

    private static void VerifyJsoncHasKeys(string jsonc, HashSet<string> expectedKeys, string label)
    {
        if (!Jsonc.TryParse(jsonc, out var parsed, out var err) || parsed is not { ValueKind: JsonValueKind.Object } obj)
        {
            throw new FormatException($"migration verification failed: {label} is not a valid JSONC object ({err})");
        }

        var actualKeys = new HashSet<string>(obj.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);
        var missing = expectedKeys.Where(k => !actualKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new FormatException($"migration verification failed: {label} is missing source key(s): {string.Join(", ", missing)}");
        }
    }

    private static HashSet<string> ExtractTopLevelYamlKeys(string yamlText)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var lines = yamlText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inBlockScalar = false;

        foreach (var raw in lines)
        {
            var trimmedEnd = raw.TrimEnd();
            if (trimmedEnd.Length == 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(trimmedEnd[0]))
            {
                continue;
            }

            inBlockScalar = false;
            if (trimmedEnd[0] == '#' || trimmedEnd[0] == '-' || trimmedEnd[0] is '[' or '{')
            {
                continue;
            }

            if (TryExtractKey(trimmedEnd, out var key, out var rest))
            {
                keys.Add(key);
                var val = rest.Trim();
                if (val.StartsWith('|') || val.StartsWith('>'))
                {
                    inBlockScalar = true;
                }
            }

            _ = inBlockScalar;
        }

        return keys;
    }

    private static bool TryExtractKey(string line, out string key, out string rest)
    {
        key = "";
        rest = "";
        if (line[0] == '"')
        {
            var end = FindDoubleQuoteEnd(line);
            if (end > 0 && end + 1 < line.Length && line[end + 1] == ':')
            {
                key = JsonSerializer.Deserialize<string>(line[..(end + 1)]) ?? line[1..end];
                rest = line[(end + 2)..];
                return true;
            }

            return false;
        }

        if (line[0] == '\'')
        {
            var end = FindSingleQuoteEnd(line);
            if (end > 0 && end + 1 < line.Length && line[end + 1] == ':')
            {
                key = line[1..end].Replace("''", "'", StringComparison.Ordinal);
                rest = line[(end + 2)..];
                return true;
            }

            return false;
        }

        var colon = line.IndexOf(':');
        if (colon > 0 && (colon + 1 == line.Length || char.IsWhiteSpace(line[colon + 1])))
        {
            key = line[..colon].Trim();
            rest = line[(colon + 1)..];
            return key.Length > 0;
        }

        return false;
    }

    private static (string Value, string? Comment) SplitAfterIndex(string text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                var body = text[(i + 1)..];
                var comment = body.StartsWith(' ') ? body[1..] : body;
                return (text[..i].TrimEnd(), comment);
            }
        }

        return (text.TrimEnd(), null);
    }

    private static (string Value, string? Comment) SplitInlineCommentInFlow(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && depth > 0)
            {
                var end = FindDoubleQuoteEnd(text, i);
                if (end < 0)
                {
                    return (text.TrimEnd(), null);
                }

                i = end;
                continue;
            }

            if (c == '\'' && depth > 0)
            {
                var end = FindSingleQuoteEnd(text, i);
                if (end < 0)
                {
                    return (text.TrimEnd(), null);
                }

                i = end;
                continue;
            }

            if (c is '[' or '{')
            {
                depth++;
            }
            else if (c is ']' or '}')
            {
                depth--;
            }
            else if (depth <= 0 && c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                var body = text[(i + 1)..];
                var comment = body.StartsWith(' ') ? body[1..] : body;
                return (text[..i].TrimEnd(), comment);
            }
        }

        return (text.TrimEnd(), null);
    }

    private static int FindDoubleQuoteEnd(string text, int start = 0)
    {
        var escape = false;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (escape)
            {
                escape = false;
            }
            else if (c == '\\')
            {
                escape = true;
            }
            else if (c == '"')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindSingleQuoteEnd(string text, int start = 0)
    {
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }

                return i;
            }
        }

        return -1;
    }
}
