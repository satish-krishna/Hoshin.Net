using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class EvaluateSerializationTests
{
    [Fact]
    public async Task EvaluateAndCommitTask_ConcurrentCallsForSameAttempt_ProduceSingleCommitPairAndSameVerdict()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            using var tmp = new TempDir($"hoshin-t09-{iteration}-");
            var repoDir = Path.Combine(tmp.Path, "repo");
            TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
            {
                ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
                [".hoshin/change.jsonc"] = """
                    { "version": 1, "change": "t09-race", "type": "feat", "scope": "math", "base": "main" }
                    """,
                [".hoshin/plan.md"] = """
                    ```jsonc
                    { "layout": 2, "system": "math", "status": "accepted" }
                    ```

                    # Plan: Math

                    ## Tasks

                    ### T1: Add sub

                    ```jsonc
                    { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                    ```

                    #### Change
                    Add `sub(a, b)`.

                    #### Tests
                    Verify `sub(5, 2) === 3`.
                    """,
            });

            var repo = TestServices.FindRepo(repoDir)!;
            var startHead = repo.Head();
            var runner = TestServices.WorkflowRunner;
            var initialState = TestServices.ReadState(repo)!;
            var step = Assert.IsType<Implement>(WorkflowEngine.Next(initialState));
            runner.Issue(repo, initialState, step, "s1");

            TestGitFixture.Write(
                Path.Combine(repoDir, "test", "t1.test.mjs"),
                "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
            TestGitFixture.Write(
                Path.Combine(repoDir, "src", "math.mjs"),
                "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

            // Snapshot state before either thread enters EvaluateAndCommitTask (matching Stop + SubagentStop race).
            var sharedState = TestServices.ReadState(repo)!;
            using var barrier = new Barrier(2);

            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return runner.EvaluateAndCommitTask(repo, sharedState, "T1", 1);
            });
            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return runner.EvaluateAndCommitTask(repo, sharedState, "T1", 1);
            });

            var verdicts = await Task.WhenAll(t1, t2);
            var v1 = verdicts[0];
            var v2 = verdicts[1];

            Assert.True(v1.Passed, $"iter {iteration} v1 failed: {string.Join("; ", v1.Reasons)}");
            Assert.True(v2.Passed, $"iter {iteration} v2 failed: {string.Join("; ", v2.Reasons)}");
            Assert.Equal(v1.Head, v2.Head);
            Assert.Equal(v1.TestCommit, v2.TestCommit);

            var subjects = TestGitFixture.Git(repoDir, "log", "--format=%s", $"{startHead}..HEAD")
                .Replace("\r\n", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, subjects.Length);
            Assert.Equal("feat(math): Add sub", subjects[0]);
            Assert.Equal("test(math): Add sub", subjects[1]);
        }
    }
}
