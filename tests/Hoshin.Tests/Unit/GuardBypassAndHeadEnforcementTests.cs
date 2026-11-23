using System.Text.Json;
using Hoshin.Cli.Commands;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class GuardBypassAndHeadEnforcementTests
{
    private static (TempDir Temp, string RepoDir, WorkflowRepo Repo) CreateFixture()
    {
        var tmp = new TempDir("hoshin-t04-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t04-sec", "type": "feat", "scope": "sec", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "sec", "status": "accepted" }
                ```

                # Plan: Sec

                ## Tasks

                ### T1: Add subtract

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
        var now = DateTimeOffset.UtcNow;
        repo.WriteRunMarker(new RunMarker(now, "s1", "claude-code", repo.ChangeFolder));
        repo.WriteStep(new CurrentStep(StepKind.NeedsApproval, null, 0, repo.Head(), now, SessionId: "s1", ChangeFolder: repo.ChangeFolder));
        return (tmp, repoDir, repo);
    }

    [Theory]
    [InlineData("git -c alias.p=push p origin")]
    [InlineData("git config alias.p push")]
    [InlineData("git --git-dir .git --work-tree . push")]
    [InlineData("g\"\"it push")]
    [InlineData("echo '{}' >.hoshin.json")]
    [InlineData("git commit -m x -n")]
    public void Guard_DeniesShellBypassVariations(string command)
    {
        var (tmp, repoDir, _) = CreateFixture();
        using var _ = tmp;

        var raw = JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command },
        });
        var reason = TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(raw)).Reason;
        Assert.NotNull(reason);
    }

    [Fact]
    public void EvaluateAndCommitTask_FailsWithoutCommitting_WhenHeadMovedOutsideWorkflow()
    {
        var (tmp, repoDir, repo) = CreateFixture();
        using var _ = tmp;

        var runner = TestServices.WorkflowRunner;
        var state = TestServices.ReadState(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        runner.Issue(repo, state, step, "s1");

        // Agent bypasses the guard and makes a manual git commit.
        TestGitFixture.Write(Path.Combine(repoDir, "manual.txt"), "bypass\n");
        TestGitFixture.Git(repoDir, "add", "manual.txt");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", "chore: manual commit");
        var headAfterManualCommit = repo.Head();

        // Also leave valid test + prod changes so evaluation would otherwise succeed.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "math.mjs"),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

        var updatedState = TestServices.ReadState(repo)!;
        var verdict = runner.EvaluateAndCommitTask(repo, updatedState, "T1", 1);

        Assert.False(verdict.Passed);
        Assert.Contains("git: HEAD moved outside the workflow", verdict.Reasons);
        Assert.Equal(headAfterManualCommit, repo.Head());
    }
}
