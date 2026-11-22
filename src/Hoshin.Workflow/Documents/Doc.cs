using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Markdig;
using Markdig.Syntax;

namespace Hoshin.Workflow.Documents;

/// <summary>A fenced code block as Markdig CommonMark found it: 0-based line indexes, fence char/length, info string.</summary>
public sealed record Fence(int Open, int Close, char Char, int Length, string Info);

/// <summary>A fenced block's text and 0-based line bounds.</summary>
public sealed record Block(string Text, int Open, int First, int Last);

/// <summary>A record heading pattern: any non-fenced line matching <see cref="Prefix"/> must match <see cref="Strict"/>.</summary>
public sealed record RecordHeading(Regex Prefix, Regex Strict, string What);

/// <summary>
/// A Markdown document split into normalized lines with Markdig CommonMark heading and fence metadata.
/// Enforces the unread-record and raw-key-line invariants of GRAMMAR.md for JSONC record blocks.
/// </summary>
public sealed partial class Doc
{
    private static readonly MarkdownPipeline CommonMark = new MarkdownPipelineBuilder().Build();

    [GeneratedRegex(@"^[\s>\-*+0-9.]*(`{3,}|~{3,})")]
    private static partial Regex OpenerRegex();

    [GeneratedRegex(@"^[\s>]*(`{3,}|~{3,})\s*$")]
    private static partial Regex ClosingFenceRegex();

    [GeneratedRegex(@"^\s*[\[{]*\s*""([^""]+)""\s*:")]
    private static partial Regex KeyLineRegex();

    [GeneratedRegex(@"^\s*[\[{,]*\s*""([^""]+)""\s*:")]
    private static partial Regex RawRecordKeyRegex();

    private Doc(string[] lines, bool[] fenced, bool[] heading, List<Fence> fences)
    {
        Lines = lines;
        Fenced = fenced;
        Heading = heading;
        Fences = fences;
    }

    public string[] Lines { get; }

    public bool[] Fenced { get; }

    public bool[] Heading { get; }

    public IReadOnlyList<Fence> Fences { get; }

    public static string Normalize(string source) => source.Replace("\r\n", "\n").Replace('\r', '\n');

    public static Doc Split(string source)
    {
        var text = Normalize(source);
        var lines = text.Split('\n');
        var fenced = new bool[lines.Length];
        var heading = new bool[lines.Length];
        var fences = new List<Fence>();

        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        int LineOf(int offset)
        {
            var index = starts.BinarySearch(offset);
            return index >= 0 ? index : ~index - 1;
        }

        var markdown = Markdown.Parse(text, CommonMark);
        foreach (var node in markdown.Descendants())
        {
            if (node is HeadingBlock h)
            {
                heading[h.Line] = true;
            }
            else if (node is FencedCodeBlock code)
            {
                var open = code.Line;
                var mark = OpenerRegex().Match(lines[open]);
                if (!mark.Success)
                {
                    continue;
                }

                var length = mark.Groups[1].Value.Length;
                var ch = mark.Groups[1].Value[0];
                var last = Math.Min(LineOf(Math.Max(code.Span.End, code.Span.Start)), lines.Length - 1);
                var closeMatch = last > open ? ClosingFenceRegex().Match(lines[last]) : Match.Empty;
                var closed = closeMatch.Success
                    && closeMatch.Groups[1].Value[0] == ch
                    && closeMatch.Groups[1].Value.Length >= length;
                var info = $"{code.Info} {code.Arguments}".Trim();
                fences.Add(new Fence(open, closed ? last : -1, ch, length, info));
                for (var i = open; i <= last; i++)
                {
                    fenced[i] = true;
                }
            }
        }

        fences.Sort((x, y) => x.Open.CompareTo(y.Open));
        return new Doc(lines, fenced, heading, fences);
    }

    public int NextHeading(int from, int limit, Regex re)
    {
        for (var i = from; i < limit; i++)
        {
            if (Heading[i] && re.IsMatch(Lines[i]))
            {
                return i;
            }
        }

        return limit;
    }

