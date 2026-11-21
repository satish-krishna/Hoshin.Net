using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>Reads a Layout 2 build log (<c>build/&lt;repo&gt;.md</c>): one <c>### T&lt;n&gt;</c> heading + JSONC block per finished task.</summary>
public sealed class BuildLogReader(IJsonSchemaValidator? schemaValidator = null)
{
    private const string EntrySchemaName = "build-log-entry.schema.json";

    private static readonly Regex EntryHeading = new(@"^### (T[0-9]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex AnyHeading = new(@"^#{1,3} ", RegexOptions.Compiled);
    private static readonly Regex RawKey = new(@"^\s*[\[{]*\s*""task""\s*:\s*""T[0-9]+", RegexOptions.Compiled);

    private readonly IJsonSchemaValidator _schema = schemaValidator ?? EmbeddedSchemaValidator.Default;

    public BuildLogReadResult Read(string text, string file)
    {
        var doc = Doc.Split(text);
        var fm = FrontMatter.Parse(doc, file);
        FrontMatter.RefuseNewerLayout(fm.Layout, file);

        var problems = new List<Problem>();
        var repo = Path.GetFileName(file);
        if (repo.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^3];
        }

        var heads = EntryHeads(doc);
        var missingVersion = false;
        if (fm.Layout < 2 && !fm.HasLayoutField)
        {
            var hasBlocks = heads.Select((h, n) => doc.FieldsBlock(h.Index + 1, EntryEnd(doc, heads, n)) is not null).Any(x => x);
            if (hasBlocks)
            {
                problems.Add(new Problem(file, 1, "no layout version"));
                missingVersion = true;
            }
        }

        if (fm.Layout < 2 && !missingVersion)
        {
            FrontMatter.RefuseUnsupportedLayout(fm.Layout, file);
        }

        var claimed = new HashSet<int>();
        var entries = ReadEntries(doc, heads, file, problems, claimed);
        doc.ReportUnread(
            file,
            problems,
            [new RecordHeading(Doc.RecordPrefix("T[0-9]+"), EntryHeading, "an entry heading")],
            ["task"],
            RawKey,
            claimed);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (seen.TryGetValue(entry.Task, out var first))
            {
                problems.Add(new Problem(file, entry.Line, $"entry {entry.Task} is recorded twice, first on line {first}"));
            }
            else
            {
                seen[entry.Task] = entry.Line;
            }
        }

        return new BuildLogReadResult(fm.Layout, fm.Data, new ParsedBuildLog(repo, entries), problems);
    }

    private static List<(int Index, string Id)> EntryHeads(Doc doc)
    {
        var heads = new List<(int Index, string Id)>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (!doc.Heading[i])
            {
                continue;
            }

            var m = EntryHeading.Match(doc.Lines[i]);
            if (m.Success)
            {
                heads.Add((i, m.Groups[1].Value));
            }
        }

        return heads;
    }

    private static int EntryEnd(Doc doc, List<(int Index, string Id)> heads, int n)
    {
        var limit = n + 1 < heads.Count ? heads[n + 1].Index : doc.Lines.Length;
        return doc.NextHeading(heads[n].Index + 1, limit, AnyHeading);
    }

    private List<ParsedBuildEntry> ReadEntries(
        Doc doc,
        List<(int Index, string Id)> heads,
        string file,
        List<Problem> problems,
        HashSet<int> claimed)
    {
        var entries = new List<ParsedBuildEntry>();
        for (var n = 0; n < heads.Count; n++)
        {
            var h = heads[n];
            var line = h.Index + 1;
            var who = $"entry {h.Id}";
            var commits = new List<string>();
            string? departure = null;
            string? red = null;

            var block = doc.FieldsBlock(h.Index + 1, EntryEnd(doc, heads, n));
            if (block is null)
            {
                problems.Add(new Problem(file, line, $"{who} has no fields block"));
                entries.Add(new ParsedBuildEntry(h.Id, commits, departure, red, line));
                continue;
            }

            claimed.Add(block.Open);
            if (!Jsonc.TryParse(block.Text, out var value, out var error))
            {
                problems.Add(new Problem(file, block.Open + 1, $"{who} fields are not valid JSONC: {error}"));
                entries.Add(new ParsedBuildEntry(h.Id, commits, departure, red, line));
                continue;
            }

            if (value is not { ValueKind: JsonValueKind.Object } f)
            {
                problems.Add(new Problem(file, block.Open + 1, $"{who} fields are not a set of fields"));
                entries.Add(new ParsedBuildEntry(h.Id, commits, departure, red, line));
                continue;
            }

            var errors = _schema.Validate(EntrySchemaName, f)
                .Where(e => !(EmbeddedSchemaValidator.IsMissingRequired(e, "commits") || (e.Path == "commits" && e.Keyword == "minItems")));
            foreach (var m in EmbeddedSchemaValidator.FormatMessages(errors, $"{who} fields", plural: true))
            {
                problems.Add(new Problem(file, block.Open + 1, m));
            }

            var taskField = Jsonc.String(f, "task");
            if (taskField is not null && taskField != h.Id)
            {
                problems.Add(new Problem(file, doc.KeyLine(block, "task"), $"heading says {h.Id}, fields say {taskField}"));
            }

            commits = Jsonc.TextList(f, "commits");
            if (commits.Count == 0)
            {
                problems.Add(new Problem(file, line, $"{who} has no commits"));
            }

            var depRaw = Jsonc.ScalarText(f, "departure");
            if (!string.IsNullOrWhiteSpace(depRaw))
            {
                departure = depRaw.Trim();
            }

            var redRaw = Jsonc.ScalarText(f, "red");
            if (!string.IsNullOrWhiteSpace(redRaw))
            {
                red = redRaw.Trim();
            }

            entries.Add(new ParsedBuildEntry(h.Id, commits, departure, red, line));
        }

        return entries;
    }
}
