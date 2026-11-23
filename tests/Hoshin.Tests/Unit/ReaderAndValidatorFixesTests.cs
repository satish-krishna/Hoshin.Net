using System.Text.Json;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// T21: small reader and validator fixes (overflow-safe review numbers, \z regex anchors & timeouts,
/// unsupported JSON Schema keyword rejection, CrossChecker in-memory superseded check, and ChangeRecordStore invalid JSONC error).
/// </summary>
public sealed class ReaderAndValidatorFixesTests
{
    [Fact]
    public void ReviewReader_OverflowReviewHeadingNumber_ReportsProblemInsteadOfThrowing()
    {
        var text = """
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 99999999999999999999999999999

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-22", "commits": { "app": "abc1234" } }
            ```
            """;

        var result = TestServices.ReviewReader.Read(text, "build/review.md");

        Assert.NotEmpty(result.Problems);
        Assert.Contains(result.Problems, p => p.Message.Contains("99999999999999999999999999999", StringComparison.Ordinal));
    }

    [Fact]
    public void Validators_UseEndOfStringAnchor_RejectingTrailingNewlines()
    {
        // MarkerReader.CommitScopeRegex
        Assert.DoesNotMatch(MarkerReader.CommitScopeRegex, "demo\n");

        // MarkerReader.NameRegex via MarkerProblems (single-repo app name)
        using var markerDoc = JsonDocument.Parse("""
            {
              "version": 1,
              "layout": "single-repo",
              "systems": {
                "demo": { "apps": { "app\n": "." } }
              }
            }
            """);
        var markerProblems = MarkerReader.MarkerProblems(markerDoc.RootElement);
        Assert.NotEmpty(markerProblems);

        // ChangeRecordStore SHA pattern
        using var changeDoc = JsonDocument.Parse("""
            {
              "version": 1,
              "branch": "change/c1",
              "type": "feat",
              "scope": "demo",
              "base": "main",
              "pullRequests": [
                { "repo": "app", "url": "https://github.com/o/r/pull/1", "number": 1, "pushed": "abcdef1\n" }
              ]
            }
            """);
        var recordProblems = ChangeRecordStore.RecordProblems(changeDoc.RootElement);
        Assert.Contains(recordProblems, p => p.Contains("pullRequests[0]", StringComparison.Ordinal));

        // EmbeddedSchemaValidator pattern keyword
        using var taskDoc = JsonDocument.Parse("""
            {
              "id": "T1\n",
              "repo": "app"
            }
            """);
        var schemaErrors = TestServices.SchemaValidator.Validate("plan-task.schema.json", taskDoc.RootElement);
        Assert.Contains(schemaErrors, e => e.Keyword == "pattern" && e.Path == "id");
    }

    [Theory]
    [InlineData("$ref", """{ "type": "object", "$ref": "#/definitions/x" }""")]
    [InlineData("oneOf", """{ "oneOf": [{ "type": "string" }] }""")]
    [InlineData("anyOf", """{ "type": "object", "properties": { "a": { "anyOf": [{ "type": "string" }] } } }""")]
    [InlineData("if", """{ "type": "object", "if": { "required": ["a"] } }""")]
    [InlineData("const", """{ "type": "object", "properties": { "v": { "const": 1 } } }""")]
    public void EmbeddedSchemaValidator_RejectsUnsupportedKeywordsAtLoad(string keyword, string schemaJson)
    {
        using var schemaDoc = JsonDocument.Parse(schemaJson);
        var ex = Assert.Throws<NotSupportedException>(() =>
            EmbeddedSchemaValidator.ValidateSchemaDefinition(schemaDoc.RootElement, "test.schema.json"));
        Assert.Contains(keyword, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CrossChecker_IsSuperseded_UsesParsedFrontMatterWithoutReadingDisk()
    {
        var specText = """
            ```jsonc
            { "layout": 2, "system": "demo" }
            ```

            # Spec

            ```gherkin
            Feature: Demo
              @R1 @from:D1
              Rule: One
                @R1.1
                Scenario: First
                  Given a thing
                @R1.2
                Scenario: Second
                  Given another thing
            ```
            """;

        var supersededPlanText = """
            ```jsonc
            { "layout": 2, "system": "demo", "status": "superseded" }
            ```

            # Plan

            ## Tasks

            ### T1: Only R1.1

            ```jsonc
            { "id": "T1", "repo": "app", "covers": ["R1.1"] }
            ```
            """;

        var specRead = TestServices.SpecReader.Read(specText, "nonexistent/spec.md");
        var planRead = TestServices.PlanReader.Read(supersededPlanText, "nonexistent/plan.md");

        // File does not exist on disk; CrossChecker must still detect status: "superseded" from PlanReadResult.FrontMatter
        // and skip checking spec coverage (so uncovered R1.2 is NOT reported).
        var problems = TestServices.CrossChecker.CheckChange([
            new ChangeDoc("nonexistent/spec.md", specRead),
            new ChangeDoc("nonexistent/plan.md", planRead),
        ]);

        Assert.Empty(problems);
    }

    [Fact]
    public void ChangeRecordStore_InvalidJsonc_ThrowsCouldNotRunInsteadOfFallingBack()
    {
        using var tmp = new TempDir("hoshin-t21-record-");
        var folder = Path.Combine(tmp.Path, "2026-11-22-broken");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ChangeRecordStore.RecordFileName), "{ \"version\": 1, broken ");

        var ex = Assert.Throws<CouldNotRunException>(() => TestServices.ChangeRecords.BranchOf(folder));
        Assert.Contains("change.jsonc", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
