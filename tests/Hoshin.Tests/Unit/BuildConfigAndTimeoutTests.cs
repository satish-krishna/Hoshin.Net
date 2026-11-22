using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class BuildConfigAndTimeoutTests
{
    [Fact]
    public void EvaluateAndCommitTask_PerTaskTimeoutOneSecond_FailsGreenWithTimeoutWithinTwoSeconds()
    {
        using var tmp = new TempDir("hoshin-t28-timeout-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export const x = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t28-timeout", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Timeout

                ## Tasks

                ### T1: Slow task

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "setTimeout(()=>{},3000)"], "timeout": 1 }
                ```

                #### Change
                Update `src/math.mjs`.

                #### Tests
                Run slow test.
                """,
        });

        var repo = WorkflowRepo.Find(repoDir)!;
        var runner = WorkflowRunner.Default;
        var state = WorkflowState.Read(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        runner.Issue(repo, state, step, "s1");

        TestGitFixture.Write(Path.Combine(repoDir, "test", "t1.test.mjs"), "export {};\n");
        TestGitFixture.Write(Path.Combine(repoDir, "src", "math.mjs"), "export const x = 2;\n");

        var evalState = WorkflowState.Read(repo)!;
        var timingRunner = new RecordingRunner(AllowListedCommandRunner.Default);
        var timedRunner = new WorkflowRunner(timingRunner);

        var verdict = timedRunner.EvaluateAndCommitTask(repo, evalState, "T1", 1);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("timed out after 1s", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(timingRunner.LastTestResult);
        Assert.True(timingRunner.LastTestResult.TimedOut);
        Assert.Equal(TimeSpan.FromSeconds(1), timingRunner.LastTestTimeout);
        Assert.True(
            timingRunner.LastTestResult.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected Green command timeout within 2s, took {timingRunner.LastTestResult.Elapsed.TotalSeconds:F2}s");
    }

    private sealed class RecordingRunner(IAllowListedCommandRunner inner) : IAllowListedCommandRunner
    {
        public TimeSpan? LastTestTimeout { get; private set; }

        public AllowListedCommandResult? LastTestResult { get; private set; }

        public AllowListedCommandResult Run(
            string workingDirectory,
            IReadOnlyList<string> argv,
            TimeSpan? timeout = null,
            IReadOnlyDictionary<string, string>? extraEnv = null)
        {
            var result = inner.Run(workingDirectory, argv, timeout, extraEnv);
            if (argv.Count > 0 && argv[0].Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                LastTestTimeout = timeout;
                LastTestResult = result;
            }

            return result;
        }
    }

    [Fact]
    public void Next_WithConfiguredMaxAttemptsOne_ReturnsDoneFalseAfterOneFailedVerdict()
    {
        var task = new WorkflowTask("T1", "Do thing", "text", ["node", "--test", "test/t1.test.mjs"]);
        var plan = new WorkflowPlan(
            Change: "c1",
            Scope: "math",
            Base: "main",
            Tasks: [task],
            Checks: [],
            MaxAttempts: 1);

        var ledger = new WorkflowLedger();
        ledger.Verdicts.Add(new WorkflowVerdict("T1", 1, "abc1234", false, "not observed", ["green: failed"], DateTimeOffset.UtcNow));

        var missingDoc = new WorkflowDocState("", false, false, false, null, null);
        var state = new WorkflowState("/repo", "/repo/.hoshin", plan, "abc1234", ledger, null, missingDoc, missingDoc, missingDoc);

        var next = WorkflowEngine.Next(state);
        var done = Assert.IsType<Done>(next);
        Assert.False(done.Success);
        Assert.Contains("T1 failed 1", done.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkerProblems_AcceptsBuildTimeoutAndMaxAttemptsConfigAndRejectsInvalidValues()
    {
        Assert.True(Jsonc.TryParse("""
            {
              "version": 1,
              "systems": { "demo": { "repos": ["app"] } },
              "build": { "testTimeoutSeconds": 120, "checkTimeoutSeconds": 300, "maxAttempts": 2 }
            }
            """, out var valid, out _));
        Assert.Empty(MarkerReader.MarkerProblems(valid!.Value));

        Assert.True(Jsonc.TryParse("""
            {
              "version": 1,
              "systems": { "demo": { "repos": ["app"] } },
              "build": { "maxAttempts": 0 }
            }
            """, out var invalid, out _));
        Assert.NotEmpty(MarkerReader.MarkerProblems(invalid!.Value));
    }
}
