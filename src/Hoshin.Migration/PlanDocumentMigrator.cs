using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

internal static partial class PlanDocumentMigrator
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [GeneratedRegex(@"^### (T\d+): (.*?)\s+\(`([\p{L}\p{N}_.-]+)`\)\s*$")]
    private static partial Regex PlanHeading1Regex();

    [GeneratedRegex(@"^### (T\d+):\s*(.*?)\s*$")]
    private static partial Regex PlanHeading2Regex();

    [GeneratedRegex(@"^#{1,3} ")]
    private static partial Regex PlanAnyHeadingRegex();

    [GeneratedRegex(@"^#{1,2} ")]
    private static partial Regex PlanTopHeadingRegex();

    [GeneratedRegex(@"^#### ")]
    private static partial Regex PlanSectionHeadingRegex();

    [GeneratedRegex(@"^## Shared decisions\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlanSharedHeadingRegex();

    [GeneratedRegex(@"^T\d+$")]
    private static partial Regex TaskIdTokenRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphaNumRegex();

    [GeneratedRegex(@"^:?-+:?$")]
    private static partial Regex TableSeparatorCellRegex();

    public static MigrationTransformResult UpgradePlanBody(
        List<string> cur,
        bool changed,
        Func<Doc, List<(Fence Fence, bool ExpectArray)>, List<string>> rewriteYamlFences)
    {
        var doc = Doc.Split(string.Join('\n', cur));
        var hasLayout1Tasks = Enumerable.Range(0, doc.Lines.Length)
            .Any(i => doc.Heading[i] && PlanHeading1Regex().IsMatch(doc.Lines[i]));

        if (hasLayout1Tasks || HasLayout1SharedDecisionsTable(doc))
        {
            var upgraded = MigrateLayout1Plan(doc);
            if (upgraded.Changed)
            {
                cur = upgraded.Lines;
                changed = true;
                doc = Doc.Split(string.Join('\n', cur));
            }
        }

        var yamlRewrites = FindPlanYamlRecordFences(doc);
        if (yamlRewrites.Count > 0)
        {
            cur = rewriteYamlFences(doc, yamlRewrites);
            changed = true;
        }

        return new MigrationTransformResult(cur, changed);
    }

    private static bool HasLayout1SharedDecisionsTable(Doc doc)
    {
        var heading = doc.NextHeading(0, doc.Lines.Length, PlanSharedHeadingRegex());
        if (heading >= doc.Lines.Length)
        {
            return false;
        }

        var end = doc.NextHeading(heading + 1, doc.Lines.Length, PlanTopHeadingRegex());
        if (doc.FieldsBlock(heading + 1, end) is not null || DocumentMigrator.FindYamlBlock(doc, heading + 1, end) is not null)
        {
            return false;
        }

        for (var i = heading + 1; i < end; i++)
        {
            if (!doc.Fenced[i] && doc.Lines[i].TrimStart().StartsWith('|'))
            {
                return true;
            }
        }

        return false;
    }

    private static MigrationTransformResult MigrateLayout1Plan(Doc doc)
    {
        var coversByTask = PlanTableHelpers.ExtractScenarioCoverage(doc);
        var replacements = new List<(int Start, int EndExclusive, List<string> Replacement)>();

        if (TryBuildSharedDecisionsReplacement(doc) is { } sharedRep)
        {
            replacements.Add(sharedRep);
        }

        CollectLayout1TaskReplacements(doc, coversByTask, replacements);

        return replacements.Count == 0
            ? new MigrationTransformResult([.. doc.Lines], Changed: false)
            : new MigrationTransformResult(DocumentMigrator.ApplyReplacements(doc.Lines, replacements), Changed: true);
    }

    private static (int Start, int EndExclusive, List<string> Replacement)? TryBuildSharedDecisionsReplacement(Doc doc)
    {
        var sharedHeading = doc.NextHeading(0, doc.Lines.Length, PlanSharedHeadingRegex());
        if (sharedHeading >= doc.Lines.Length)
        {
            return null;
        }

        var sharedEnd = doc.NextHeading(sharedHeading + 1, doc.Lines.Length, PlanTopHeadingRegex());
        if (doc.FieldsBlock(sharedHeading + 1, sharedEnd) is not null || DocumentMigrator.FindYamlBlock(doc, sharedHeading + 1, sharedEnd) is not null)
        {
            return null;
        }

        var (tableStart, tableEnd, decisions) = ParseSharedDecisionsTable(doc, sharedHeading + 1, sharedEnd);
        if (tableStart < 0 || tableEnd <= tableStart)
        {
            return null;
        }

        var block = new List<string> { "```jsonc" };
        var json = JsonSerializer.Serialize(decisions, PrettyJsonOptions).Replace("\r\n", "\n");
        block.AddRange(json.Split('\n'));
        block.Add("```");
        return (tableStart, tableEnd, block);
    }

    private static (int Start, int End, List<Dictionary<string, string>> Decisions) ParseSharedDecisionsTable(Doc doc, int from, int end)
    {
        var tableStart = -1;
        var tableEnd = -1;
        var decisions = new List<Dictionary<string, string>>();

        for (var i = from; i < end; i++)
        {
            if (doc.Fenced[i])
            {
                continue;
            }

            var cells = PlanTableHelpers.PlanTableCells(doc.Lines[i]);
            if (cells is null)
            {
                if (tableStart >= 0)
                {
                    break;
                }

                continue;
            }

            if (tableStart < 0)
            {
                tableStart = i;
            }

            tableEnd = i + 1;
            if (cells.Count < 3)
            {
                continue;
            }

            var question = cells[0];
            if (TableSeparatorCellRegex().IsMatch(question) ||
                question.Equals("question", StringComparison.OrdinalIgnoreCase) ||
                question.StartsWith('<'))
            {
                continue;
            }

            decisions.Add(new Dictionary<string, string>
            {
                ["question"] = question,
                ["owner"] = cells[1],
                ["function"] = cells[2],
            });
        }

        return (tableStart, tableEnd, decisions);
    }

    private static void CollectLayout1TaskReplacements(
        Doc doc,
        Dictionary<string, List<string>> coversByTask,
        List<(int Start, int EndExclusive, List<string> Replacement)> replacements)
    {
        var heads = new List<(int Index, string Id, string Name, string Repo)>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && PlanHeading1Regex().Match(doc.Lines[i]) is { Success: true } m)
            {
                heads.Add((i, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
            }
        }

        var priorIds = new List<string>();
        for (var n = 0; n < heads.Count; n++)
        {
            var h = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1].Index : doc.Lines.Length;
            var end = doc.NextHeading(h.Index + 1, limit, PlanAnyHeadingRegex());
            var contentEnd = end;
            while (contentEnd > h.Index + 1 && string.IsNullOrWhiteSpace(doc.Lines[contentEnd - 1]))
            {
                contentEnd--;
            }

            var outTask = RenderLayout1Task(h.Id, h.Name, h.Repo, doc.Lines[(h.Index + 1)..contentEnd], priorIds, coversByTask);
            replacements.Add((h.Index, contentEnd, outTask));
            priorIds.Add(h.Id);
        }
    }

    private static List<string> RenderLayout1Task(
        string id,
        string name,
        string repo,
        string[] bodyLines,
        List<string> priorIds,
        Dictionary<string, List<string>> coversByTask)
    {
        var dependsLine = bodyLines.FirstOrDefault(l => l.TrimStart().StartsWith("- **Depends on:**", StringComparison.Ordinal));
        List<string> dependsOn = dependsLine is null
            ? [.. priorIds]
            : NonAlphaNumRegex().Split(dependsLine.TrimStart()["- **Depends on:**".Length..])
                .Where(w => TaskIdTokenRegex().IsMatch(w))
                .ToList();

        var outTask = new List<string>
        {
            $"### {id}: {name}",
            "",
            "```jsonc",
            "{",
            $"  \"id\": {JsonSerializer.Serialize(id, CompactJsonOptions)},",
            $"  \"repo\": {JsonSerializer.Serialize(repo, CompactJsonOptions)},",
        };

        var covers = coversByTask.TryGetValue(id, out var cList) && cList.Count > 0 ? cList : null;
        var depJson = "[" + string.Join(", ", dependsOn.Select(d => JsonSerializer.Serialize(d, CompactJsonOptions))) + "]";
        outTask.Add(covers is null ? $"  \"depends_on\": {depJson}" : $"  \"depends_on\": {depJson},");
        if (covers is not null)
        {
            outTask.Add($"  \"covers\": [{string.Join(", ", covers.Select(c => JsonSerializer.Serialize(c, CompactJsonOptions)))}]");
        }

        outTask.Add("}");
        outTask.Add("```");

        AppendRenderedSections(outTask, PlanTableHelpers.ParseBulletSections(bodyLines));
        return outTask;
    }

    private static void AppendRenderedSections(List<string> outTask, List<(string Title, string Body)> sections)
    {
        foreach (var (title, body) in sections)
        {
            if (string.IsNullOrEmpty(title))
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    outTask.Add("");
                    outTask.AddRange(body.Split('\n'));
                }

                continue;
            }

            outTask.Add("");
            outTask.Add($"#### {title}");
            if (!string.IsNullOrWhiteSpace(body))
            {
                outTask.Add("");
                outTask.AddRange(body.Split('\n'));
            }
        }
    }

    private static List<(Fence Fence, bool ExpectArray)> FindPlanYamlRecordFences(Doc doc)
    {
        var found = new List<(Fence Fence, bool ExpectArray)>();
        var sharedHeading = doc.NextHeading(0, doc.Lines.Length, PlanSharedHeadingRegex());
        if (sharedHeading < doc.Lines.Length)
        {
            var sharedEnd = doc.NextHeading(sharedHeading + 1, doc.Lines.Length, PlanTopHeadingRegex());
            if (DocumentMigrator.FindYamlBlock(doc, sharedHeading + 1, sharedEnd) is { } yamlFence)
            {
                found.Add((yamlFence, ExpectArray: true));
            }
        }

        var heads = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && PlanHeading2Regex().IsMatch(doc.Lines[i]))
            {
                heads.Add(i);
            }
        }

        for (var n = 0; n < heads.Count; n++)
        {
            var start = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1] : doc.Lines.Length;
            var end = doc.NextHeading(start + 1, limit, PlanAnyHeadingRegex());
            var fieldsLimit = doc.NextHeading(start + 1, end, PlanSectionHeadingRegex());
            if (DocumentMigrator.FindYamlBlock(doc, start + 1, fieldsLimit) is { } yamlFence)
            {
                found.Add((yamlFence, ExpectArray: false));
            }
        }

        return found;
    }
}
