using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class SubagentNonceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");

    [Fact]
    public void HandleResult_ClaudeAgentWithoutDispatchNonce_IsIgnoredAndLeavesAttemptOpen()
    {
        using var tmp = new TempDir("hoshin-t24-claude-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t24-nonce", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Nonce

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

        var repo = WorkflowRepo.Find(repoDir)!;
        var orchestrator = new HookOrchestrator();

        var (startOut, _) = orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = "s1",
            command_name = "hoshin:build",
            prompt = "/hoshin:build",
        })), SkillsDir);

        var startCtx = JsonDocument.Parse(startOut!).RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        var nonceMatch = Regex.Match(startCtx, @"<!-- hoshin:T1:attempt-1:[0-9a-f]{8} -->");
        Assert.True(nonceMatch.Success, $"Expected dispatch nonce in start context:\n{startCtx}");
        var nonce = nonceMatch.Value;

        // 1. Explore/research Agent call without nonce must NOT burn attempt 1!
        var (exploreOut, _) = orchestrator.HandleResult(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Agent",
            tool_input = new { prompt = "Explore the codebase and find math files" },
        })), SkillsDir);

        Assert.Null(exploreOut);
        Assert.Empty(repo.ReadLedger().Verdicts);
        var nextAfterExplore = Assert.IsType<Implement>(WorkflowEngine.Next(WorkflowState.Read(repo, SkillsDir)!));
        Assert.Equal("T1", nextAfterExplore.Agent.Task);
        Assert.Equal(1, nextAfterExplore.Agent.Attempt);

        // 2. Real implement subagent carrying the nonce IS judged and accepted!
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "math.mjs"),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

        var (implOut, _) = orchestrator.HandleResult(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Agent",
            tool_input = new { prompt = $"{nonce}\n# Implement one task, test first" },
        })), SkillsDir);

        Assert.NotNull(implOut);
        var verdict = Assert.Single(repo.ReadLedger().Verdicts);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
    }

    [Fact]
    public void HandleResult_AntigravitySubagentWithoutDispatchNonce_DoesNotRegisterActiveSubagent()
    {
        using var tmp = new TempDir("hoshin-t24-agy-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t24-agy", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Nonce

                ## Tasks

                ### T1: Add sub

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                ```

                #### Tests
                Verify.
                """,
        });

        var repo = WorkflowRepo.Find(repoDir)!;
        var orchestrator = new HookOrchestrator();

        orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-p1",
            cwd = repoDir,
            workspacePaths = new[] { repoDir },
            invocationNum = 0,
            lastUserInput = "/build",
        })), SkillsDir);

        var (researchOut, _) = orchestrator.HandleResult(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hookEventName = "PostToolUse",
            conversationId = "agy-p1",
            cwd = repoDir,
            toolCall = new { id = "r1", name = "invoke_subagent", args = new { TypeName = "research", Role = "Researcher", Prompt = "Look up files" } },
            toolResponse = new { conversationId = "agy-research-1" },
        })), SkillsDir);

        Assert.Equal("{}", researchOut);
        Assert.Null(repo.ReadLedger().ActiveSubagent);
        Assert.Null(repo.ReadStep()?.AgentId);
    }
}
