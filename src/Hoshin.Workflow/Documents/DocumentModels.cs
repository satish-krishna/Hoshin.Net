using System.Text;
using System.Text.Json;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Documents;

public enum DocKind
{
    Spec,
    Plan,
    BuildLog,
    Review,
}

public static class DocKindExtensions
{
    public static string ToWireName(this DocKind kind) => kind switch
    {
        DocKind.Spec => "spec",
        DocKind.Plan => "plan",
        DocKind.BuildLog => "build-log",
        DocKind.Review => "review",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

public sealed record ParsedScenario(string Id, string Title, bool Outline, List<string> Steps, List<List<string>>? Examples, int Line);

public sealed record ParsedRequirement(string Id, string Title, List<string> From, List<ParsedScenario> Scenarios, int Line);

public sealed record ParsedSpec(List<ParsedRequirement> Requirements);

public sealed record TaskSection(string Title, string Body);

public sealed record ParsedTask(
    string Id,
    string Name,
    string Repo,
    List<string> DependsOn,
    List<string> Uses,
    List<string> Files,
    List<string> Covers,
    List<string> Supports,
    string[]? Test,
    string[]? Compile,
    List<TaskSection> Sections,
    string Text,
    int Line,
    int? Timeout = null);

public sealed record ParsedSharedDecision(string Question, string Owner, string Function);

public sealed record ParsedPlan(List<ParsedTask> Tasks, List<ParsedSharedDecision> SharedDecisions);

public sealed record ParsedBuildEntry(string Task, List<string> Commits, string? Departure, string? Red, int Line);

public sealed record ParsedBuildLog(string Repo, List<ParsedBuildEntry> Entries);

public sealed record ParsedFinding(
    string Id,
    string Severity,
    string? Repo,
    string? Concern,
    string? Rule,
    string File,
    int Line,
    string Text);

public sealed record ParsedReviewSection(
    int Number,
    string ReviewedAt,
    Dictionary<string, string> Commits,
    List<ParsedFinding> Findings);

public sealed record ParsedReview(List<ParsedReviewSection> Reviews);

/// <summary>Union result matching <c>schema/read-result.schema.json</c>.</summary>
public abstract record ReadResult(DocKind Kind, int Layout, JsonElement FrontMatter, List<Problem> Problems)
{
    public string ToJson(bool indented = false)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind.ToWireName());
            w.WriteNumber("layout", Layout);
            w.WritePropertyName("document");
            WriteDocument(w);
            w.WriteStartArray("problems");
            foreach (var p in Problems)
            {
                w.WriteStartObject();
                w.WriteString("file", p.File);
                w.WriteNumber("line", p.Line);
                w.WriteString("message", p.Message);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    protected abstract void WriteDocument(Utf8JsonWriter w);

    protected static void WriteStringArray(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    protected static void WriteNullableStringArray(Utf8JsonWriter w, string name, string[]? values)
    {
        if (values is null)
        {
            w.WriteNull(name);
            return;
        }

        WriteStringArray(w, name, values);
    }
}

public sealed record SpecReadResult(int Layout, JsonElement FrontMatter, ParsedSpec Document, List<Problem> Problems)
    : ReadResult(DocKind.Spec, Layout, FrontMatter, Problems)
{
    protected override void WriteDocument(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("requirements");
        foreach (var r in Document.Requirements)
        {
            w.WriteStartObject();
            w.WriteString("id", r.Id);
            w.WriteString("title", r.Title);
            WriteStringArray(w, "from", r.From);
            w.WriteStartArray("scenarios");
            foreach (var s in r.Scenarios)
            {
                w.WriteStartObject();
                w.WriteString("id", s.Id);
                w.WriteString("title", s.Title);
                w.WriteBoolean("outline", s.Outline);
                WriteStringArray(w, "steps", s.Steps);
                if (s.Examples is null)
                {
                    w.WriteNull("examples");
                }
                else
                {
                    w.WriteStartArray("examples");
                    foreach (var row in s.Examples)
                    {
                        w.WriteStartArray();
                        foreach (var cell in row)
                        {
                            w.WriteStringValue(cell);
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndArray();
                }

                w.WriteNumber("line", s.Line);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("line", r.Line);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

public sealed record PlanReadResult(int Layout, JsonElement FrontMatter, ParsedPlan Document, List<Problem> Problems)
    : ReadResult(DocKind.Plan, Layout, FrontMatter, Problems)
{
    protected override void WriteDocument(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("tasks");
        foreach (var t in Document.Tasks)
        {
            w.WriteStartObject();
            w.WriteString("id", t.Id);
            w.WriteString("name", t.Name);
            w.WriteString("repo", t.Repo);
            WriteStringArray(w, "depends_on", t.DependsOn);
            WriteStringArray(w, "uses", t.Uses);
            WriteStringArray(w, "files", t.Files);
            WriteStringArray(w, "covers", t.Covers);
            WriteStringArray(w, "supports", t.Supports);
            WriteNullableStringArray(w, "test", t.Test);
            WriteNullableStringArray(w, "compile", t.Compile);
            if (t.Timeout is int timeoutSeconds)
            {
                w.WriteNumber("timeout", timeoutSeconds);
            }
            w.WriteStartArray("sections");
            foreach (var s in t.Sections)
            {
                w.WriteStartObject();
                w.WriteString("title", s.Title);
                w.WriteString("body", s.Body);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteString("text", t.Text);
            w.WriteNumber("line", t.Line);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("shared_decisions");
        foreach (var d in Document.SharedDecisions)
        {
            w.WriteStartObject();
            w.WriteString("question", d.Question);
            w.WriteString("owner", d.Owner);
            w.WriteString("function", d.Function);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

public sealed record BuildLogReadResult(int Layout, JsonElement FrontMatter, ParsedBuildLog Document, List<Problem> Problems)
    : ReadResult(DocKind.BuildLog, Layout, FrontMatter, Problems)
{
    protected override void WriteDocument(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("repo", Document.Repo);
        w.WriteStartArray("entries");
        foreach (var e in Document.Entries)
        {
            w.WriteStartObject();
            w.WriteString("task", e.Task);
            WriteStringArray(w, "commits", e.Commits);
            if (e.Departure is null)
            {
                w.WriteNull("departure");
            }
            else
            {
                w.WriteString("departure", e.Departure);
            }

            if (e.Red is null)
            {
                w.WriteNull("red");
            }
            else
            {
                w.WriteString("red", e.Red);
            }

            w.WriteNumber("line", e.Line);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}

public sealed record ReviewReadResult(int Layout, JsonElement FrontMatter, ParsedReview Document, List<Problem> Problems)
    : ReadResult(DocKind.Review, Layout, FrontMatter, Problems)
{
    protected override void WriteDocument(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteStartArray("reviews");
        foreach (var r in Document.Reviews)
        {
            w.WriteStartObject();
            w.WriteNumber("number", r.Number);
            w.WriteString("reviewed_at", r.ReviewedAt);
            w.WriteStartObject("commits");
            foreach (var (repo, hash) in r.Commits)
            {
                w.WriteString(repo, hash);
            }

            w.WriteEndObject();
            w.WriteStartArray("findings");
            foreach (var f in r.Findings)
            {
                w.WriteStartObject();
                w.WriteString("id", f.Id);
                w.WriteString("severity", f.Severity);
                if (f.Repo is not null)
                {
                    w.WriteString("repo", f.Repo);
                }

                if (f.Concern is not null)
                {
                    w.WriteString("concern", f.Concern);
                }

                if (f.Rule is not null)
                {
                    w.WriteString("rule", f.Rule);
                }

                w.WriteString("file", f.File);
                w.WriteNumber("line", f.Line);
                w.WriteString("text", f.Text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }
}
