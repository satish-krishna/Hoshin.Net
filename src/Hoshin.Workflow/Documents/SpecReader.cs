using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Gherkin;
using Gherkin.Ast;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>
/// Reads a Layout 2 <c>spec.md</c> using Cucumber's <c>Gherkin</c> parser and enforces all GRAMMAR.md tag,
/// outline-parameter, and unread-tag rules.
/// </summary>
public sealed partial class SpecReader
{
    private static readonly Regex RuleTag = new(@"^@R[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex ScenarioTag = new(@"^@R([0-9]+)\.[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex FromTag = new(@"^@from:(.+)$", RegexOptions.Compiled);
    private static readonly Regex TagInLine = new(@"(?<![\w@])@R([0-9]+(?:\.[0-9]+)?)(?!\w)", RegexOptions.Compiled);
    private static readonly Regex HasRuleTagLine = new(@"^\s*@R[0-9]", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex PlaceholderRegex = new(@"<([^<>\s][^<>]*)>", RegexOptions.Compiled);

    [GeneratedRegex(@"^\(\d+:\d+\):\s*")]
    private static partial Regex GherkinLocationPrefixRegex();

    private sealed record GherkinBlock(string Text, int Offset);

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance method on injected document reader service.")]
    public SpecReadResult Read(string text, string file)
    {
        var doc = Doc.Split(text);
        var fm = FrontMatter.Parse(doc, file);
        FrontMatter.RefuseNewerLayout(fm.Layout, file);

        var problems = new List<Problem>();
        var block = FindBlock(doc);
        var requirements = new List<ParsedRequirement>();

        var missingVersion = !fm.HasLayoutField && block is not null && HasRuleTagLine.IsMatch(block.Text);
        if (fm.Layout < 2 && !missingVersion)
        {
            FrontMatter.RefuseUnsupportedLayout(fm.Layout, file);
        }

        if (missingVersion)
        {
            problems.Add(new Problem(file, 1, "no layout version"));
        }

        if (block is null)
        {
            problems.Add(new Problem(file, 1, "no gherkin requirements block found"));
            return new SpecReadResult(fm.Layout, fm.Data, new ParsedSpec(requirements), problems);
        }

        var reported = new HashSet<string>(StringComparer.Ordinal);
        if (ReadBlock(block, file, problems, requirements, reported))
        {
            ReportUnreadTags(doc.Lines, file, requirements, reported, problems);
        }

        return new SpecReadResult(fm.Layout, fm.Data, new ParsedSpec(requirements), problems);
    }

    private static GherkinBlock? FindBlock(Doc doc)
    {
        var fence = doc.Fences.FirstOrDefault(f => f.Info.Equals("gherkin", StringComparison.OrdinalIgnoreCase));
        if (fence is null)
        {
            return null;
        }

        var close = fence.Close < 0 ? doc.Lines.Length : fence.Close;
        return new GherkinBlock(string.Join('\n', doc.Lines[(fence.Open + 1)..close]), fence.Open + 1);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private static bool ReadBlock(
        GherkinBlock block,
        string file,
        List<Problem> problems,
        List<ParsedRequirement> output,
        HashSet<string> reported)
    {
        int At(long line) => block.Offset + (int)line;

        GherkinDocument parsed;
        try
        {
            parsed = new Parser().Parse(new StringReader(block.Text));
        }
        catch (CompositeParserException e)
        {
            foreach (var error in e.Errors)
            {
                problems.Add(new Problem(file, error.Location is { } where ? At(where.Line) : At(1), $"gherkin: {Strip(error.Message)}"));
            }

            return false;
        }
        catch (ParserException e)
        {
            problems.Add(new Problem(file, e.Location is { } at ? At(at.Line) : At(1), $"gherkin: {Strip(e.Message)}"));
            return false;
        }

        var seenRequirements = new HashSet<string>(StringComparer.Ordinal);
        var seenScenarios = new HashSet<string>(StringComparer.Ordinal);

        foreach (var child in parsed.Feature?.Children ?? [])
        {
            if (child is not Rule rule)
            {
                if (child is Scenario loose)
                {
                    var tags = loose.Tags.Select(t => t.Name).Where(n => RuleTag.IsMatch(n) || ScenarioTag.IsMatch(n)).ToList();
                    if (tags.Count > 0)
                    {
                        foreach (var t in tags)
                        {
                            reported.Add(t[1..]);
                        }

                        var named = tags.FirstOrDefault(n => ScenarioTag.IsMatch(n)) ?? tags[0];
                        problems.Add(new Problem(file, At(loose.Location.Line), $"scenario {named[1..]} sits under no requirement"));
                    }
                }

                continue;
            }

            var names = rule.Tags.Select(t => t.Name).ToList();
            var ids = names.Where(n => RuleTag.IsMatch(n)).Select(n => n[1..]).ToList();
            var id = ids.FirstOrDefault() ?? "";
            var from = names.Select(n => FromTag.Match(n)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            var requirement = new ParsedRequirement(id, rule.Name, from, [], At(rule.Location.Line));
            output.Add(requirement);

            if (ids.Count == 0)
            {
                problems.Add(new Problem(file, requirement.Line, "requirement has no ID"));
            }
            else
            {
                if (ids.Count > 1)
                {
                    problems.Add(new Problem(file, requirement.Line, $"requirement carries more than one ID: {string.Join(", ", ids)}"));
                }

                if (!seenRequirements.Add(id))
                {
                    problems.Add(new Problem(file, requirement.Line, $"requirement ID {id} is used twice"));
                }
            }

            foreach (var sc in rule.Children.OfType<Scenario>())
            {
                var scIds = sc.Tags.Select(t => t.Name).Where(n => ScenarioTag.IsMatch(n)).Select(n => n[1..]).ToList();
                var scId = scIds.FirstOrDefault() ?? "";
                var first = sc.Examples.FirstOrDefault();
                List<List<string>>? examples = first?.TableHeader is null
                    ? null
                    : [first.TableHeader.Cells.Select(c => c.Value).ToList(), .. sc.Examples.SelectMany(ex => ex.TableBody.Select(r => r.Cells.Select(c => c.Value).ToList()))];
                var isOutline = sc.Examples.Any() || sc.Keyword.Trim() is "Scenario Outline" or "Scenario Template";
                var steps = sc.Steps.Select(s => $"{s.Keyword.Trim()} {s.Text}").ToList();
                var scenario = new ParsedScenario(scId, sc.Name, isOutline, steps, examples, At(sc.Location.Line));
                requirement.Scenarios.Add(scenario);

                if (scIds.Count == 0)
                {
                    problems.Add(new Problem(file, scenario.Line, "scenario has no ID"));
                    continue;
                }

                if (scIds.Count > 1)
                {
                    problems.Add(new Problem(file, scenario.Line, $"scenario carries more than one ID: {string.Join(", ", scIds)}"));
                }

                var owner = ScenarioTag.Match($"@{scId}").Groups[1].Value;
                if (ids.Count > 0 && $"R{owner}" != id)
                {
                    problems.Add(new Problem(file, scenario.Line, $"scenario {scId} sits under requirement {id}"));
                }

                if (!seenScenarios.Add(scId))
                {
                    problems.Add(new Problem(file, scenario.Line, $"scenario ID {scId} is used twice"));
                }

                if (scenario.Outline)
                {
                    var listed = examples?[0] ?? [];
                    var used = sc.Steps
                        .SelectMany(s => PlaceholderRegex.Matches(s.Text).Select(m => m.Groups[1].Value))
                        .Distinct(StringComparer.Ordinal);
                    foreach (var p in used)
                    {
                        if (!listed.Contains(p))
                        {
                            problems.Add(new Problem(file, scenario.Line, $"scenario {scId} uses <{p}> but its examples lack it"));
                        }
                    }
                }
            }

            if (requirement.Scenarios.Count == 0 && ids.Count > 0)
            {
                problems.Add(new Problem(file, requirement.Line, $"requirement {id} has no scenario"));
            }
        }

        return true;
    }

    private static void ReportUnreadTags(
        string[] lines,
        string file,
        List<ParsedRequirement> requirements,
        HashSet<string> reported,
        List<Problem> problems)
    {
        var known = new HashSet<string>(reported, StringComparer.Ordinal);
        foreach (var r in requirements)
        {
            if (r.Id.Length > 0)
            {
                known.Add(r.Id);
            }

            foreach (var s in r.Scenarios)
            {
                if (s.Id.Length > 0)
                {
                    known.Add(s.Id);
                }
            }
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in TagInLine.Matches(lines[i]))
            {
                var id = $"R{m.Groups[1].Value}";
                if (known.Contains(id) || !seen.Add(id))
                {
                    continue;
                }

                var what = id.Contains('.') ? "scenario" : "requirement";
                problems.Add(new Problem(file, i + 1, $"{what} {id} is not part of the requirements the reader found"));
            }
        }
    }

    private static string Strip(string message) => GherkinLocationPrefixRegex().Replace(message, "");
}
