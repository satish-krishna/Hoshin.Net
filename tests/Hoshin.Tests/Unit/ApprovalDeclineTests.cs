using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R22 (W3, D6): Typed <c>/hoshin:decline &lt;reason&gt;</c> at <see cref="NeedsApproval"/> records a declined approval
/// and routes through a fix step carrying the reason, checks, review, and <see cref="NeedsApproval"/> again;
/// missing reason, non-approval states, and model-sourced invocations are refused without changing state.
/// </summary>
public sealed class ApprovalDeclineTests
{
    private const string Reason = "shout must trim the name before shouting";

    [Fact]
    public void TypedDeclineAtNeedsApproval_RecordsDeclineAndRoutesThroughFixChecksReviewToNeedsApproval()
    {
        using var tmp = new TempDir("hoshin-r22-decline-");
        var (repoDir, repo) = CreateRepoAtNeedsApproval(tmp.Path);
        var orchestrator = TestServices.HookOrchestrator;
        var headBefore = repo.Head();
        var codeHeadBefore = TestServices.ReadState(repo)!.EffectiveCodeHead;

        // 1. Typed /hoshin:decline <reason> at NeedsApproval records declined approval and does not publish.
        var (declineOut, _) = orchestrator.HandleStart(
            ClaudeStart("s-decline", repoDir, "hoshin:decline", Reason),
            rolesRoot: null);
        Assert.NotNull(declineOut);
        var declineCtx = ExtractClaudeContext(declineOut!);
        Assert.Contains(Reason, declineCtx, StringComparison.Ordinal);
        Assert.DoesNotContain(HookInstructions.BeginMarker, declineCtx, StringComparison.Ordinal);

        var approval = Assert.Single(repo.ReadLedger().WorkflowApprovalRecords);
        Assert.False(approval.Approved);
        Assert.Equal(Reason, approval.Note);
        Assert.Equal(codeHeadBefore, approval.Head);

        // 2. Next step is a fix step carrying the engineer's reason.
        var afterDecline = TestServices.ReadState(repo)!;
        var fixStep = Assert.IsType<Implement>(WorkflowEngine.Next(afterDecline));
        Assert.StartsWith("FIX-", fixStep.Agent.Task, StringComparison.Ordinal);
        Assert.Contains(Reason, fixStep.Agent.Prompt, StringComparison.Ordinal);

        // 3. /hoshin:build dispatches the fix carrying the reason, commits it, runs checks, and advances to Review.
        var (buildOut, _) = orchestrator.HandleStart(
            ClaudeStart("s-build", repoDir, "hoshin:build", ""),
            rolesRoot: null);
        Assert.NotNull(buildOut);
        var buildCtx = ExtractClaudeContext(buildOut!);
        Assert.Contains(HookInstructions.BeginMarker, buildCtx, StringComparison.Ordinal);
        Assert.Contains(Reason, buildCtx, StringComparison.Ordinal);

        var nonce = repo.ReadStep()?.DispatchNonce;
        Assert.NotNull(nonce);
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "trim.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { greet } from '../src/greet.mjs';
            test('trims name', () => { assert.equal(greet('  Ada '), 'Hello, Ada'); });
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "greet.mjs"),
            "export const greet = (n) => `Hello, ${n.trim()}`;\n");

        var (resOut, resDecision) = orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = "s-build",
                cwd = repoDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Build fix\n{nonce}" },
                tool_response = new { status = "completed" },
            })),
            rolesRoot: null);
        Assert.NotNull(resOut);
        Assert.Contains("verdict passed", resDecision, StringComparison.Ordinal);
        Assert.NotEqual(headBefore, repo.Head());

        // Checks ran and passed at the new head; next step is Review 2.
        var stateAfterFix = TestServices.ReadState(repo)!;
        var reviewStep = Assert.IsType<ReviewDocument>(WorkflowEngine.Next(stateAfterFix));
        Assert.Contains("Review 2", reviewStep.Brief, StringComparison.Ordinal);

        // 4. Write Review 2 and Stop -> commits Review 2 and reaches NeedsApproval again.
        var newShort = WorkflowEngine.Short(stateAfterFix.EffectiveCodeHead);
        var reviewPath = Path.Combine(repoDir, ".hoshin", "build", "review.md");
        var existingReview = File.Exists(reviewPath) ? File.ReadAllText(reviewPath) : "";
        TestGitFixture.Write(
            reviewPath,
            existingReview + $$"""

            ## Review 2

            ```jsonc
            { "review": 2, "reviewed_at": "2026-11-27", "commits": { "repo": "{{newShort}}" } }
            ```

            No findings.
            """);
        var (stopOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                session_id = "s-build",
                cwd = repoDir,
                stop_hook_active = false,
                background_tasks = Array.Empty<object>(),
            })),
            rolesRoot: null);
        Assert.NotNull(stopOut);
        Assert.Contains("review is committed", stopOut!, StringComparison.OrdinalIgnoreCase);

        var finalState = TestServices.ReadState(repo)!;
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(finalState));
    }

    [Fact]
    public void DeclineWithoutReason_RefusedAndChangesNothing()
    {
        using var tmp = new TempDir("hoshin-r22-noreason-");
        var (repoDir, repo) = CreateRepoAtNeedsApproval(tmp.Path);
        var orchestrator = TestServices.HookOrchestrator;

        var (outJson, _) = orchestrator.HandleStart(
            ClaudeStart("s-noreason", repoDir, "hoshin:decline", "   "),
            rolesRoot: null);
        Assert.NotNull(outJson);
        Assert.Contains("reason", ExtractClaudeContext(outJson!), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repo.ReadLedger().WorkflowApprovalRecords);
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(repo)!));
    }

    [Fact]
    public void DeclineAtReview_RefusedAndChangesNothing()
    {
        using var tmp = new TempDir("hoshin-r22-review-");
        var (repoDir, repo) = CreateRepoAtReview(tmp.Path);
        var orchestrator = TestServices.HookOrchestrator;

        var (outJson, _) = orchestrator.HandleStart(
            ClaudeStart("s-review", repoDir, "hoshin:decline", Reason),
            rolesRoot: null);
        Assert.NotNull(outJson);
        Assert.Contains("decline", ExtractClaudeContext(outJson!), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(repo.ReadLedger().WorkflowApprovalRecords);
        Assert.IsType<ReviewDocument>(WorkflowEngine.Next(TestServices.ReadState(repo)!));
    }

    [Theory]
    [InlineData(HookHarness.ClaudeCode)]
    [InlineData(HookHarness.Antigravity)]
    public void ModelSourcedDecline_IsRefusedAndChangesNothing(HookHarness harness)
    {
        using var tmp = new TempDir($"hoshin-r22-model-{harness}-");
        var (repoDir, repo) = CreateRepoAtNeedsApproval(tmp.Path);
        var orchestrator = TestServices.HookOrchestrator;

        var ev = harness == HookHarness.ClaudeCode
            ? TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = "s-model-cc",
                cwd = repoDir,
                tool_name = "Skill",
                tool_input = new { skill = "hoshin:decline", args = Reason },
            }))
            : TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hookEventName = "PostToolUse",
                conversationId = "s-model-agy",
                cwd = repoDir,
                workspacePaths = new[] { repoDir },
                toolCall = new
                {
                    id = "call-1",
                    name = "read_file",
                    args = new { Path = Path.Combine(repoDir, ".agents", "skills", "decline", "SKILL.md") },
                },
            }));

        var (outJson, decision) = orchestrator.HandleStart(ev, rolesRoot: null);
        Assert.Contains("refused model-sourced", decision, StringComparison.OrdinalIgnoreCase);
        if (harness == HookHarness.ClaudeCode)
        {
            Assert.NotNull(outJson);
            Assert.Contains("decline", ExtractClaudeContext(outJson!), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(repo.ReadLedger().WorkflowApprovalRecords);
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(repo)!));
    }

    [Fact]
    public void DeclinedApprovalFix_RetriesWithExtraInstructions_AndStopsAfterMaxAttempts()
    {
        var doc = new WorkflowDocState("plan.md", true, true, false, "accepted", "accepted");
        var plan = new WorkflowPlan("c1", "shop", "main", [new WorkflowTask("T1", "Task 1", "Do T1", ["node", "--test"], Repo: "web")], []);
        var baseLedger = Hoshin.Workflow.Events.LedgerStore.Empty() with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "h1", true, "observed", [], DateTimeOffset.UtcNow)],
            Checks = [new WorkflowCheckRun("h1", true, "ok", DateTimeOffset.UtcNow)],
            Reviews = [new WorkflowReviewRun("h1", 1, 0, 0, DateTimeOffset.UtcNow)],
            WorkflowApprovals = [new WorkflowApproval("h1", false, Reason, DateTimeOffset.UtcNow)],
        };

        var retryLedger = baseLedger with
        {
            Verdicts =
            [
                .. baseLedger.WorkflowVerdicts,
                new WorkflowVerdict("FIX-DECLINE-1", 1, "h1", false, "skipped", ["no changes"], DateTimeOffset.UtcNow),
            ],
        };
        var retryState = new WorkflowState("repo", "change", plan, "h1", retryLedger, null, doc, doc, doc, SessionRepo: "web", BuildInstructions: "also update docs");
        var retry = Assert.IsType<Retry>(WorkflowEngine.Next(retryState));
        Assert.Equal("FIX-DECLINE-1", retry.Agent.Task);
        Assert.Equal(2, retry.Agent.Attempt);
        Assert.Contains(Reason, retry.Agent.Prompt, StringComparison.Ordinal);
        Assert.Contains("also update docs", retry.Agent.Prompt, StringComparison.Ordinal);

        var exhaustedLedger = baseLedger with
        {
            Verdicts =
            [
                .. baseLedger.WorkflowVerdicts,
                new WorkflowVerdict("FIX-DECLINE-1", 1, "h1", false, "skipped", ["r1"], DateTimeOffset.UtcNow),
                new WorkflowVerdict("FIX-DECLINE-1", 2, "h1", false, "skipped", ["r2"], DateTimeOffset.UtcNow),
                new WorkflowVerdict("FIX-DECLINE-1", 3, "h1", false, "skipped", ["r3"], DateTimeOffset.UtcNow),
            ],
        };
        var exhausted = Assert.IsType<Done>(WorkflowEngine.Next(new WorkflowState("repo", "change", plan, "h1", exhaustedLedger, null, doc, doc, doc)));
        Assert.False(exhausted.Success);
        Assert.Contains("FIX-DECLINE-1 failed 3 attempts", exhausted.Outcome, StringComparison.Ordinal);
    }

    private static NormalizedHookEvent ClaudeStart(string sessionId, string cwd, string commandName, string args) =>
        TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            session_id = sessionId,
            cwd,
            command_name = commandName,
            command_args = args,
            prompt = string.IsNullOrEmpty(args) ? $"/{commandName}" : $"/{commandName} {args}",
        }));

    private static string ExtractClaudeContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }

    private static (string RepoDir, WorkflowRepo Repo) CreateRepoAtReview(string root)
    {
        var repoDir = Path.Combine(root, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/greet.mjs"] = "export const greet = (n) => `Hello, ${n}`;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "2026-11-27-decline-demo", "branch": "change/decline-demo", "type": "feat", "scope": "greet", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "greet", "status": "accepted" }
                ```

                # Plan: Decline demo

                ## Tasks

                ### T1: Greet

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Change
                Greet.

                #### Tests
                Test greet.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var head = repo.Head();
        repo.UpdateLedger(l => l with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, head, true, "observed", [], DateTimeOffset.UtcNow)],
            Checks = [new WorkflowCheckRun(head, true, "ok", DateTimeOffset.UtcNow)],
        });
        return (repoDir, repo);
    }

    private static (string RepoDir, WorkflowRepo Repo) CreateRepoAtNeedsApproval(string root)
    {
        var (repoDir, repo) = CreateRepoAtReview(root);
        var head = repo.Head();
        var shortHead = WorkflowEngine.Short(head);
        TestGitFixture.Write(
            Path.Combine(repoDir, ".hoshin", "build", "review.md"),
            $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-27", "commits": { "repo": "{{shortHead}}" } }
            ```

            No findings.
            """);
        TestGitFixture.Git(repoDir, "add", ".hoshin/build/review.md");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", "docs(greet): review 1 decline-demo", "-m", "Generated-By: hoshin");

        var state = TestServices.ReadState(repo)!;
        var codeHead = state.EffectiveCodeHead;
        repo.UpdateLedger(l => l with
        {
            Checks = [new WorkflowCheckRun(codeHead, true, "ok", DateTimeOffset.UtcNow)],
            Reviews = [new WorkflowReviewRun(codeHead, 1, 0, 0, DateTimeOffset.UtcNow)],
        });
        return (repoDir, repo);
    }
}
