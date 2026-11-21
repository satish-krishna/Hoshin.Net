using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>Checks an <c>intent.md</c> document: line-1 JSONC front matter fields, required sections, and no leftover template placeholders.</summary>
public sealed class IntentReader
{
    public static readonly string[] RequiredSections =
    [
        "## Problem",
        "## Proposed outcome",
        "## Affected users and systems",
        "## Constraints",
        "## Decisions",
        "## Open questions",
    ];

    private static readonly string[] ValidStatuses = ["draft", "accepted", "rejected"];
    private static readonly Regex TopHeading = new("^#{1,2} ", RegexOptions.Compiled);
    private static readonly Regex InlineCode = new("`[^`]*`", RegexOptions.Compiled);
    private static readonly Regex TemplatePlaceholder = new("<[a-z][^>]*>", RegexOptions.Compiled);

    public List<Problem> Check(string text, string file, string? expectedScope = null)
    {
        var problems = new List<Problem>();
        var doc = Doc.Split(text);
        if (FrontMatterCheck.Check(doc, file) is { } shape)
        {
            problems.Add(shape);
            return problems;
        }

        JsonElement front;
        try
        {
            front = FrontMatter.Parse(doc, file).Data;
        }
        catch (CouldNotRunException e)
        {
            problems.Add(new Problem(file, 1, e.Message));
            return problems;
        }

        foreach (var field in new[] { "title", "system", "author", "date", "status", "scope" })
        {
            if (string.IsNullOrWhiteSpace(Jsonc.String(front, field)))
            {
                problems.Add(new Problem(file, 2, $"front matter lacks {field}"));
            }
        }

        var status = Jsonc.String(front, "status");
        if (status is not null && !ValidStatuses.Contains(status))
        {
            problems.Add(new Problem(file, 2, $"status is {status}, not draft, accepted or rejected"));
        }

        var writtenScope = Jsonc.String(front, "scope");
        if (expectedScope is not null && writtenScope is not null && writtenScope != expectedScope)
        {
            problems.Add(new Problem(file, 2, $"scope is {writtenScope}, but the change's scope is {expectedScope}"));
        }

        var title = Array.FindIndex(doc.Lines, l => l.StartsWith("# Intent: ", StringComparison.Ordinal));
        if (title < 0)
        {
            problems.Add(new Problem(file, 1, "there is no `# Intent: <title>` heading"));
        }

        var last = -1;
        foreach (var section in RequiredSections)
        {
            var at = Enumerable.Range(0, doc.Lines.Length)
                .FirstOrDefault(i => doc.Heading[i] && doc.Lines[i].TrimEnd() == section, -1);
            if (at < 0)
            {
                problems.Add(new Problem(file, Math.Max(last, 0) + 1, $"section `{section}` is missing"));
                continue;
            }

            if (at < last)
            {
                problems.Add(new Problem(file, at + 1, $"section `{section}` is out of order"));
            }

            var end = doc.NextHeading(at + 1, doc.Lines.Length, TopHeading);
            if (doc.Lines[(at + 1)..end].All(string.IsNullOrWhiteSpace))
            {
                problems.Add(new Problem(file, at + 1, $"section `{section}` is empty"));
            }

            last = at;
        }

        var placeholder = Enumerable.Range(0, doc.Lines.Length)
            .FirstOrDefault(i => !doc.Fenced[i] && TemplatePlaceholder.IsMatch(InlineCode.Replace(doc.Lines[i], "")), -1);
        if (placeholder >= 0)
        {
            problems.Add(new Problem(file, placeholder + 1, $"a template placeholder is still there: {doc.Lines[placeholder].Trim()}"));
        }

        return problems;
    }
}
