using System.Text.Json;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class DocumentReaderTests
{
    private readonly SpecReader _spec = TestServices.SpecReader;
    private readonly PlanReader _plan = TestServices.PlanReader;
    private readonly BuildLogReader _buildLog = TestServices.BuildLogReader;
    private readonly ReviewReader _review = TestServices.ReviewReader;
    private readonly IntentReader _intent = TestServices.IntentReader;

    [Fact]
    public void FrontMatter_RefusesYamlWithGuidance()
    {
        var ex = Assert.Throws<CouldNotRunException>(() => _plan.Read("---\nlayout: 2\n---\n", "plan.md"));
        Assert.Contains("front matter is YAML", ex.Message);
        Assert.Contains("```jsonc", ex.Message);
    }

    [Fact]
    public void FrontMatter_RefusesLayout1AndNewerLayouts()
    {
        var ex1 = Assert.Throws<CouldNotRunException>(() => _plan.Read("```jsonc\n{ \"layout\": 1 }\n```\n", "plan.md"));
        Assert.Contains("layout 1", ex1.Message);

        var ex3 = Assert.Throws<CouldNotRunException>(() => _plan.Read("```jsonc\n{ \"layout\": 3 }\n```\n", "plan.md"));
        Assert.Contains("layout 3 is newer", ex3.Message);
    }

    [Fact]
    public void FrontMatterCheck_NamesBareJsonYamlAndMissing()
    {
        var bare = FrontMatterCheck.Check(Doc.Split("{\n  \"layout\": 2\n}\n# Spec"), "spec.md");
        Assert.NotNull(bare);
        Assert.Contains("bare JSON", bare!.Message);

        var yaml = FrontMatterCheck.Check(Doc.Split("---\nlayout: 2\n---\n"), "spec.md");
        Assert.NotNull(yaml);
        Assert.Contains("YAML", yaml!.Message);

        var missing = FrontMatterCheck.Check(Doc.Split("# Intent: Test\n"), "intent.md");
        Assert.NotNull(missing);
        Assert.Contains("missing", missing!.Message);
    }

    [Fact]
    public void IntentReader_ValidDocumentPasses_AndIgnoresInlineCodeAngleBrackets()
    {
        var text = """
            ```jsonc
            {
              "title": "Greeting",
              "system": "demo",
              "author": "Alice",
              "date": "2026-11-21",
              "status": "draft",
              "scope": "greet"
            }
            ```

            # Intent: Greeting

            ## Problem

            Greet users with `Hello, <name>!`.

            ## Proposed outcome

            Users see a personalized greeting.

            ## Affected users and systems

            CLI users of `demo`.

            ## Constraints

            Keep startup fast.

            ## Decisions

            D1: Default name is world.

            ## Open questions

            None.
            """;

        var problems = _intent.Check(text, "intent.md", "greet");
        Assert.Empty(problems);
    }

    [Fact]
    public void IntentReader_FlagsRealTemplatePlaceholderAndScopeMismatch()
    {
        var text = """
            ```jsonc
            {
              "title": "Greeting",
              "system": "demo",
              "author": "Alice",
              "date": "2026-11-21",
              "status": "draft",
              "scope": "wrong"
            }
            ```

            # Intent: Greeting

            ## Problem

            Describe <problem here>.

            ## Proposed outcome

            Outcome.

            ## Affected users and systems

            Users.

            ## Constraints

            None.

            ## Decisions

            None.

            ## Open questions

            None.
            """;

        var problems = _intent.Check(text, "intent.md", "greet");
        Assert.Contains(problems, p => p.Message.Contains("scope is wrong"));
        Assert.Contains(problems, p => p.Message.Contains("template placeholder"));
    }

    [Fact]
    public void SpecReader_ValidatesGherkinRulesTagsOutlinesAndUnreadTags()
    {
        var text = """
            ```jsonc
            { "layout": 2, "system": "demo", "status": "draft" }
            ```

            # Spec: Demo

            ```gherkin
            Feature: Demo
              @R1 @from:D1 @from:outcome-1
              Rule: Greeting works

                @R1.1
                Scenario: Default greeting
                  Given no name
                  When greeted
                  Then output is "Hello, world!"

                @R1.2
                Scenario Outline: Named greeting
                  Given a name <name>
                  When greeted
                  Then output is <expected>

                  Examples:
                    | name | expected |
                    | Ada  | Hello, Ada! |
            ```
            """;

        var res = _spec.Read(text, "spec.md");
        Assert.Empty(res.Problems);
        Assert.Single(res.Document.Requirements);
        Assert.Equal("R1", res.Document.Requirements[0].Id);
        Assert.Equal(["D1", "outcome-1"], res.Document.Requirements[0].From);
        Assert.Equal(2, res.Document.Requirements[0].Scenarios.Count);
        Assert.True(res.Document.Requirements[0].Scenarios[1].Outline);
    }

    [Fact]
    public void SpecReader_ReportsGherkinSyntaxErrorWithLine()
    {
        var text = """
            ```jsonc
            { "layout": 2 }
            ```

            ```gherkin
            Feature: Broken
              @R1 @from:D1
              Rule: Bad

                @R1.1
                Scenario: Bad table
                  Given rows
                    | a | b |
                    | 1 |
            ```
            """;

        var res = _spec.Read(text, "spec.md");
        Assert.NotEmpty(res.Problems);
        Assert.StartsWith("gherkin:", res.Problems[0].Message);
        Assert.Equal(14, res.Problems[0].Line);
    }

    [Fact]
    public void ChangeRecordStore_ValidatesAndRoundTripsJsonc()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hoshin-rec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var record = new ChangeRecord(1, "feat/PROJ-123-demo", "feat", "PROJ-123", "main", [new PullRequestRecord("app", "https://github.com/org/app/pull/7", 7, "3f2a9c1e4b7d")]);
            var path = TestServices.ChangeRecords.Write(dir, record);
            Assert.EndsWith("change.jsonc", path);
            var text = File.ReadAllText(path);
            Assert.StartsWith("// Written by the hoshin program", text);

            var loaded = TestServices.ChangeRecords.Read(dir);
            Assert.NotNull(loaded);
            Assert.Equal(record.Branch, loaded!.Branch);
            Assert.Single(loaded.PullRequests);
            Assert.Equal(7, loaded.PullRequests[0].Number);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void BuildLogAndReviewReaders_HandleAllDigitCommitsAndOptionalFindingFields()
    {
        var logText = """
            ```jsonc
            { "layout": 2 }
            ```

            # Build log: app

            ### T1

            ```jsonc
            {
              "task": "T1",
              "commits": ["0123456", "2408179"],
              "red": "observed"
            }
            ```
            """;

        var log = _buildLog.Read(logText, "build/app.md");
        Assert.Empty(log.Problems);
        Assert.Equal(["0123456", "2408179"], log.Document.Entries[0].Commits);
        Assert.Equal("observed", log.Document.Entries[0].Red);

        var revText = """
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            {
              "review": 1,
              "reviewed_at": "2026-11-21",
              "commits": { "app": "0123456" }
            }
            ```

            ### F1

            ```jsonc
            {
              "id": "F1",
              "severity": "low",
              "repo": "app",
              "concern": "convention",
              "file": "src/a.cs",
              "line": 10
            }
            ```

            Missing rule must fail.
            """;

        var rev = _review.Read(revText, "build/review.md");
        Assert.Contains(rev.Problems, p => p.Message.Contains("convention finding with no rule"));
        Assert.Empty(rev.Document.Reviews[0].Findings);
    }

    [Fact]
    public void SolidArchitecture_CustomSchemaValidatorCanBeInjectedIntoReaders()
    {
        var customValidator = new StubSchemaValidator();
        var planReader = new PlanReader(customValidator);
        var text = """
            ```jsonc
            { "layout": 2 }
            ```

            ### T1: Custom

            ```jsonc
            { "id": "T1", "repo": "app" }
            ```

            #### Tests

            Test it.
            """;

        var result = planReader.Read(text, "plan.md");
        Assert.Contains(result.Problems, p => p.Message.Contains("custom validation injected"));
    }

    private sealed class StubSchemaValidator : IJsonSchemaValidator
    {
        public IReadOnlyList<SchemaError> Validate(string schemaName, JsonElement value) =>
            [new SchemaError("custom", "repo", "custom validation injected", null)];
    }
}
