using System.Text;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

internal static partial class PlanTableHelpers
{
    [GeneratedRegex(@"^- \*\*(.+?):\*\*\s*(.*)$")]
    private static partial Regex PlanFieldBulletRegex();

    [GeneratedRegex(@"^## Scenario coverage\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlanCoverageHeadingRegex();

    [GeneratedRegex(@"^#{1,2} ")]
    private static partial Regex PlanTopHeadingRegex();

    [GeneratedRegex(@"^T\d+$")]
    private static partial Regex TaskIdTokenRegex();

    [GeneratedRegex(@"^R\d+(?:\.\d+)?$")]
    private static partial Regex ScenarioIdRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonAlphaNumRegex();

    public static Dictionary<string, List<string>> ExtractScenarioCoverage(Doc doc)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var heading = doc.NextHeading(0, doc.Lines.Length, PlanCoverageHeadingRegex());
        if (heading >= doc.Lines.Length)
        {
            return result;
        }

        var end = doc.NextHeading(heading + 1, doc.Lines.Length, PlanTopHeadingRegex());
        for (var i = heading + 1; i < end; i++)
        {
            if (doc.Fenced[i])
            {
                continue;
            }

            var cells = PlanTableCells(doc.Lines[i]);
            if (cells is null || cells.Count < 3 || !ScenarioIdRegex().IsMatch(cells[0].Trim()))
            {
                continue;
            }

            var scenario = cells[0].Trim();
            foreach (var taskId in NonAlphaNumRegex().Split(cells[^1]).Where(w => TaskIdTokenRegex().IsMatch(w)).Distinct(StringComparer.Ordinal))
            {
                if (!result.TryGetValue(taskId, out var list))
                {
                    list = [];
                    result[taskId] = list;
                }

                if (!list.Contains(scenario))
                {
                    list.Add(scenario);
                }
            }
        }

        return result;
    }

    public static List<(string Title, string Body)> ParseBulletSections(string[] lines)
    {
        var sections = new List<(string Title, List<string> Lines)> { ("", []) };
        foreach (var line in lines)
        {
            var m = PlanFieldBulletRegex().Match(line);
            if (m.Success)
            {
                sections.Add((m.Groups[1].Value, [m.Groups[2].Value]));
            }
            else
            {
                sections[^1].Lines.Add(sections.Count > 1 ? StripOneOrTwoLeadingSpaces(line) : line);
            }
        }

        return sections
            .Select(s => (s.Title, Body: string.Join('\n', s.Lines).Trim()))
            .Where(s => s.Title.Length > 0 || s.Body.Length > 0)
            .ToList();
    }

    public static List<string>? PlanTableCells(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith('|'))
        {
            return null;
        }

        var cells = new List<string>();
        var cur = new StringBuilder();
        for (var i = 1; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|')
            {
                cur.Append('|');
                i++;
            }
            else if (c == '|')
            {
                cells.Add(cur.ToString().Trim());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }

        if (cur.ToString().Trim().Length > 0)
        {
            cells.Add(cur.ToString().Trim());
        }

        return cells;
    }

    private static string StripOneOrTwoLeadingSpaces(string line) =>
        line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] :
        line.StartsWith(' ') ? line[1..] : line;
}