    /// <summary>A fields block holds a record: its info string is <c>jsonc</c> or <c>json</c>, in any case.</summary>
    public static bool IsFieldsBlock(Fence f) =>
        f.Info.Equals("jsonc", StringComparison.OrdinalIgnoreCase) || f.Info.Equals("json", StringComparison.OrdinalIgnoreCase);

    public Block BlockOf(Fence f)
    {
        var close = f.Close < 0 ? Lines.Length : f.Close;
        return new Block(string.Join('\n', Lines[(f.Open + 1)..close]), f.Open, f.Open + 1, close - 1);
    }

    public Block? FieldsBlock(int from, int limit)
    {
        var f = Fences.FirstOrDefault(x => IsFieldsBlock(x) && x.Open >= from && x.Open < limit);
        return f == null ? null : BlockOf(f);
    }

    public static Regex RecordPrefix(string word, int topLevel = 1) => new($"^#{{{topLevel},6}}\\s*{word}\\b");

    /// <summary>The 1-based line of the first <c>"key":</c> inside a block, or the block's opening line.</summary>
    public int KeyLine(Block block, string key)
    {
        for (var i = block.First; i <= block.Last; i++)
        {
            var m = KeyLineRegex().Match(Lines[i]);
            if (m.Success && string.Equals(m.Groups[1].Value, key, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return block.Open + 1;
    }

    private bool HoldsRecordKey(Fence f, IReadOnlyList<string> keys)
    {
        var block = BlockOf(f);
        var parsed = Jsonc.TryParse(block.Text, out var value, out _);
        if (parsed)
        {
            return value is { ValueKind: JsonValueKind.Object } v && keys.Any(k => v.TryGetProperty(k, out _));
        }

        return block.Text.Split('\n').Any(line =>
        {
            var m = RawRecordKeyRegex().Match(line);
            return m.Success && keys.Contains(m.Groups[1].Value);
        });
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public void ReportUnread(
        string file,
        List<Problem> problems,
        IReadOnlyList<RecordHeading> headings,
        IReadOnlyList<string> keys,
        Regex rawKey,
        ISet<int> claimed)
    {
        var found = Fences.Where(f => f.Close < 0)
            .Select(f => new Problem(file, f.Open + 1, $"fenced block opened on line {f.Open + 1} is never closed"))
            .ToList();

        var covered = new HashSet<int>();
        void Cover(Fence f)
        {
            var last = f.Close < 0 ? Lines.Length - 1 : f.Close;
            for (var i = f.Open; i <= last; i++)
            {
                covered.Add(i);
            }
        }

        for (var index = 0; index < Lines.Length; index++)
        {
            if (Fenced[index])
            {
                continue;
            }

            var heading = headings.FirstOrDefault(h => h.Prefix.IsMatch(Lines[index]));
            if (heading != null && !heading.Strict.IsMatch(Lines[index]))
            {
                found.Add(new Problem(file, index + 1, $"heading \"{Lines[index].TrimEnd()}\" is not {heading.What}"));
            }
        }

        var keyLines = new HashSet<int>();
        foreach (var f in Fences)
        {
            if (f.Close < 0)
            {
                Cover(f);
            }

            if (claimed.Contains(f.Open))
            {
                var block = BlockOf(f);
                for (var i = block.First; i <= block.Last; i++)
                {
                    if (rawKey.IsMatch(Lines[i]))
                    {
                        keyLines.Add(i);
                        break;
                    }
                }
            }
            else if (f.Open == 0 && IsFieldsBlock(f))
            {
                // Front matter block on line 1 is never an unclaimed record block.
                continue;
            }
            else if (IsFieldsBlock(f) && HoldsRecordKey(f, keys))
            {
                found.Add(new Problem(file, f.Open + 1, "jsonc block is not part of any record"));
                Cover(f);
            }
        }

        for (var index = 0; index < Lines.Length; index++)
        {
            if (rawKey.IsMatch(Lines[index]) && !keyLines.Contains(index) && !covered.Contains(index))
            {
                found.Add(new Problem(file, index + 1, $"{KeyText(Lines[index])} is not part of any record the reader found"));
            }
        }

        problems.AddRange(found.OrderBy(p => p.Line));
    }

    public static string KeyText(string line) => line.Trim().TrimStart('[', '{', ' ').TrimEnd(',', '}', ']', ' ');
}
