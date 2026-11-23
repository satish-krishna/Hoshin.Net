using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>Reads a Layout 2 review document (<c>build/review.md</c>): <c>## Review &lt;n&gt;</c> sections and <c>### F&lt;n&gt;</c> findings.</summary>
public sealed class ReviewReader(IJsonSchemaValidator schemaValidator)
{
    private const string HeaderSchemaName = "review-header.schema.json";
    private const string FindingSchemaName = "finding.schema.json";

    private static readonly Regex ReviewHeading = new(@"^## Review ([0-9]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex FindingHeading = new(@"^### (F[0-9]+)\s*$", RegexOptions.Compiled);
    private static readonly Regex TopHeading = new(@"^#{1,2} ", RegexOptions.Compiled);
    private static readonly Regex AnyHeading = new(@"^#{1,3} ", RegexOptions.Compiled);
    private static readonly Regex RawKey = new(@"^\s*[\[{]*\s*(""id""\s*:\s*""F[0-9]+|""review""\s*:\s*[0-9]+)", RegexOptions.Compiled);

    private readonly IJsonSchemaValidator _schema = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));

    public ReviewReadResult Read(string text, string file)
    {
        var doc = Doc.Split(text);
        var fm = FrontMatter.Parse(doc, file);
        FrontMatter.RefuseNewerLayout(fm.Layout, file);

        var problems = new List<Problem>();
        if (!fm.HasLayoutField)
        {
            problems.Add(new Problem(file, 1, "no layout version"));
        }
        else if (fm.Layout < 2)
        {
            FrontMatter.RefuseUnsupportedLayout(fm.Layout, file);
        }

        var heads = new List<(int Index, int Number)>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (!doc.Heading[i])
            {
                continue;
            }

            var m = ReviewHeading.Match(doc.Lines[i]);
            if (m.Success)
            {
                if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var num))
                {
                    heads.Add((i, num));
                }
                else
                {
                    problems.Add(new Problem(file, i + 1, $"review number '{m.Groups[1].Value}' is not a valid integer"));
                }
            }
        }

        var claimed = new HashSet<int>();
        if (heads.Count == 0)
        {
            problems.Add(new Problem(file, 1, "review document has no review section"));
        }

        var reviews = new List<ParsedReviewSection>();
        for (var n = 0; n < heads.Count; n++)
        {
            var h = heads[n];
            if (h.Number != n + 1)
            {
                problems.Add(new Problem(file, h.Index + 1, $"review {h.Number} is out of order, expected review {n + 1}"));
            }

            var end = doc.NextHeading(h.Index + 1, doc.Lines.Length, TopHeading);
            reviews.Add(ReadSection(doc, h.Index, h.Number, end, file, problems, claimed));
        }

        doc.ReportUnread(
            file,
            problems,
            [
                new RecordHeading(Doc.RecordPrefix("Review", topLevel: 2), ReviewHeading, "a review heading"),
                new RecordHeading(Doc.RecordPrefix("F[0-9]+"), FindingHeading, "a finding heading"),
            ],
            ["review", "id"],
            RawKey,
            claimed);

        return new ReviewReadResult(fm.Layout, fm.Data, new ParsedReview(reviews), problems);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private ParsedReviewSection ReadSection(
        Doc doc,
        int index,
        int number,
        int end,
        string file,
        List<Problem> problems,
        HashSet<int> claimed)
    {
        var reviewedAt = "";
        var commits = new Dictionary<string, string>(StringComparer.Ordinal);
        var findings = new List<ParsedFinding>();
        var who = $"review {number}";

        var firstFinding = doc.NextHeading(index + 1, end, FindingHeading);
        var block = doc.FieldsBlock(index + 1, firstFinding);
        if (block is null)
        {
            problems.Add(new Problem(file, index + 1, $"{who} has no header block"));
        }
        else
        {
            claimed.Add(block.Open);
            if (!Jsonc.TryParse(block.Text, out var value, out var error))
            {
                problems.Add(new Problem(file, block.Open + 1, $"{who} fields are not valid JSONC: {error}"));
            }
            else if (value is not { ValueKind: JsonValueKind.Object } f)
            {
                problems.Add(new Problem(file, block.Open + 1, $"{who} fields are not a set of fields"));
            }
            else
            {
                var errors = _schema.Validate(HeaderSchemaName, f);
                foreach (var m in EmbeddedSchemaValidator.FormatMessages(errors, $"{who} header", plural: true))
                {
                    problems.Add(new Problem(file, block.Open + 1, m));
                }

                if (f.TryGetProperty("review", out var revProp) && revProp.ValueKind == JsonValueKind.Number && revProp.TryGetInt32(out var revNum) && revNum != number)
                {
                    problems.Add(new Problem(file, doc.KeyLine(block, "review"), $"heading says review {number}, fields say review {revNum}"));
                }

                reviewedAt = Jsonc.ScalarText(f, "reviewed_at") ?? "";
                if (f.TryGetProperty("commits", out var commitsProp) && commitsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in commitsProp.EnumerateObject())
                    {
                        var hash = prop.Value.ValueKind switch
                        {
                            JsonValueKind.String => prop.Value.GetString(),
                            JsonValueKind.Number when prop.Value.TryGetInt64(out _) => prop.Value.GetRawText(),
                            _ => null,
                        };
                        if (hash is not null)
                        {
                            commits[prop.Name] = hash;
                        }
                    }
                }
            }
        }

        var findingHeads = new List<(int Index, string Id)>();
        for (var i = firstFinding; i < end; i++)
        {
            if (!doc.Heading[i])
            {
                continue;
            }

            var m = FindingHeading.Match(doc.Lines[i]);
            if (m.Success)
            {
                findingHeads.Add((i, m.Groups[1].Value));
            }
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var n = 0; n < findingHeads.Count; n++)
        {
            var h = findingHeads[n];
            if (seen.TryGetValue(h.Id, out var first))
            {
                problems.Add(new Problem(file, h.Index + 1, $"finding {h.Id} is recorded twice, first on line {first}"));
            }
            else
            {
                seen[h.Id] = h.Index + 1;
            }

            var nextBound = n + 1 < findingHeads.Count ? findingHeads[n + 1].Index : end;
            var limit = doc.NextHeading(h.Index + 1, nextBound, AnyHeading);
            var finding = ReadFinding(doc, h.Index, h.Id, limit, file, problems, claimed, number, commits);
            if (finding is not null)
            {
                findings.Add(finding);
            }
        }

        return new ParsedReviewSection(number, reviewedAt, commits, findings);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private ParsedFinding? ReadFinding(
        Doc doc,
        int index,
        string id,
        int end,
        string file,
        List<Problem> problems,
        HashSet<int> claimed,
        int reviewNumber,
        Dictionary<string, string> reviewedCommits)
    {
        var line = index + 1;
        var who = $"finding {id}";
        var block = doc.FieldsBlock(index + 1, end);
        if (block is null)
        {
            problems.Add(new Problem(file, line, $"{who} has no fields block"));
            return null;
        }

        claimed.Add(block.Open);
        if (!Jsonc.TryParse(block.Text, out var value, out var error))
        {
            problems.Add(new Problem(file, block.Open + 1, $"{who} fields are not valid JSONC: {error}"));
            return null;
        }

        if (value is not { ValueKind: JsonValueKind.Object } f)
        {
            problems.Add(new Problem(file, block.Open + 1, $"{who} fields are not a set of fields"));
            return null;
        }

        var allErrors = _schema.Validate(FindingSchemaName, f);
        var ok = allErrors.Count == 0;
        var reportedErrors = allErrors.Where(e => !EmbeddedSchemaValidator.IsMissingRequired(e, "severity"));
        foreach (var m in EmbeddedSchemaValidator.FormatMessages(reportedErrors, $"{who} fields", plural: true))
        {
            problems.Add(new Problem(file, block.Open + 1, m));
        }

        if (!f.TryGetProperty("severity", out _))
        {
            problems.Add(new Problem(file, line, $"{who} has no severity"));
        }

        var idField = Jsonc.String(f, "id");
        if (idField is not null && idField != id)
        {
            problems.Add(new Problem(file, doc.KeyLine(block, "id"), $"heading says {id}, fields say {idField}"));
        }

        var concern = Jsonc.String(f, "concern");
        var rule = Jsonc.String(f, "rule");
        var unruled = concern == "convention" && !f.TryGetProperty("rule", out _);
        if (unruled)
        {
            problems.Add(new Problem(file, line, $"{who} is a convention finding with no rule; cite the written rule it breaks as <file>:<line>"));
        }

        var repo = Jsonc.String(f, "repo");
        var stray = repo is not null && reviewedCommits.Count > 0 && !reviewedCommits.ContainsKey(repo);
        if (stray)
        {
            problems.Add(new Problem(file, doc.KeyLine(block, "repo"), $"{who} names repo {repo}, which review {reviewNumber} does not list in its commits"));
        }

        if (!ok || unruled || stray)
        {
            return null;
        }

        var textStart = Math.Min(block.Last + 2, end);
        var prose = string.Join('\n', doc.Lines[textStart..end]).Trim();

        return new ParsedFinding(
            id,
            Jsonc.String(f, "severity")!,
            repo,
            concern,
            rule,
            Jsonc.String(f, "file")!,
            f.GetProperty("line").GetInt32(),
            prose);
    }
}
