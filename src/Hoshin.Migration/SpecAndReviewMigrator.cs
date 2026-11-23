using System.Text.RegularExpressions;
using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Migration;

internal static partial class SpecAndReviewMigrator
{
    [GeneratedRegex(@"^(\s*)Rule:\s*(R\d+)\s*[-\u2013\u2014]\s*(.*?)\s*\(from:\s*(.*)\)\s*$")]
    private static partial Regex SpecRuleWithFromRegex();

    [GeneratedRegex(@"^(\s*)Rule:\s*(R\d+)\s*[-\u2013\u2014]\s*(.*?)\s*$")]
    private static partial Regex SpecRulePlainRegex();

    [GeneratedRegex(@"^(\s*)Scenario(\s+Outline)?:\s*(R\d+\.\d+)\s*[-\u2013\u2014]\s*(.*?)\s*$")]
    private static partial Regex SpecScenarioRegex();

    [GeneratedRegex(@"^\s*@R\d+", RegexOptions.Multiline)]
    private static partial Regex SpecHasLayout2TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"^## Review (\d+)\s*$")]
    private static partial Regex ReviewHeadingRegex();

    [GeneratedRegex(@"^### (F\d+)\s*$")]
    private static partial Regex FindingHeadingRegex();

    [GeneratedRegex(@"^#{1,2} ")]
    private static partial Regex TopHeadingRegex();

    [GeneratedRegex(@"^#{1,3} ")]
    private static partial Regex AnyHeadingRegex();

    public static MigrationTransformResult UpgradeSpecBody(List<string> cur, bool changed)
    {
        var doc = Doc.Split(string.Join('\n', cur));
        var gherkinFence = doc.Fences.FirstOrDefault(f => f.Info.Equals("gherkin", StringComparison.OrdinalIgnoreCase));
        if (gherkinFence is null)
        {
            return new MigrationTransformResult(cur, changed);
        }

        var close = gherkinFence.Close < 0 ? cur.Count : gherkinFence.Close;
        var blockLines = cur.GetRange(gherkinFence.Open + 1, close - (gherkinFence.Open + 1));
        if (SpecHasLayout2TagRegex().IsMatch(string.Join('\n', blockLines)))
        {
            return new MigrationTransformResult(cur, changed);
        }

        var (rewritten, gherkinChanged) = RewriteLayout1Gherkin(blockLines);
        if (!gherkinChanged)
        {
            return new MigrationTransformResult(cur, changed);
        }

        var outLines = new List<string>(cur.Count + rewritten.Count - blockLines.Count);
        outLines.AddRange(cur.Take(gherkinFence.Open + 1));
        outLines.AddRange(rewritten);
        outLines.AddRange(cur.Skip(close));
        return new MigrationTransformResult(outLines, Changed: true);
    }

    private static (List<string> Lines, bool Changed) RewriteLayout1Gherkin(List<string> blockLines)
    {
        var rewritten = new List<string>(blockLines.Count);
        var changed = false;

        foreach (var raw in blockLines)
        {
            if (TryRewriteRuleOrScenario(raw, rewritten))
            {
                changed = true;
            }
            else
            {
                rewritten.Add(raw);
            }
        }

        return (rewritten, changed);
    }

    private static bool TryRewriteRuleOrScenario(string raw, List<string> output)
    {
        var mRuleFrom = SpecRuleWithFromRegex().Match(raw);
        if (mRuleFrom.Success)
        {
            var indent = mRuleFrom.Groups[1].Value;
            var id = mRuleFrom.Groups[2].Value;
            var title = mRuleFrom.Groups[3].Value.Trim();
            var fromParts = mRuleFrom.Groups[4].Value
                .Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(s => "@from:" + WhitespaceRunRegex().Replace(s, "-"));
            output.Add($"{indent}{string.Join(' ', [$"@{id}", .. fromParts])}");
            output.Add($"{indent}Rule: {title}");
            return true;
        }

        var mRulePlain = SpecRulePlainRegex().Match(raw);
        if (mRulePlain.Success)
        {
            var indent = mRulePlain.Groups[1].Value;
            output.Add($"{indent}@{mRulePlain.Groups[2].Value}");
            output.Add($"{indent}Rule: {mRulePlain.Groups[3].Value.Trim()}");
            return true;
        }

        var mSc = SpecScenarioRegex().Match(raw);
        if (mSc.Success)
        {
            var indent = mSc.Groups[1].Value;
            var outline = mSc.Groups[2].Success ? " Outline" : "";
            output.Add($"{indent}@{mSc.Groups[3].Value}");
            output.Add($"{indent}Scenario{outline}: {mSc.Groups[4].Value.Trim()}");
            return true;
        }

        return false;
    }

    public static List<(Fence Fence, bool ExpectArray)> FindReviewYamlRecordFences(Doc doc)
    {
        var found = new List<(Fence Fence, bool ExpectArray)>();
        var reviews = new List<int>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (doc.Heading[i] && ReviewHeadingRegex().IsMatch(doc.Lines[i]))
            {
                reviews.Add(i);
            }
        }

        for (var r = 0; r < reviews.Count; r++)
        {
            CollectReviewSectionFences(doc, reviews[r], found);
        }

        return found;
    }

    private static void CollectReviewSectionFences(Doc doc, int revStart, List<(Fence Fence, bool ExpectArray)> found)
    {
        var revEnd = doc.NextHeading(revStart + 1, doc.Lines.Length, TopHeadingRegex());
        var firstFinding = doc.NextHeading(revStart + 1, revEnd, FindingHeadingRegex());

        if (DocumentMigrator.FindYamlBlock(doc, revStart + 1, firstFinding) is { } headerFence)
        {
            found.Add((headerFence, ExpectArray: false));
        }

        var findings = new List<int>();
        for (var i = firstFinding; i < revEnd; i++)
        {
            if (doc.Heading[i] && FindingHeadingRegex().IsMatch(doc.Lines[i]))
            {
                findings.Add(i);
            }
        }

        for (var f = 0; f < findings.Count; f++)
        {
            var fStart = findings[f];
            var bound = f + 1 < findings.Count ? findings[f + 1] : revEnd;
            var fEnd = doc.NextHeading(fStart + 1, bound, AnyHeadingRegex());
            if (DocumentMigrator.FindYamlBlock(doc, fStart + 1, fEnd) is { } findingFence)
            {
                found.Add((findingFence, ExpectArray: false));
            }
        }
    }
}
