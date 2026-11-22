using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hoshin.Workflow.Migration;

/// <summary>
/// Minimal, zero-dependency YAML-subset parser and comment-preserving JSONC serializer tailored to Hoshin documents
/// (<c>intent.md</c>, <c>spec.md</c>, <c>plan.md</c>, <c>build/*.md</c>, <c>build/review.md</c>, <c>context/*.md</c>,
/// <c>branch.md</c>, and <c>change.yml</c>).
/// </summary>
public sealed class YamlToJsoncConverter : IYamlToJsoncConverter
{
    public static YamlToJsoncConverter Default { get; } = new();

    public static readonly IReadOnlySet<string> DefaultStringKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "commits", "pushed", "source_intent_commit", "source_spec_commit",
        "departure", "red", "notes", "file", "rule", "repo", "branch",
        "scope", "base", "type", "question", "owner", "function", "system",
        "status", "author", "title", "date", "reviewed_at", "id", "task",
        "severity", "concern", "test", "compile", "files", "depends_on",
        "uses", "covers", "supports", "policies_applied", "policies_not_checked",
        "url", "source_intent", "source_spec",
    };

    private static readonly JsonSerializerOptions JsonStringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Regex JsonIntegerRegex = new(@"^-?(?:0|[1-9][0-9]*)$", RegexOptions.Compiled);
    private static readonly Regex JsonFloatRegex = new(@"^-?(?:0|[1-9][0-9]*)\.[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex BlockScalarHeaderRegex = new(@"^([|>])([+-]?)([1-9]?)$|^([|>])([1-9]?)([+-]?)$", RegexOptions.Compiled);

    public string ConvertFrontMatter(string yamlText, bool forceLayout2 = false) =>
        ConvertBlock(yamlText, new YamlConversionOptions(DefaultStringKeys, ForceLayout2: forceLayout2));

    public string ConvertChangeYaml(string yamlText) =>
        ConvertBlock(yamlText, new YamlConversionOptions(DefaultStringKeys, ForceLayout2: false, RootLeadingCommentsOutside: true));

    public string ConvertBlock(string yamlText, YamlConversionOptions? options = null)
    {
        var opts = options ?? new YamlConversionOptions(DefaultStringKeys);
        var stringKeys = opts.StringKeys ?? DefaultStringKeys;
        var normalized = yamlText.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawLines = normalized.Split('\n');

        // Trim leading/trailing empty lines only when scanning, keeping comments intact.
        var lines = rawLines.Select(l => l.TrimEnd()).ToList();
        var parser = new Parser(lines, stringKeys);
        var root = parser.ParseRoot(opts.ExpectArray);

        if (opts.ForceLayout2 && root is YamlMapping map)
        {
            EnsureLayout2(map);
        }

        return Emitter.EmitRoot(root, opts.RootLeadingCommentsOutside);
    }

    private static void EnsureLayout2(YamlMapping map)
    {
        for (var i = 0; i < map.Entries.Count; i++)
        {
            if (map.Entries[i].Key == "layout")
            {
                var existing = map.Entries[i];
                if (existing.Value is YamlScalar s && int.TryParse(s.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) && num >= 2)
                {
                    return;
                }

                map.Entries[i] = existing with { Value = new YamlScalar("2", ScalarStyle.Plain, ForceString: false) };
                return;
            }
        }

        map.Entries.Insert(0, new YamlEntry("layout", new YamlScalar("2", ScalarStyle.Plain, ForceString: false), [], null));
    }

    private enum ScalarStyle
    {
        Plain,
        SingleQuoted,
        DoubleQuoted,
        Block,
    }

    private abstract record YamlNode;

    private sealed record YamlScalar(string Raw, ScalarStyle Style, bool ForceString) : YamlNode;

    private sealed record YamlEntry(string Key, YamlNode Value, List<string> LeadingComments, string? InlineComment);

    private sealed record YamlMapping(List<YamlEntry> Entries, List<string> LeadingComments, List<string> TrailingComments) : YamlNode;

    private sealed record YamlSequenceItem(YamlNode Value, List<string> LeadingComments, string? InlineComment);

    private sealed record YamlSequence(List<YamlSequenceItem> Items, bool IsFlow, List<string> LeadingComments, List<string> TrailingComments) : YamlNode;

    private sealed class Parser(List<string> lines, IReadOnlySet<string> stringKeys)
    {
        private int _pos;

        public YamlNode ParseRoot(bool expectArray)
        {
            var leading = CollectCommentsAndBlanks();
            if (_pos >= lines.Count)
            {
                return expectArray
                    ? new YamlSequence([], IsFlow: false, leading, [])
                    : new YamlMapping([], leading, []);
            }

            var indent = IndentOf(lines[_pos]);
            var trimmed = lines[_pos].TrimStart();

            if (trimmed.StartsWith('[') || trimmed.StartsWith('{'))
            {
                var (valPart, comment) = SplitInlineComment(trimmed);
                _pos++;
                var flow = ParseFlowValue(valPart, forceString: false);
                var trailing = CollectCommentsAndBlanks();
                if (flow is YamlMapping fm)
                {
                    return fm with { LeadingComments = leading, TrailingComments = trailing };
                }

                if (flow is YamlSequence fs)
                {
                    return fs with { LeadingComments = leading, TrailingComments = trailing };
                }

                return flow;
            }

            if (IsSequenceItem(trimmed))
            {
                var seq = ParseBlockSequence(indent, forceString: false);
                return seq with { LeadingComments = [.. leading, .. seq.LeadingComments] };
            }

            var map = ParseBlockMapping(indent, forceString: false);
            return map with { LeadingComments = [.. leading, .. map.LeadingComments] };
        }

        private YamlMapping ParseBlockMapping(int minIndent, bool forceString)
        {
            var entries = new List<YamlEntry>();
            var pendingComments = new List<string>();

            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                if (string.IsNullOrWhiteSpace(line))
                {
                    _pos++;
                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    pendingComments.Add(StripCommentHash(trimmed));
                    _pos++;
                    continue;
                }

                var indent = IndentOf(line);
                if (indent < minIndent)
                {
                    break;
                }

                if (indent > minIndent && entries.Count > 0)
                {
                    break;
                }

                if (IsSequenceItem(trimmed))
                {
                    break;
                }

                if (!TrySplitKeyValue(trimmed, out var key, out var rest))
                {
                    throw new FormatException($"YAML line {_pos + 1} is not a key-value pair: {trimmed}");
                }

                _pos++;
                var childForceString = forceString || stringKeys.Contains(key);
                var (value, inlineComment) = ParseValueAfterColon(rest, indent, childForceString);
                entries.Add(new YamlEntry(key, value, pendingComments, inlineComment));
                pendingComments = [];
            }

            return new YamlMapping(entries, [], pendingComments);
        }

        private YamlSequence ParseBlockSequence(int seqIndent, bool forceString)
        {
            var items = new List<YamlSequenceItem>();
            var pendingComments = new List<string>();

            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                if (string.IsNullOrWhiteSpace(line))
                {
                    _pos++;
                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    pendingComments.Add(StripCommentHash(trimmed));
                    _pos++;
                    continue;
                }

                var indent = IndentOf(line);
                if (indent < seqIndent)
                {
                    break;
                }

                if (indent != seqIndent || !IsSequenceItem(trimmed))
                {
                    break;
                }

                var afterDash = trimmed.Length == 1 ? "" : trimmed[2..];
                var itemContentIndent = indent + 2;
                _pos++;

                if (string.IsNullOrWhiteSpace(afterDash) || afterDash.TrimStart().StartsWith('#'))
                {
                    var itemInline = afterDash.TrimStart().StartsWith('#') ? StripCommentHash(afterDash.TrimStart()) : null;
                    var nested = ParseIndentedNode(indent, forceString);
                    items.Add(new YamlSequenceItem(nested, pendingComments, itemInline));
                    pendingComments = [];
                    continue;
                }

                var afterTrimmed = afterDash.TrimStart();
                var extraSpaces = afterDash.Length - afterTrimmed.Length;
                var effectiveIndent = itemContentIndent + extraSpaces;

                if (TrySplitKeyValue(afterTrimmed, out var key, out var rest))
                {
                    var childForceString = forceString || stringKeys.Contains(key);
                    var (firstVal, firstInline) = ParseValueAfterColon(rest, effectiveIndent, childForceString);
                    var mapEntries = new List<YamlEntry> { new(key, firstVal, [], firstInline) };
                    var restMap = ParseBlockMapping(effectiveIndent, forceString);
                    mapEntries.AddRange(restMap.Entries);
                    items.Add(new YamlSequenceItem(new YamlMapping(mapEntries, [], restMap.TrailingComments), pendingComments, null));
                    pendingComments = [];
                }
                else
                {
                    var (valPart, comment) = SplitInlineComment(afterTrimmed);
                    var val = ParseInlineValue(valPart, effectiveIndent, forceString);
                    items.Add(new YamlSequenceItem(val, pendingComments, comment));
                    pendingComments = [];
                }
            }

            return new YamlSequence(items, IsFlow: false, [], pendingComments);
        }

        private (YamlNode Value, string? InlineComment) ParseValueAfterColon(string rest, int keyIndent, bool forceString)
        {
            var trimmed = rest.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                var inline = trimmed.StartsWith('#') ? StripCommentHash(trimmed) : null;
                var nested = ParseNestedBlockAfterKey(keyIndent, forceString);
                return (nested, inline);
            }

            var (valPart, comment) = SplitInlineComment(trimmed);
            if (BlockScalarHeaderRegex.IsMatch(valPart))
            {
                var text = ParseBlockScalar(valPart, keyIndent);
                return (new YamlScalar(text, ScalarStyle.Block, ForceString: true), comment);
            }

            var node = ParseInlineValue(valPart, keyIndent, forceString);
            return (node, comment);
        }

        [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Returns polymorphic YamlNode; converter is replaced in T11.")]
        private YamlNode ParseNestedBlockAfterKey(int keyIndent, bool forceString)
        {
            var saved = _pos;
            var leading = CollectCommentsAndBlanks();
            if (_pos >= lines.Count)
            {
                _pos = saved;
                return new YamlScalar("null", ScalarStyle.Plain, ForceString: false);
            }

            var nextLine = lines[_pos];
            var nextIndent = IndentOf(nextLine);
            var nextTrimmed = nextLine.TrimStart();

            // Block sequence directly under key may sit at keyIndent or deeper.
            if (nextIndent >= keyIndent && IsSequenceItem(nextTrimmed))
            {
                var seq = ParseBlockSequence(nextIndent, forceString);
                return seq with { LeadingComments = [.. leading, .. seq.LeadingComments] };
            }

            if (nextIndent <= keyIndent)
            {
                // Nothing nested under this key; restore so comments attach to next sibling.
                _pos = saved;
                return new YamlScalar("null", ScalarStyle.Plain, ForceString: false);
            }

            var map = ParseBlockMapping(nextIndent, forceString);
            return map with { LeadingComments = [.. leading, .. map.LeadingComments] };
        }

        private YamlNode ParseIndentedNode(int parentIndent, bool forceString)
        {
            var saved = _pos;
            var leading = CollectCommentsAndBlanks();
            if (_pos >= lines.Count)
            {
                _pos = saved;
                return new YamlScalar("null", ScalarStyle.Plain, ForceString: false);
            }

            var nextLine = lines[_pos];
            var nextIndent = IndentOf(nextLine);
            if (nextIndent <= parentIndent)
            {
                _pos = saved;
                return new YamlScalar("null", ScalarStyle.Plain, ForceString: false);
            }

            var nextTrimmed = nextLine.TrimStart();
            if (IsSequenceItem(nextTrimmed))
            {
                var seq = ParseBlockSequence(nextIndent, forceString);
                return seq with { LeadingComments = [.. leading, .. seq.LeadingComments] };
            }

            if (TrySplitKeyValue(nextTrimmed, out _, out _))
            {
                var map = ParseBlockMapping(nextIndent, forceString);
                return map with { LeadingComments = [.. leading, .. map.LeadingComments] };
            }

            _pos++;
            var (valPart, _) = SplitInlineComment(nextTrimmed);
            return ParseInlineValue(valPart, nextIndent, forceString);
        }

        private YamlNode ParseInlineValue(string valPart, int keyIndent, bool forceString)
        {
            if (valPart.StartsWith('[') || valPart.StartsWith('{'))
            {
                var full = CollectMultilineFlow(valPart);
                return ParseFlowValue(full, forceString);
            }

            if (valPart.StartsWith('"'))
            {
                return new YamlScalar(UnquoteDouble(valPart), ScalarStyle.DoubleQuoted, ForceString: true);
            }

            if (valPart.StartsWith('\''))
            {
                return new YamlScalar(UnquoteSingle(valPart), ScalarStyle.SingleQuoted, ForceString: true);
            }

            // Check for multi-line plain scalar continuation lines.
            var sb = new StringBuilder(valPart);
            while (_pos < lines.Count)
            {
                var next = lines[_pos];
                if (string.IsNullOrWhiteSpace(next))
                {
                    break;
                }

                var nextTrimmed = next.TrimStart();
                if (nextTrimmed.StartsWith('#'))
                {
                    break;
                }

                var nextIndent = IndentOf(next);
                if (nextIndent <= keyIndent || IsSequenceItem(nextTrimmed) || TrySplitKeyValue(nextTrimmed, out _, out _))
                {
                    break;
                }

                var (contVal, _) = SplitInlineComment(nextTrimmed);
                sb.Append(' ').Append(contVal);
                _pos++;
            }

            return new YamlScalar(sb.ToString(), ScalarStyle.Plain, forceString);
        }

        private string CollectMultilineFlow(string initial)
        {
            var sb = new StringBuilder(initial);
            while (!IsFlowBalanced(sb.ToString()) && _pos < lines.Count)
            {
                var (part, _) = SplitInlineComment(lines[_pos].Trim());
                if (part.Length > 0)
                {
                    sb.Append(' ').Append(part);
                }

                _pos++;
            }

            return sb.ToString();
        }

        private static bool IsFlowBalanced(string text)
        {
            var depth = 0;
            var inSingle = false;
            var inDouble = false;
            var escape = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (inDouble)
                {
                    if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inDouble = false;
                    }

                    continue;
                }

                if (inSingle)
                {
                    if (c == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                    }
                    else if (c == '\'')
                    {
                        inSingle = false;
                    }

                    continue;
                }

                switch (c)
                {
                    case '"': inDouble = true; break;
                    case '\'': inSingle = true; break;
                    case '[' or '{': depth++; break;
                    case ']' or '}': depth--; break;
                }
            }

            return depth <= 0;
        }

        private YamlNode ParseFlowValue(string text, bool forceString)
        {
            var cursor = 0;
            return ParseFlowNode(text, ref cursor, forceString);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: replaced in T11")]
        private YamlNode ParseFlowNode(string text, ref int i, bool forceString)
        {
            SkipWs(text, ref i);
            if (i >= text.Length)
            {
                return new YamlScalar("null", ScalarStyle.Plain, ForceString: false);
            }

            if (text[i] == '[')
            {
                i++; // '['
                var items = new List<YamlSequenceItem>();
                while (i < text.Length)
                {
                    SkipWs(text, ref i);
                    if (i >= text.Length || text[i] == ']')
                    {
                        if (i < text.Length)
                        {
                            i++;
                        }

                        break;
                    }

                    var item = ParseFlowNode(text, ref i, forceString);
                    items.Add(new YamlSequenceItem(item, [], null));
                    SkipWs(text, ref i);
                    if (i < text.Length && text[i] == ',')
                    {
                        i++;
                    }
                }

                return new YamlSequence(items, IsFlow: true, [], []);
            }

            if (text[i] == '{')
            {
                i++; // '{'
                var entries = new List<YamlEntry>();
                while (i < text.Length)
                {
                    SkipWs(text, ref i);
                    if (i >= text.Length || text[i] == '}')
                    {
                        if (i < text.Length)
                        {
                            i++;
                        }

                        break;
                    }

                    var key = ParseFlowKey(text, ref i);
                    SkipWs(text, ref i);
                    if (i < text.Length && text[i] == ':')
                    {
                        i++;
                    }

                    var childForce = forceString || stringKeys.Contains(key);
                    var val = ParseFlowNode(text, ref i, childForce);
                    entries.Add(new YamlEntry(key, val, [], null));
                    SkipWs(text, ref i);
                    if (i < text.Length && text[i] == ',')
                    {
                        i++;
                    }
                }

                return new YamlMapping(entries, [], []);
            }

            if (text[i] == '"')
            {
                var raw = ReadFlowDoubleQuoted(text, ref i);
                return new YamlScalar(UnquoteDouble(raw), ScalarStyle.DoubleQuoted, ForceString: true);
            }

            if (text[i] == '\'')
            {
                var raw = ReadFlowSingleQuoted(text, ref i);
                return new YamlScalar(UnquoteSingle(raw), ScalarStyle.SingleQuoted, ForceString: true);
            }

            var start = i;
            while (i < text.Length && text[i] is not ',' and not ']' and not '}')
            {
                i++;
            }

            var scalar = text[start..i].Trim();
            return new YamlScalar(scalar, ScalarStyle.Plain, forceString);
        }

        private static string ParseFlowKey(string text, ref int i)
        {
            SkipWs(text, ref i);
            if (i < text.Length && text[i] == '"')
            {
                return UnquoteDouble(ReadFlowDoubleQuoted(text, ref i));
            }

            if (i < text.Length && text[i] == '\'')
            {
                return UnquoteSingle(ReadFlowSingleQuoted(text, ref i));
            }

            var start = i;
            while (i < text.Length && text[i] is not ':' and not '}' and not ',')
            {
                i++;
            }

            return text[start..i].Trim();
        }

        private static string ReadFlowDoubleQuoted(string text, ref int i)
        {
            var start = i++;
            var escape = false;
            while (i < text.Length)
            {
                var c = text[i++];
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
                    break;
                }
            }

            return text[start..i];
        }

        private static string ReadFlowSingleQuoted(string text, ref int i)
        {
            var start = i++;
            while (i < text.Length)
            {
                if (text[i] == '\'')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i += 2;
                        continue;
                    }

                    i++;
                    break;
                }

                i++;
            }

            return text[start..i];
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: replaced in T11")]
        private string ParseBlockScalar(string header, int parentIndent)
        {
            var folded = header[0] == '>';
            var chomp = header.Contains('-') ? '-' : header.Contains('+') ? '+' : ' ';
            var explicitIndent = 0;
            foreach (var c in header[1..])
            {
                if (c is >= '1' and <= '9')
                {
                    explicitIndent = parentIndent + (c - '0');
                }
            }

            var blockLines = new List<string>();
            var contentIndent = explicitIndent;

            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                if (string.IsNullOrWhiteSpace(line))
                {
                    blockLines.Add("");
                    _pos++;
                    continue;
                }

                var ind = IndentOf(line);
                if (ind <= parentIndent)
                {
                    break;
                }

                if (contentIndent == 0)
                {
                    contentIndent = ind;
                }
                else if (ind < contentIndent)
                {
                    break;
                }

                blockLines.Add(line.Length >= contentIndent ? line[contentIndent..] : line.TrimStart());
                _pos++;
            }

            var trailingBlanks = 0;
            while (blockLines.Count > 0 && blockLines[^1].Length == 0)
            {
                blockLines.RemoveAt(blockLines.Count - 1);
                trailingBlanks++;
            }

            string body;
            if (!folded)
            {
                body = string.Join('\n', blockLines);
            }
            else
            {
                var sb = new StringBuilder();
                for (var k = 0; k < blockLines.Count; k++)
                {
                    var cur = blockLines[k];
                    if (k == 0)
                    {
                        sb.Append(cur);
                        continue;
                    }

                    var prev = blockLines[k - 1];
                    if (cur.Length == 0)
                    {
                        sb.Append('\n');
                    }
                    else if (prev.Length == 0)
                    {
                        sb.Append(cur);
                    }
                    else if (char.IsWhiteSpace(cur[0]) || char.IsWhiteSpace(prev[0]))
                    {
                        sb.Append('\n').Append(cur);
                    }
                    else
                    {
                        sb.Append(' ').Append(cur);
                    }
                }

                body = sb.ToString();
            }

            if (body.Length == 0)
            {
                return chomp == '+' ? new string('\n', trailingBlanks) : "";
            }

            return chomp switch
            {
                '-' => body,
                '+' => body + new string('\n', trailingBlanks + 1),
                _ => body + "\n",
            };
        }

        private List<string> CollectCommentsAndBlanks()
        {
            var comments = new List<string>();
            while (_pos < lines.Count)
            {
                var line = lines[_pos];
                if (string.IsNullOrWhiteSpace(line))
                {
                    _pos++;
                    continue;
                }

                var trimmed = line.TrimStart();
                if (trimmed.StartsWith('#'))
                {
                    comments.Add(StripCommentHash(trimmed));
                    _pos++;
                    continue;
                }

                break;
            }

            return comments;
        }

        private static bool IsSequenceItem(string trimmed) =>
            trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal);

        private static int IndentOf(string line)
        {
            var i = 0;
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            return i;
        }

        private static string StripCommentHash(string trimmed)
        {
            var body = trimmed[1..];
            return body.StartsWith(' ') ? body[1..] : body;
        }

        private static void SkipWs(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: replaced in T11")]
        private static (string Value, string? Comment) SplitInlineComment(string text)
        {
            var inSingle = false;
            var inDouble = false;
            var escape = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (inDouble)
                {
                    if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inDouble = false;
                    }

                    continue;
                }

                if (inSingle)
                {
                    if (c == '\'' && i + 1 < text.Length && text[i + 1] == '\'')
                    {
                        i++;
                    }
                    else if (c == '\'')
                    {
                        inSingle = false;
                    }

                    continue;
                }

                if (c == '"')
                {
                    inDouble = true;
                }
                else if (c == '\'')
                {
                    inSingle = true;
                }
                else if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
                {
                    var val = text[..i].TrimEnd();
                    var comment = StripCommentHash(text[i..]);
                    return (val, comment);
                }
            }

            return (text.TrimEnd(), null);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: replaced in T11")]
        private static bool TrySplitKeyValue(string trimmed, out string key, out string rest)
        {
            key = "";
            rest = "";
            if (trimmed.Length == 0)
            {
                return false;
            }

            if (trimmed[0] == '"')
            {
                var i = 0;
                var raw = ReadFlowDoubleQuoted(trimmed, ref i);
                SkipWs(trimmed, ref i);
                if (i < trimmed.Length && trimmed[i] == ':' && (i + 1 == trimmed.Length || char.IsWhiteSpace(trimmed[i + 1])))
                {
                    key = UnquoteDouble(raw);
                    rest = trimmed[(i + 1)..];
                    return true;
                }

                return false;
            }

            if (trimmed[0] == '\'')
            {
                var i = 0;
                var raw = ReadFlowSingleQuoted(trimmed, ref i);
                SkipWs(trimmed, ref i);
                if (i < trimmed.Length && trimmed[i] == ':' && (i + 1 == trimmed.Length || char.IsWhiteSpace(trimmed[i + 1])))
                {
                    key = UnquoteSingle(raw);
                    rest = trimmed[(i + 1)..];
                    return true;
                }

                return false;
            }

            for (var i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == ':' && (i + 1 == trimmed.Length || char.IsWhiteSpace(trimmed[i + 1])))
                {
                    key = trimmed[..i].Trim();
                    rest = trimmed[(i + 1)..];
                    return key.Length > 0;
                }
            }

            return false;
        }

        private static string UnquoteSingle(string raw)
        {
            if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            {
                return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            return raw;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: replaced in T11")]
        private static string UnquoteDouble(string raw)
        {
            if (raw.Length < 2 || raw[0] != '"' || raw[^1] != '"')
            {
                return raw;
            }

            try
            {
                return JsonSerializer.Deserialize<string>(raw) ?? raw[1..^1];
            }
            catch
            {
                // Fallback for YAML-specific escapes such as \0, \a, \v, \e, \_, \N, \L, \P, \xHH.
                var inner = raw[1..^1];
                var sb = new StringBuilder(inner.Length);
                for (var i = 0; i < inner.Length; i++)
                {
                    if (inner[i] != '\\' || i + 1 >= inner.Length)
                    {
                        sb.Append(inner[i]);
                        continue;
                    }

                    var next = inner[++i];
                    switch (next)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case '0': sb.Append('\0'); break;
                        case ' ': sb.Append(' '); break;
                        case 'x' when i + 2 < inner.Length && int.TryParse(inner.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx):
                            sb.Append((char)hx);
                            i += 2;
                            break;
                        case 'u' when i + 4 < inner.Length && int.TryParse(inner.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hu):
                            sb.Append((char)hu);
                            i += 4;
                            break;
                        default:
                            sb.Append(next);
                            break;
                    }
                }

                return sb.ToString();
            }
        }
    }

    private static class Emitter
    {
        public static string EmitRoot(YamlNode root, bool rootLeadingCommentsOutside)
        {
            var sb = new StringBuilder();
            if (rootLeadingCommentsOutside && root is YamlMapping rm && rm.LeadingComments.Count > 0)
            {
                foreach (var c in rm.LeadingComments)
                {
                    AppendComment(sb, "", c);
                    sb.Append('\n');
                }

                root = rm with { LeadingComments = [] };
            }
            else if (rootLeadingCommentsOutside && root is YamlSequence rs && rs.LeadingComments.Count > 0)
            {
                foreach (var c in rs.LeadingComments)
                {
                    AppendComment(sb, "", c);
                    sb.Append('\n');
                }

                root = rs with { LeadingComments = [] };
            }

            EmitNode(sb, root, 0);
            return sb.ToString();
        }

        private static void EmitNode(StringBuilder sb, YamlNode node, int indent)
        {
            switch (node)
            {
                case YamlScalar s:
                    sb.Append(FormatScalar(s));
                    break;
                case YamlMapping map:
                    EmitMapping(sb, map, indent);
                    break;
                case YamlSequence seq:
                    EmitSequence(sb, seq, indent);
                    break;
            }
        }

        private static void EmitMapping(StringBuilder sb, YamlMapping map, int indent)
        {
            if (map.Entries.Count == 0 && map.LeadingComments.Count == 0 && map.TrailingComments.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            var pad = new string(' ', indent);
            var childPad = new string(' ', indent + 2);
            sb.Append("{\n");

            foreach (var c in map.LeadingComments)
            {
                AppendComment(sb, childPad, c);
                sb.Append('\n');
            }

            for (var i = 0; i < map.Entries.Count; i++)
            {
                var entry = map.Entries[i];
                foreach (var c in entry.LeadingComments)
                {
                    AppendComment(sb, childPad, c);
                    sb.Append('\n');
                }

                sb.Append(childPad);
                sb.Append(JsonString(entry.Key));
                sb.Append(": ");
                EmitNode(sb, entry.Value, indent + 2);
                if (i < map.Entries.Count - 1)
                {
                    sb.Append(',');
                }

                if (entry.InlineComment is not null)
                {
                    sb.Append(' ');
                    AppendComment(sb, "", entry.InlineComment);
                }

                sb.Append('\n');
            }

            foreach (var c in map.TrailingComments)
            {
                AppendComment(sb, childPad, c);
                sb.Append('\n');
            }

            sb.Append(pad).Append('}');
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: replaced in T11")]
        private static void EmitSequence(StringBuilder sb, YamlSequence seq, int indent)
        {
            if (seq.Items.Count == 0 && seq.LeadingComments.Count == 0 && seq.TrailingComments.Count == 0)
            {
                sb.Append("[]");
                return;
            }

            var hasComments = seq.LeadingComments.Count > 0 || seq.TrailingComments.Count > 0 ||
                seq.Items.Any(item => item.LeadingComments.Count > 0 || item.InlineComment is not null);
            var allSimpleScalars = !hasComments && seq.Items.All(item => item.Value is YamlScalar s && !s.Raw.Contains('\n'));

            if (allSimpleScalars && (seq.IsFlow || seq.Items.Count <= 6))
            {
                var parts = seq.Items.Select(item => FormatScalar((YamlScalar)item.Value)).ToList();
                var inline = "[" + string.Join(", ", parts) + "]";
                if (inline.Length <= 100)
                {
                    sb.Append(inline);
                    return;
                }
            }

            var pad = new string(' ', indent);
            var childPad = new string(' ', indent + 2);
            sb.Append("[\n");

            foreach (var c in seq.LeadingComments)
            {
                AppendComment(sb, childPad, c);
                sb.Append('\n');
            }

            for (var i = 0; i < seq.Items.Count; i++)
            {
                var item = seq.Items[i];
                foreach (var c in item.LeadingComments)
                {
                    AppendComment(sb, childPad, c);
                    sb.Append('\n');
                }

                sb.Append(childPad);
                EmitNode(sb, item.Value, indent + 2);
                if (i < seq.Items.Count - 1)
                {
                    sb.Append(',');
                }

                if (item.InlineComment is not null)
                {
                    sb.Append(' ');
                    AppendComment(sb, "", item.InlineComment);
                }

                sb.Append('\n');
            }

            foreach (var c in seq.TrailingComments)
            {
                AppendComment(sb, childPad, c);
                sb.Append('\n');
            }

            sb.Append(pad).Append(']');
        }

        private static string FormatScalar(YamlScalar scalar)
        {
            if (scalar.Style != ScalarStyle.Plain)
            {
                return JsonString(scalar.Raw);
            }

            var raw = scalar.Raw.Trim();
            if (raw.Length == 0 || raw is "null" or "Null" or "NULL" or "~")
            {
                return "null";
            }

            if (!scalar.ForceString)
            {
                if (raw is "true" or "True" or "TRUE")
                {
                    return "true";
                }

                if (raw is "false" or "False" or "FALSE")
                {
                    return "false";
                }

                if (JsonIntegerRegex.IsMatch(raw) && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return raw;
                }

                if (JsonFloatRegex.IsMatch(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    return raw;
                }
            }

            return JsonString(raw);
        }

        private static string JsonString(string value) => JsonSerializer.Serialize(value, JsonStringOptions);

        private static void AppendComment(StringBuilder sb, string pad, string comment)
        {
            sb.Append(pad);
            if (string.IsNullOrEmpty(comment))
            {
                sb.Append("//");
            }
            else
            {
                sb.Append("// ").Append(comment);
            }
        }
    }
}
