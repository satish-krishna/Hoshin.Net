using System.Text;
using System.Text.Json;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Hoshin.Workflow.Migration;

internal sealed record ScannedYamlComment(long Line, string Text, bool IsInline);

internal sealed class YamlJsoncEmitter(List<ScannedYamlComment> comments, YamlConversionOptions options)
{
    private readonly HashSet<int> _consumed = [];

    public List<string> Warnings { get; } = [];

    public static string EmitEmpty(List<ScannedYamlComment> comments, YamlConversionOptions opts)
    {
        var open = opts.ExpectArray ? "[" : "{";
        var close = opts.ExpectArray ? "]" : "}";
        if (comments.Count == 0)
        {
            return open + close;
        }

        var sb = new StringBuilder();
        if (opts.RootLeadingCommentsOutside)
        {
            foreach (var c in comments)
            {
                sb.AppendLine(FormatComment(c.Text));
            }

            sb.Append(open).Append(close);
            return sb.ToString();
        }

        sb.AppendLine(open);
        foreach (var c in comments)
        {
            sb.Append("  ").AppendLine(FormatComment(c.Text));
        }

        sb.Append(close);
        return sb.ToString();
    }

    public string EmitRoot(YamlNode root)
    {
        var sb = new StringBuilder();
        var rootStart = FirstChildStartLine(root);
        if (options.RootLeadingCommentsOutside)
        {
            foreach (var c in TakeBlockCommentsBefore(rootStart))
            {
                sb.AppendLine(FormatComment(c.Text));
            }
        }

        if (root is YamlMappingNode map)
        {
            EmitMapping(sb, map, indent: 0, forceString: false, isRoot: true);
        }
        else if (root is YamlSequenceNode seq)
        {
            EmitSequence(sb, seq, indent: 0, forceString: false, isRoot: true);
        }
        else if (root is YamlScalarNode scalar)
        {
            sb.Append(YamlScalarFormatter.FormatScalar(scalar, forceString: false));
        }

        RecordUnconsumedCommentWarnings();
        return sb.ToString();
    }

    private void EmitMapping(StringBuilder sb, YamlMappingNode map, int indent, bool forceString, bool isRoot)
    {
        var entries = BuildMappingEntries(map, isRoot);
        if (entries.Count == 0)
        {
            var remaining = isRoot ? TakeAllRemainingComments() : [];
            if (remaining.Count == 0)
            {
                sb.Append("{}");
                return;
            }

            sb.AppendLine("{");
            AppendBlockComments(sb, remaining, indent + 2);
            sb.Append(new string(' ', indent)).Append('}');
            return;
        }

        sb.AppendLine("{");
        var inner = new string(' ', indent + 2);

        for (var i = 0; i < entries.Count; i++)
        {
            var (key, value, keyLine, valueEndLine, synthetic) = entries[i];
            if (!synthetic)
            {
                AppendBlockComments(sb, TakeBlockCommentsBefore(keyLine), indent + 2);
            }

            var childForceString = forceString || (options.StringKeys?.Contains(key) ?? false);
            var comma = i + 1 < entries.Count ? "," : "";

            sb.Append(inner).Append(JsonSerializer.Serialize(key, YamlScalarFormatter.StringJsonOptions)).Append(": ");
            EmitValue(sb, value, indent + 2, childForceString);
            sb.Append(comma);

            var inline = synthetic ? null : TakeInlineCommentOn(valueEndLine);
            if (inline is not null)
            {
                sb.Append(' ').Append(FormatComment(inline.Text));
            }

            sb.AppendLine();
        }

        if (isRoot)
        {
            AppendBlockComments(sb, TakeAllRemainingComments(), indent + 2);
        }

        sb.Append(new string(' ', indent)).Append('}');
    }

    private List<(string Key, YamlNode Value, long KeyLine, long ValueEndLine, bool Synthetic)> BuildMappingEntries(YamlMappingNode map, bool isRoot)
    {
        var entries = new List<(string Key, YamlNode Value, long KeyLine, long ValueEndLine, bool Synthetic)>();
        var hasLayout = false;

        foreach (var pair in map.Children)
        {
            var key = pair.Key is YamlScalarNode ks ? ks.Value ?? "" : pair.Key.ToString();
            var value = pair.Value;
            if (isRoot && options.ForceLayout2 && key == "layout")
            {
                hasLayout = true;
                value = new YamlScalarNode("2") { Style = ScalarStyle.Plain };
            }

            var valEnd = Math.Max(pair.Key.End.Line, pair.Value.End.Line);
            entries.Add((key, value, pair.Key.Start.Line, valEnd, false));
        }

        if (isRoot && options.ForceLayout2 && !hasLayout)
        {
            entries.Insert(0, ("layout", new YamlScalarNode("2") { Style = ScalarStyle.Plain }, 0, 0, true));
        }

        return entries;
    }

