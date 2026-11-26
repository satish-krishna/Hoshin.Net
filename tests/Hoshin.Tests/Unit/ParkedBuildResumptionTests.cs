using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R08 (W2): Parking a build after ignored Stops records parked state instead of deleting <c>run.json</c>,
/// never traps unrelated sessions, honors <c>stop_hook_active</c> on tampering checks, and allows a new session
/// to resume and complete the task via <c>/hoshin:build</c>.
/// </summary>
public sealed class ParkedBuildResumptionTests
{
    private static (TempDir Temp, string RepoDir, WorkflowRepo Repo) CreateSingleTaskFixture()
    {
        var tmp = new TempDir("hoshin-r08-park-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["package.json"] = """{ "name": "demo", "type": "module", "private": true }""",
            ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "r08-park", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Park and resume

                ## Tasks

                ### T1: Multiply

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/mul.test.mjs"] }
                ```

                #### Change
                Add multiply.

                #### Tests
                Test multiply.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        return (tmp, repoDir, repo);
    }

    [Fact]
    public void ParkedBuildAfterFourStops_TrapsNoSession_AndNewSessionResumesAndCompletesTask()
    {
        var (tmp, repoDir, repo) = CreateSingleTaskFixture();
        using var _ = tmp;

        var orchestrator = TestServices.CreateOrchestrator();

        // 1. Session s1 starts /hoshin:build -> T1 is issued.
        var (startOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(PromptPayload("s1", repoDir, "build")),
            rolesRoot: null);
        Assert.NotNull(startOut);
        using (var startDoc = JsonDocument.Parse(startOut!))
        {
            var startCtx = startDoc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
            Assert.Contains(HookInstructions.BeginMarker, startCtx, StringComparison.Ordinal);
        }

        // 2. Model ends its turn 4 times without dispatching -> 3 blocks, 4th parks the build.
        for (var i = 0; i < 4; i++)
        {
            var (stopOut, _) = orchestrator.HandleStop(
                TestServices.HookAdapter.Parse(StopPayload("s1", repoDir, stopHookActive: i > 0)),
                rolesRoot: null);
            if (i < 3)
            {
                Assert.Contains("\"decision\":\"block\"", stopOut ?? "", StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("parked", stopOut ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }

        // 3. A Stop from an unrelated session MUST be completely silent (not blocked as tampering!).
        var (unrelatedStop, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("s-unrelated", repoDir, stopHookActive: false)),
            rolesRoot: null);
        Assert.Null(unrelatedStop);

        // A further Stop from s1 is also silent.
        var (s1LaterStop, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("s1", repoDir, stopHookActive: false)),
            rolesRoot: null);
        Assert.Null(s1LaterStop);

        // 4. New session s2 runs /hoshin:build -> resumes, dispatches T1, completes it, and stops quietly.
        var (resumeOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(PromptPayload("s2", repoDir, "build")),
            rolesRoot: null);
        Assert.NotNull(resumeOut);
        using var doc = JsonDocument.Parse(resumeOut!);
        var ctx = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains(HookInstructions.BeginMarker, ctx, StringComparison.Ordinal);
        var start = ctx.IndexOf(HookInstructions.BeginMarker, StringComparison.Ordinal);
        var end = ctx.IndexOf(HookInstructions.EndMarker, StringComparison.Ordinal);
        var prompt = ctx[(start + HookInstructions.BeginMarker.Length)..end].Trim('\r', '\n');

        TestGitFixture.Write(Path.Combine(repoDir, "test", "mul.test.mjs"), """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { mul } from '../src/calc.mjs';
            test('mul', () => { assert.equal(mul(3, 4), 12); });
            """);
        TestGitFixture.Write(Path.Combine(repoDir, "src", "calc.mjs"), "export const add = (a, b) => a + b;\nexport const mul = (a, b) => a * b;\n");

        var (resOut, _) = orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = "s2",
                cwd = repoDir,
                tool_name = "Agent",
                tool_use_id = "toolu_t1",
                tool_input = new { subagent_type = "general-purpose", description = "Build T1", prompt },
                tool_response = new { status = "completed", content = new[] { new { type = "text", text = "Done." } } },
            })),
            rolesRoot: null);
        Assert.Contains("T1 attempt 1 is accepted", resOut ?? "", StringComparison.Ordinal);

        var verdicts = repo.ReadLedger().WorkflowVerdicts;
        Assert.Contains(verdicts, v => v.Task == "T1" && v.Passed);
    }

    [Fact]
    public void MissingRunMarkerTampering_AppliesOnlyToRunSession_AndHonorsStopHookActive()
    {
        var (tmp, repoDir, repo) = CreateSingleTaskFixture();
        using var _ = tmp;

        var orchestrator = TestServices.CreateOrchestrator();
        orchestrator.HandleStart(TestServices.HookAdapter.Parse(PromptPayload("s1", repoDir, "build")), rolesRoot: null);
        Assert.True(File.Exists(repo.RunMarkerPath));

        // Genuine tampering: delete run.json mid-build.
        File.Delete(repo.RunMarkerPath);

        // Unrelated session is never trapped.
        var (otherOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("s-other", repoDir, stopHookActive: false)),
            rolesRoot: null);
        Assert.Null(otherOut);

        // Chained stop_hook_active=true is never trapped in an infinite loop.
        var (activeOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("s1", repoDir, stopHookActive: true)),
            rolesRoot: null);
        Assert.Null(activeOut);

        // Original session with stop_hook_active=false IS still warned/blocked as tampering.
        var (ownerOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("s1", repoDir, stopHookActive: false)),
            rolesRoot: null);
        Assert.NotNull(ownerOut);
        Assert.Contains("run.json", ownerOut!, StringComparison.OrdinalIgnoreCase);

        // Missing session_id is never treated as the run's owning session.
        var (noSessionOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "Stop", cwd = repoDir, stop_hook_active = false })),
            rolesRoot: null);
        Assert.Null(noSessionOut);
    }

    private static string PromptPayload(string sessionId, string cwd, string skill) => JsonSerializer.Serialize(new
    {
        hook_event_name = "UserPromptExpansion",
        session_id = sessionId,
        cwd,
        command_name = $"hoshin:{skill}",
        command_args = "",
        prompt = $"/hoshin:{skill}",
    });

    private static string StopPayload(string sessionId, string cwd, bool stopHookActive) => JsonSerializer.Serialize(new
    {
        hook_event_name = "Stop",
        session_id = sessionId,
        cwd,
        stop_hook_active = stopHookActive,
    });
}
