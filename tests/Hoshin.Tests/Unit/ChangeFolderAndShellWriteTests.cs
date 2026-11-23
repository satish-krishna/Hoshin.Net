using System.Text.Json;
using Hoshin.Cli.Commands;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class ChangeFolderAndShellWriteTests
{
    private static (TempDir Temp, string RepoDir, WorkflowRepo Repo) CreateStandardLayoutFixture()
    {
        var tmp = new TempDir("hoshin-t06-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
            ["intent/demo/c1/change.jsonc"] = """
                { "version": 1, "change": "c1", "type": "feat", "scope": "demo", "base": "main" }
                """,
            ["intent/demo/c1/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Demo

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
        return (tmp, repoDir, repo);
    }

    [Fact]
    public void EvaluateAndCommitTask_FailsWithProtectedReason_WhenChangeFolderPlanMdIsModified()
    {
        var (tmp, repoDir, repo) = CreateStandardLayoutFixture();
        using var _ = tmp;

        var startHead = repo.Head();
        // Agent writes valid test + prod files AND also tampers with intent/demo/c1/plan.md.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "math.mjs"),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "intent", "demo", "c1", "plan.md"),
            File.ReadAllText(Path.Combine(repoDir, "intent", "demo", "c1", "plan.md")) + "\n<!-- tampered -->\n");

        var state = TestServices.ReadState(repo)!;
        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, state, "T1", 1);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.StartsWith("protected:", StringComparison.Ordinal));
        Assert.Equal(startHead, repo.Head());
    }

    [Fact]
    public void Guard_DeniesChangeFolderEditsAndShellRedirectsDuringImplementRedPhase()
    {
        var (tmp, repoDir, repo) = CreateStandardLayoutFixture();
        using var _ = tmp;

        var now = DateTimeOffset.UtcNow;
        repo.WriteRunMarker(new RunMarker(now, "s1", "claude-code", repo.ChangeFolder));
        repo.WriteStep(new CurrentStep("Implement", "T1", 1, repo.Head(), now, SessionId: "s1", ChangeFolder: repo.ChangeFolder, TestPatterns: WorkflowPlan.DefaultTestPatterns));

        string? EvalShell(string command)
        {
            var raw = JsonSerializer.Serialize(new
            {
                hook_event_name = "PreToolUse",
                cwd = repoDir,
                session_id = "s1",
                tool_name = "Bash",
                tool_input = new { command },
            });
            return TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(raw)).Reason;
        }

        string? EvalEdit(string filePath)
        {
            var raw = JsonSerializer.Serialize(new
            {
                hook_event_name = "PreToolUse",
                cwd = repoDir,
                session_id = "s1",
                tool_name = "Edit",
                tool_input = new { file_path = filePath },
            });
            return TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(raw)).Reason;
        }

        // Red-phase shell redirects and tee to production file must be denied before any test file exists.
        Assert.NotNull(EvalShell("cat > src/prod.mjs <<'EOF'\nexport const a = 1;\nEOF"));
        Assert.NotNull(EvalShell("echo 'x' | tee src/prod.mjs"));
        Assert.NotNull(EvalShell("echo 'x' | tee -a src/prod.mjs"));

        // Redirect to a test file during red-phase IS allowed.
        Assert.Null(EvalShell("cat > test/t1.test.mjs <<'EOF'\n// test\nEOF"));

        // Editing plan.md in the change folder during Implement must be denied (both Edit and redirect).
        Assert.NotNull(EvalEdit(Path.Combine(repoDir, "intent", "demo", "c1", "plan.md")));
        Assert.NotNull(EvalShell("echo x >> intent/demo/c1/plan.md"));
    }

    [Fact]
    public void Guard_DeniesShellRedirectToOtherFilesDuringWriteDocument()
    {
        var (tmp, repoDir, repo) = CreateStandardLayoutFixture();
        using var _ = tmp;

        var now = DateTimeOffset.UtcNow;
        repo.WriteRunMarker(new RunMarker(now, "s1", "claude-code", repo.ChangeFolder));
        repo.WriteStep(new CurrentStep("WriteDocument", "intent", 0, repo.Head(), now, SessionId: "s1", ChangeFolder: repo.ChangeFolder));

        var denyProd = TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command = "echo x > src/a.cs" },
        }))).Reason;
        Assert.NotNull(denyProd);

        var denyTee = TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command = "echo x | tee src/a.cs" },
        }))).Reason;
        Assert.NotNull(denyTee);

        // Redirect to the single allowed intent.md file is permitted.
        var allowIntent = TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command = "cat > intent/demo/c1/intent.md <<'EOF'\n# Intent\nEOF" },
        }))).Reason;
        Assert.Null(allowIntent);
    }
}