    private void EmitSequence(StringBuilder sb, YamlSequenceNode seq, int indent, bool forceString, bool isRoot)
    {
        if (seq.Children.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        if (!isRoot && IsSimpleInlineSequence(seq))
        {
            sb.Append('[');
            for (var i = 0; i < seq.Children.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(YamlScalarFormatter.FormatScalar((YamlScalarNode)seq.Children[i], forceString));
            }

            sb.Append(']');
            return;
        }

        sb.AppendLine("[");
        var inner = new string(' ', indent + 2);
        for (var i = 0; i < seq.Children.Count; i++)
        {
            var item = seq.Children[i];
            AppendBlockComments(sb, TakeBlockCommentsBefore(item.Start.Line), indent + 2);
            sb.Append(inner);
            EmitValue(sb, item, indent + 2, forceString);
            if (i + 1 < seq.Children.Count)
            {
                sb.Append(',');
            }

            var inline = TakeInlineCommentOn(item.End.Line);
            if (inline is not null)
            {
                sb.Append(' ').Append(FormatComment(inline.Text));
            }

            sb.AppendLine();
        }

        if (isRoot)
        {
            AppendBlockComments(sb, TakeAllRemainingComments(), indent + 2);
        }

        sb.Append(new string(' ', indent)).Append(']');
    }

    private void EmitValue(StringBuilder sb, YamlNode node, int indent, bool forceString)
    {
        switch (node)
        {
            case YamlMappingNode map:
                EmitMapping(sb, map, indent, forceString, isRoot: false);
                break;
            case YamlSequenceNode seq:
                EmitSequence(sb, seq, indent, forceString, isRoot: false);
                break;
            case YamlScalarNode scalar:
                sb.Append(YamlScalarFormatter.FormatScalar(scalar, forceString));
                break;
            default:
                sb.Append("null");
                break;
        }
    }

    private bool IsSimpleInlineSequence(YamlSequenceNode seq)
    {
        if (seq.Style != SequenceStyle.Flow && seq.Children.Count > 6)
        {
            return false;
        }

        var hasCommentsInSpan = comments.Where((_, idx) => !_consumed.Contains(idx))
            .Any(c => c.Line >= seq.Start.Line && c.Line <= seq.End.Line);
        if (hasCommentsInSpan)
        {
            return false;
        }

        return seq.Children.All(c => c is YamlScalarNode s && (s.Value?.Length ?? 0) <= 60 && !(s.Value?.Contains('\n') ?? false));
    }

    private static string FormatComment(string text) =>
        string.IsNullOrEmpty(text) ? "//" : $"// {text}";

    private List<ScannedYamlComment> TakeBlockCommentsBefore(long line)
    {
        var taken = new List<ScannedYamlComment>();
        for (var i = 0; i < comments.Count; i++)
        {
            if (!_consumed.Contains(i) && !comments[i].IsInline && comments[i].Line < line)
            {
                _consumed.Add(i);
                taken.Add(comments[i]);
            }
        }

        return taken;
    }

    private ScannedYamlComment? TakeInlineCommentOn(long line)
    {
        for (var i = 0; i < comments.Count; i++)
        {
            if (!_consumed.Contains(i) && comments[i].IsInline && comments[i].Line == line)
            {
                _consumed.Add(i);
                return comments[i];
            }
        }

        return null;
    }

    private List<ScannedYamlComment> TakeAllRemainingComments()
    {
        var taken = new List<ScannedYamlComment>();
        for (var i = 0; i < comments.Count; i++)
        {
            if (_consumed.Add(i))
            {
                taken.Add(comments[i]);
            }
        }

        return taken;
    }

    private void RecordUnconsumedCommentWarnings()
    {
        for (var i = 0; i < comments.Count; i++)
        {
            if (!_consumed.Contains(i))
            {
                Warnings.Add($"YAML line {comments[i].Line}: comment could not be preserved: {comments[i].Text}");
            }
        }
    }

    private static void AppendBlockComments(StringBuilder sb, List<ScannedYamlComment> block, int indent)
    {
        var pad = new string(' ', indent);
        foreach (var c in block)
        {
            sb.Append(pad).AppendLine(FormatComment(c.Text));
        }
    }

    private static long FirstChildStartLine(YamlNode root) => root switch
    {
        YamlMappingNode m when m.Children.Count > 0 => m.Children.First().Key.Start.Line,
        YamlSequenceNode s when s.Children.Count > 0 => s.Children[0].Start.Line,
        _ => root.Start.Line,
    };
}
