using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class MarkerTestPatternsTests
{
    [Fact]
    public void Check_MarkerWithValidTestPatterns_PassesAndClassifiesSpecAsTest()
    {
        using var tmp = new TempDir("hoshin-t29-marker-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": { "demo": { "repos": ["repo"] } },
                  "testPatterns": ["spec/**"]
                }
                """,
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t29-demo", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Demo

                ## Tasks

                ### T1: Ruby spec

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["rspec", "spec/a.rb"] }
                ```

                #### Change
                Update `lib/a.rb`.

                #### Tests
                Add `spec/a.rb`.
                """,
        });

        var markerPath = Path.Combine(repoDir, ".hoshin.json");
        var checkProblems = DocumentCheckService.Default.CheckFiles([markerPath]);
        Assert.Empty(checkProblems);

        var repo = WorkflowRepo.Find(repoDir)!;
        var plan = repo.ReadPlan()!;
        Assert.True(WorkflowRepo.IsTestPath("spec/a.rb", plan.EffectiveTestPatterns));
        Assert.False(WorkflowRepo.IsTestPath("lib/a.rb", plan.EffectiveTestPatterns));
    }

    [Theory]
    [InlineData("\"spec/**\"")]
    [InlineData("[123]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"   \"]")]
    public void MarkerProblems_InvalidTestPatterns_ReportsProblem(string rawTestPatterns)
    {
        var json = $$"""
            {
              "version": 1,
              "systems": { "demo": { "repos": ["repo"] } },
              "testPatterns": {{rawTestPatterns}}
            }
            """;
        Assert.True(Jsonc.TryParse(json, out var raw, out _));
        var problems = MarkerReader.MarkerProblems(raw!.Value);
        Assert.Contains(problems, p => p.Contains("testPatterns must be", StringComparison.Ordinal));
    }
}
