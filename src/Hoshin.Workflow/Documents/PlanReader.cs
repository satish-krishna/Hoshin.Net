using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

/// <summary>
/// Reads a Layout 2 <c>plan.md</c>: tasks with JSONC field blocks under <c>### T&lt;n&gt;: name</c>,
/// <c>####</c> prose sections, and <c>## Shared decisions</c> as a JSONC array.
/// </summary>
public sealed class PlanReader(IJsonSchemaValidator? schemaValidator = null)
{
    private const string TaskSchemaName = "plan-task.schema.json";
    private const string DecisionSchemaName = "shared-decision.schema.json";

    private static readonly Regex Heading2 = new(@"^### (T[0-9]+):\s*(.*?)\s*$", RegexOptions.Compiled);
    private static readonly Regex AnyHeading = new(@"^#{1,3} ", RegexOptions.Compiled);
    private static readonly Regex TopHeading = new(@"^#{1,2} ", RegexOptions.Compiled);
    private static readonly Regex SectionHeading = new(@"^#### ", RegexOptions.Compiled);
    private static readonly Regex SharedHeading = new(@"^## Shared decisions\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RawKey = new(@"^\s*[\[{]*\s*""id""\s*:\s*""T[0-9]+", RegexOptions.Compiled);

    private readonly IJsonSchemaValidator _schema = schemaValidator ?? EmbeddedSchemaValidator.Default;

    public PlanReadResult Read(string text, string file, IReadOnlyList<string>? allowedRepos = null, string? systemName = null)
    {
        var doc = Doc.Split(text);
        var fm = FrontMatter.Parse(doc, file);
        FrontMatter.RefuseNewerLayout(fm.Layout, file);

        var problems = new List<Problem>();
        var heads = TaskHeads(doc);

        var missingVersion = false;
        if (fm.Layout < 2 && !fm.HasLayoutField)
        {
            var hasFieldsBlock = heads.Select((h, n) =>
            {
                var limit = n + 1 < heads.Count ? heads[n + 1].Index : doc.Lines.Length;
                var end = doc.NextHeading(h.Index + 1, limit, AnyHeading);
                return doc.FieldsBlock(h.Index + 1, end) is not null;
            }).Any(x => x);

            if (hasFieldsBlock)
            {
                problems.Add(new Problem(file, 1, "no layout version"));
                missingVersion = true;
            }
        }

        if (fm.Layout < 2 && !missingVersion)
        {
            FrontMatter.RefuseUnsupportedLayout(fm.Layout, file);
        }

        var system = systemName ?? Jsonc.String(fm.Data, "system") ?? "";
        var claimed = new HashSet<int>();
        var tasks = ReadTasks(doc, heads, file, problems, allowedRepos, system, claimed);
        var decisions = ReadDecisions(doc, file, problems, claimed);
        doc.ReportUnread(
            file,
            problems,
            [new RecordHeading(Doc.RecordPrefix("T[0-9]+"), Heading2, "a task heading")],
            ["id"],
            RawKey,
            claimed);

        return new PlanReadResult(fm.Layout, fm.Data, new ParsedPlan(tasks, decisions), problems);
    }

    private static List<(int Index, string Id, string Name)> TaskHeads(Doc doc)
    {
        var heads = new List<(int Index, string Id, string Name)>();
        for (var i = 0; i < doc.Lines.Length; i++)
        {
            if (!doc.Heading[i])
            {
                continue;
            }

            var m = Heading2.Match(doc.Lines[i]);
            if (m.Success)
            {
                heads.Add((i, m.Groups[1].Value, m.Groups[2].Value));
            }
        }

        return heads;
    }

    private List<ParsedTask> ReadTasks(
        Doc doc,
        List<(int Index, string Id, string Name)> heads,
        string file,
        List<Problem> problems,
        IReadOnlyList<string>? allowedRepos,
        string system,
        HashSet<int> claimed)
    {
        var tasks = new List<ParsedTask>();
        for (var n = 0; n < heads.Count; n++)
        {
            var h = heads[n];
            var limit = n + 1 < heads.Count ? heads[n + 1].Index : doc.Lines.Length;
            var end = doc.NextHeading(h.Index + 1, limit, AnyHeading);
            var line = h.Index + 1;
            var task = new ParsedTask(
                h.Id,
                h.Name,
                "",
                [],
                [],
                [],
                [],
                [],
                null,
                null,
                ReadSections(doc, h.Index + 1, end),
                string.Join('\n', doc.Lines[h.Index..end]).TrimEnd(),
                line);

            var block = doc.FieldsBlock(h.Index + 1, doc.NextHeading(h.Index + 1, end, SectionHeading));
            if (block is null)
            {
                problems.Add(new Problem(file, line, $"task {h.Id} has no fields block"));
            }
            else
            {
                claimed.Add(block.Open);
                if (!Jsonc.TryParse(block.Text, out var value, out var error))
                {
                    problems.Add(new Problem(file, block.Open + 1, $"task {h.Id} fields are not valid JSONC: {error}"));
                }
                else if (value is not { ValueKind: JsonValueKind.Object } f)
                {
                    problems.Add(new Problem(file, block.Open + 1, $"task {h.Id} fields are not a set of fields"));
                }
                else
                {
                    var errors = _schema.Validate(TaskSchemaName, f);
                    foreach (var m in EmbeddedSchemaValidator.FormatMessages(errors, $"task {h.Id} fields", plural: true))
                    {
                        problems.Add(new Problem(file, block.Open + 1, m));
                    }

                    var id = Jsonc.String(f, "id");
                    if (id is not null && id != h.Id)
                    {
                        problems.Add(new Problem(file, doc.KeyLine(block, "id"), $"heading says {h.Id}, fields say {id}"));
                    }

                    var repo = Jsonc.String(f, "repo");
                    if (repo is not null)
                    {
                        if (allowedRepos is not null && !allowedRepos.Contains(repo))
                        {
                            problems.Add(new Problem(file, doc.KeyLine(block, "repo"), $"task {h.Id} names repository {repo}, not in system {system}"));
                        }
                    }

                    task = task with
                    {
                        Repo = repo ?? "",
                        DependsOn = Jsonc.Strings(f, "depends_on"),
                        Uses = Jsonc.Strings(f, "uses"),
                        Files = Jsonc.Strings(f, "files"),
                        Covers = Jsonc.Strings(f, "covers"),
                        Supports = Jsonc.Strings(f, "supports"),
                        Test = Jsonc.Argv(f, "test"),
                        Compile = Jsonc.Argv(f, "compile"),
                    };
                }
            }

            if (!task.Sections.Any(s => s.Title.Equals("tests", StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(new Problem(file, line, $"task {h.Id} has no Tests section"));
            }

            tasks.Add(task);
        }

        return tasks;
    }

    private static List<TaskSection> ReadSections(Doc doc, int from, int end)
    {
        var sections = new List<TaskSection>();
        var i = doc.NextHeading(from, end, SectionHeading);
        while (i < end)
        {
            var next = doc.NextHeading(i + 1, end, SectionHeading);
            sections.Add(new TaskSection(
                SectionHeading.Replace(doc.Lines[i], "").Trim(),
                string.Join('\n', doc.Lines[(i + 1)..next]).Trim()));
            i = next;
        }

        return sections;
    }

    private List<ParsedSharedDecision> ReadDecisions(Doc doc, string file, List<Problem> problems, HashSet<int> claimed)
    {
        var heading = doc.NextHeading(0, doc.Lines.Length, SharedHeading);
        if (heading >= doc.Lines.Length)
        {
            return [];
        }

        var end = doc.NextHeading(heading + 1, doc.Lines.Length, TopHeading);
        var block = doc.FieldsBlock(heading + 1, end);
        if (block is null)
        {
            problems.Add(new Problem(file, heading + 1, "shared decisions are not a jsonc list"));
            return [];
        }

        claimed.Add(block.Open);
        if (!Jsonc.TryParse(block.Text, out var value, out var error))
        {
            problems.Add(new Problem(file, block.Open + 1, $"shared decisions are not valid JSONC: {error}"));
            return [];
        }

        if (value is null)
        {
            return [];
        }

        if (value.Value.ValueKind != JsonValueKind.Array)
        {
            problems.Add(new Problem(file, block.Open + 1, "shared decisions are not a list"));
            return [];
        }

        var decisions = new List<ParsedSharedDecision>();
        var i = 0;
        foreach (var entry in value.Value.EnumerateArray())
        {
            var who = $"shared decision {++i}";
            if (entry.ValueKind != JsonValueKind.Object)
            {
                problems.Add(new Problem(file, block.Open + 1, $"{who} is not a set of fields"));
                continue;
            }

            var errors = _schema.Validate(DecisionSchemaName, entry);
            if (errors.Count > 0)
            {
                problems.AddRange(EmbeddedSchemaValidator.FormatMessages(errors, who, plural: false)
                    .Select(m => new Problem(file, block.Open + 1, m)));
                continue;
            }

            decisions.Add(new ParsedSharedDecision(
                Jsonc.String(entry, "question")!,
                Jsonc.String(entry, "owner")!,
                Jsonc.String(entry, "function")!));
        }

        return decisions;
    }
}
