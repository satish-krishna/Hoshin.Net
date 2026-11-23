using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

internal static partial class BuildLogMigrator
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^### (T\d+)\s*$")]
    private static partial Regex BuildEntryHeadingRegex();

    [GeneratedRegex(@"^[-: ]+$")]
    private static partial Regex BuildTableSeparatorCellRegex();

    [GeneratedRegex(@"^T\d+$")]
    private static partial Regex TaskIdTokenRegex();

    [GeneratedRegex(@"^#{1,3} ")]
    private static partial Regex AnyHeadingRegex();

    public static MigrationTransformResult UpgradeBuildLogBody(
        List<string> cur,
        bool changed,
        Func<Doc, List<(Fence Fence, bool ExpectArray)>, List<string>> rewriteYamlFences)
    {
        var doc = Doc.Split(string.Join('\n', cur));
        var tableUpgraded = MigrateBuildLogTables(doc);
        if (tableUpgraded.Changed)
        {
            cur = tableUpgraded.Lines;
            changed = true;
            doc = Doc.Split(string.Join('\n', cur));
        }

        var yamlRewrites = FindBuildLogYamlRecordFences(doc);
        if (yamlRewrites.Count > 0)
        {
            cur = rewriteYamlFences(doc, yamlRewrites);
            changed = true;
        }

        return new MigrationTransformResult(cur, changed);
    }

    private static MigrationTransformResult MigrateBuildLogTables(Doc doc)
    {
        var replacements = new List<(int Start, int EndExclusive, List<string> Replacement)>();
        var i = 0;
        while (i < doc.Lines.Length)
        {
            if (doc.Fenced[i])
            {
                i++;
                continue;
            }

            var header = BuildTableCells(doc.Lines[i]);
            if (header is null || header.Count == 0 || !header[0].Equals("task", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            var tableStart = i;
            var entries = ReadBuildTableEntries(doc, header, ref i);
            replacements.Add((tableStart, i, RenderBuildLogEntries(entries)));
        }

        return replacements.Count == 0
            ? new MigrationTransformResult([.. doc.Lines], Changed: false)
            : new MigrationTransformResult(DocumentMigrator.ApplyReplacements(doc.Lines, replacements), Changed: true);
    }

    private static List<(string Task, List<string> Commits, string? Departure)> ReadBuildTableEntries(Doc doc, List<string> header, ref int i)
    {
        var findCommits = header.FindIndex(c => c.Equals("commits", StringComparison.OrdinalIgnoreCase));
        var findDeparture = header.FindIndex(c => c.Equals("departure", StringComparison.OrdinalIgnoreCase));
        var commitsAt = findCommits >= 0 ? findCommits : 1;
        var departureAt = findDeparture >= 0 ? findDeparture : 2;

        var entries = new List<(string Task, List<string> Commits, string? Departure)>();
        i++;

        while (i < doc.Lines.Length && !doc.Fenced[i])
        {
            var row = BuildTableCells(doc.Lines[i]);
            if (row is null)
            {
                break;
            }

            if (IsBuildTableSeparator(row))
            {
                i++;
                continue;
            }

            if (row.Count > 0 && TaskIdTokenRegex().IsMatch(row[0]))
            {
                entries.Add(ParseBuildRow(row, commitsAt, departureAt));
            }

            i++;
        }

        return entries;
    }

    private static (string Task, List<string> Commits, string? Departure) ParseBuildRow(List<string> row, int commitsAt, int departureAt)
    {
        string Cell(int idx) => idx < row.Count ? row[idx] : "";
        var last = departureAt + 1 == row.Count || departureAt >= row.Count;
        var rawDep = (last ? Cell(departureAt) : string.Join('|', row.Skip(departureAt))).Trim();
        var departure = rawDep.Length == 0 || rawDep.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : rawDep;
        return (row[0], CommitsInCell(Cell(commitsAt)), departure);
    }

    private static List<string> RenderBuildLogEntries(List<(string Task, List<string> Commits, string? Departure)> entries)
    {
        var rendered = new List<string>();
        for (var k = 0; k < entries.Count; k++)
        {
            var e = entries[k];
            if (k > 0)
            {
                rendered.Add("");
            }

            rendered.Add($"### {e.Task}");
            rendered.Add("");
            rendered.Add("```jsonc");
            rendered.Add("{");
            rendered.Add($"  \"task\": {JsonSerializer.Serialize(e.Task, CompactJsonOptions)},");
            var commitsJson = "[" + string.Join(", ", e.Commits.Select(c => JsonSerializer.Serialize(c, CompactJsonOptions))) + "]";
            if (e.Departure is null)
            {
                rendered.Add($"  \"commits\": {commitsJson}");
            }
            else
            {
                rendered.Add($"  \"commits\": {commitsJson},");
                rendered.Add($"  \"departure\": {JsonSerializer.Serialize(e.Departure, CompactJsonOptions)}");
            }

            rendered.Add("}");
            rendered.Add("```");
        }

        return rendered;
    }

    private static List<string>? BuildTableCells(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('|'))
        {
            return null;
        }

        var inner = t[1..];
        if (inner.EndsWith('|'))
        {
            inner = inner[..^1];
        }

        return inner.Split('|').Select(c => c.Trim()).ToList();
    }

    private static bool IsBuildTableSeparator(List<string> row) =>
        row.Count > 0 && row.All(c => c.Length > 0 && BuildTableSeparatorCellRegex().IsMatch(c));

    private static List<string> CommitsInCell(string cell)
    {
        IEnumerable<string> parts = cell.Contains('`')
            ? cell.Split('`').Where((_, idx) => idx % 2 == 1)
            : cell.Split(',');
        return parts.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    private static List<(Fence Fence, bool ExpectArray)> FindBuildLogYamlRecordFences(Doc doc)
    {
        var found = new List<(Fence Fence, bool ExpectArray)>();
        var heads = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && BuildEntryHeadingRegex().IsMatch(doc.Lines[i]))
            {
                heads.Add(i);
            }
        }

        for (var n = 0; n < heads.Count; n++)
        {
            var start = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1] : doc.Lines.Length;
            var end = doc.NextHeading(start + 1, limit, AnyHeadingRegex());
            if (DocumentMigrator.FindYamlBlock(doc, start + 1, end) is { } yamlFence)
            {
                found.Add((yamlFence, ExpectArray: false));
            }
        }

        return found;
    }
}
