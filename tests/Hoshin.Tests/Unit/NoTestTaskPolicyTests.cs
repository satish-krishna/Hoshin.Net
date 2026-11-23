using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class NoTestTaskPolicyTests
{
    private static string InitNoTestPlanRepo(TempDir tmp)
    {
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["config/app.json"] = "{ \"timeout\": 30 }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t41-notest", "type": "chore", "scope": "config", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "config", "status": "accepted" }
                ```

                # Plan: Update config

                ## Tasks

                ### T1: Bump app timeout config

                ```jsonc
                { "id": "T1", "repo": "repo", "files": ["config/app.json"], "test": null }
                ```

                #### Change
                Update `config/app.json` timeout to 60.

                #### Tests
                No testable behavior; verified by build.finish checks.
                """,
        });
        return repoDir;
    }

    [Fact]
    public void ReadPlan_TaskWithNullTest_LoadsAndBriefMentionsNoTestAndChecks()
    {
        using var tmp = new TempDir("hoshin-t41-load-");
        var repoDir = InitNoTestPlanRepo(tmp);

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;
        var task = Assert.Single(state.Plan.Tasks);
        Assert.Null(task.Test);

        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        Assert.Contains("no testable behavior", step.Agent.Brief, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build.finish", step.Agent.Brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateAndCommitTask_NoTestTaskWithConfigChange_PassesWithSingleCommitAndSkippedRed()
    {
        using var tmp = new TempDir("hoshin-t41-pass-");
        var repoDir = InitNoTestPlanRepo(tmp);

        var repo = TestServices.FindRepo(repoDir)!;
        var startHead = repo.Head();
        var state = TestServices.ReadState(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        TestServices.WorkflowRunner.Issue(repo, state, step, "s1");

        // Guard allows editing config/app.json for a no-test task and denies editing test files.
        var currentStep = repo.ReadStep()!;
        Assert.Null(GuardStepEvaluator.CheckBuildTargetPath(Path.Combine(repoDir, "config", "app.json"), repo, currentStep));
        Assert.NotNull(GuardStepEvaluator.CheckBuildTargetPath(Path.Combine(repoDir, "test", "t1.test.mjs"), repo, currentStep));

        TestGitFixture.Write(Path.Combine(repoDir, "config", "app.json"), "{ \"timeout\": 60 }\n");

        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
        Assert.Equal("skipped: no testable behavior", verdict.Red);
        Assert.Null(verdict.TestCommit);

        var subjects = TestGitFixture.Git(repoDir, "log", "--format=%s", $"{startHead}..HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var subject = Assert.Single(subjects);
        Assert.Equal("chore(config): Bump app timeout config", subject);
    }

    [Fact]
    public void EvaluateAndCommitTask_NoTestTaskWithNoChanges_FailsWithWorkReason()
    {
        using var tmp = new TempDir("hoshin-t41-empty-");
        var repoDir = InitNoTestPlanRepo(tmp);

        var repo = TestServices.FindRepo(repoDir)!;
        var startHead = repo.Head();
        var state = TestServices.ReadState(repo)!;
        TestServices.WorkflowRunner.Issue(repo, state, WorkflowEngine.Next(state), "s1");

        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.StartsWith("work:", StringComparison.Ordinal));
        Assert.Equal(startHead, repo.Head());
    }

    [Fact]
    public void EvaluateAndCommitTask_NoTestTaskWithTestFileChange_FailsWithOrderReason()
    {
        using var tmp = new TempDir("hoshin-t41-testchange-");
        var repoDir = InitNoTestPlanRepo(tmp);

        var repo = TestServices.FindRepo(repoDir)!;
        var startHead = repo.Head();
        var state = TestServices.ReadState(repo)!;
        TestServices.WorkflowRunner.Issue(repo, state, WorkflowEngine.Next(state), "s1");

        TestGitFixture.Write(Path.Combine(repoDir, "config", "app.json"), "{ \"timeout\": 60 }\n");
        TestGitFixture.Write(Path.Combine(repoDir, "test", "accidental.test.mjs"), "export {};\n");

        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("order: a task with no test must not change test files", StringComparison.Ordinal));
        Assert.Equal(startHead, repo.Head());
    }
}
