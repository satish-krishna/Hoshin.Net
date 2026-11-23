using System.Text.Json;
using Hoshin.Cli.Commands;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class SessionIsolationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");

    private static (TempDir Temp, string RepoDir, WorkflowRepo Repo) CreateActiveBuildFixture(string ownerSession = "claude-a")
    {
        var tmp = new TempDir("hoshin-t22-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t22-session", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Session Isolation

                ## Tasks

                ### T1: Add sub

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                ```

                #### Change
                Add `sub`.

                #### Tests
                Verify `sub`.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var orchestrator = TestServices.CreateOrchestrator();
        var startEv = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = ownerSession,
            command_name = "hoshin:build",
            prompt = "/hoshin:build",
        }));
        orchestrator.HandleStart(startEv, SkillsDir);
        return (tmp, repoDir, repo);
    }

    [Fact]
    public void HandleStop_UnrelatedClaudeSessionDuringImplement_ReturnsEmptyAndLeavesStepUnchanged()
    {
        var (tmp, repoDir, repo) = CreateActiveBuildFixture("claude-a");
        using var _ = tmp;

        var stepBefore = File.ReadAllText(repo.StepPath);
        var orchestrator = TestServices.CreateOrchestrator();
        var stopEv = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            cwd = repoDir,
            session_id = "claude-b",
        }));

        var (output, _) = orchestrator.HandleStop(stopEv, SkillsDir);
        Assert.Null(output);
        Assert.Equal(stepBefore, File.ReadAllText(repo.StepPath));
    }

    [Fact]
    public void HandleResult_UnrelatedClaudeSessionAgent_RecordsNoVerdict()
    {
        var (tmp, repoDir, repo) = CreateActiveBuildFixture("claude-a");
        using var _ = tmp;

        var orchestrator = TestServices.CreateOrchestrator();
        var resultEv = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            cwd = repoDir,
            session_id = "claude-b",
            tool_name = "Agent",
        }));

        var (output, _) = orchestrator.HandleResult(resultEv, SkillsDir);
        Assert.Null(output);
        Assert.Empty(repo.ReadLedger().WorkflowVerdicts);
    }

    [Fact]
    public void Guard_AllowsUnrelatedSessionUnrelatedAgentAndMissingSessionDuringRedPhase()
    {
        var (tmp, repoDir, repo) = CreateActiveBuildFixture("claude-a");
        using var cleanup = tmp;

        // Register session A's active subagent so step.AgentId is set.
        Assert.True(TestServices.WorkflowRunner.TryRegisterActiveSubagent(repo, "T1", 1, "agent-a1", out _));
        var prodPath = Path.Combine(repoDir, "src", "math.mjs");

        // 1. Unrelated session B (even while step.AgentId is non-null!) must be allowed.
        var evSessionB = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "claude-b",
            tool_name = "Edit",
            tool_input = new { file_path = prodPath },
        }));
        Assert.Null(TestServices.EvaluateGuard(evSessionB).Reason);

        // 2. Unrelated agent_id from session B must be allowed.
        var evOtherAgent = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "claude-b",
            agent_id = "agent-b99",
            tool_name = "Edit",
            tool_input = new { file_path = prodPath },
        }));
        Assert.Null(TestServices.EvaluateGuard(evOtherAgent).Reason);

        // 3. Missing session_id must NOT be treated as in the run.
        var evMissingSession = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            tool_name = "Edit",
            tool_input = new { file_path = prodPath },
        }));
        Assert.Null(TestServices.EvaluateGuard(evMissingSession).Reason);

        // 4. Session A's registered subagent (agent_id == agent-a1) IS denied during red phase!
        var evRegisteredAgent = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "claude-a",
            agent_id = "agent-a1",
            tool_name = "Edit",
            tool_input = new { file_path = prodPath },
        }));
        Assert.NotNull(TestServices.EvaluateGuard(evRegisteredAgent).Reason);
    }
}
