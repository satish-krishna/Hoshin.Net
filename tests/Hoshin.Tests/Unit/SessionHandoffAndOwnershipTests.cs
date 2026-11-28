using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R23 (S16, R03 × R07): Per-session hand-off is distinct from change-level <see cref="Done"/>,
/// <c>run.json</c> is cleared only by its owner or on change-level <see cref="Done"/>, and every
/// owned step blocks other sessions with a message naming the owner rather than silence or takeover.
/// </summary>
public sealed class SessionHandoffAndOwnershipTests
{
    [Fact]
    public void MasterAtReview_ProductBuildAndSecondMasterReview_AreBlockedWithoutClearingRunAndReviewRecords()
    {
        using var tmp = new TempDir("hoshin-r23-review-");
        var (masterDir, apiDir, webDir) = CreateWorkspaceWithBothTasksBuilt(tmp.Path, out var copyRoot, out var changeFolder);
        var orchestrator = TestServices.HookOrchestrator;

        // 1. Master session starts /hoshin:review and owns run.json + Review step.
        const string poSession = "po-r23";
        var (reviewStartOut, _) = orchestrator.HandleStart(
            StartEvent(poSession, masterDir, "hoshin:review", "/hoshin:review"),
            rolesRoot: null);
        Assert.NotNull(reviewStartOut);
        var reviewBrief = ExtractContext(reviewStartOut!);
        Assert.Contains("Review 1", reviewBrief, StringComparison.Ordinal);

        var repo = TestServices.Default.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        Assert.True(File.Exists(repo.RunMarkerPath));
        Assert.Equal(poSession, repo.ReadRunMarker()?.SessionId);

        // 2. While master writes the review, product session with no tasks left types /hoshin:build.
        var (apiOut, _) = orchestrator.HandleStart(
            StartEvent("api-r23", apiDir, "hoshin:build", "/hoshin:build"),
            rolesRoot: null);
        Assert.NotNull(apiOut);
        var apiCtx = ExtractContext(apiOut!);
        Assert.DoesNotContain(HookInstructions.BeginMarker, apiCtx, StringComparison.Ordinal);
        Assert.Contains(poSession, apiCtx, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.RunMarkerPath), "another session must never clear the owning session's run.json");
        Assert.Equal(poSession, repo.ReadRunMarker()?.SessionId);

        // 3. A second master session typing /hoshin:review is told who owns it and does NOT get the review brief.
        var (secondOut, _) = orchestrator.HandleStart(
            StartEvent("po-second-r23", masterDir, "hoshin:review", "/hoshin:review"),
            rolesRoot: null);
        Assert.NotNull(secondOut);
        var secondCtx = ExtractContext(secondOut!);
        Assert.Contains(poSession, secondCtx, StringComparison.Ordinal);
        Assert.DoesNotContain("Write the review section", secondCtx, StringComparison.Ordinal);
        Assert.Equal(poSession, repo.ReadRunMarker()?.SessionId);

        // 4. An unrelated non-hoshin prompt from another session stays silent.
        var (unrelatedOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = "other-chat",
                cwd = masterDir,
                prompt = "explain src/calc.mjs",
            })),
            rolesRoot: null);
        Assert.Null(unrelatedOut);

        // 5. The owning master session writes review.md and Stop commits and records Review 1.
        var apiHead = WorkflowEngine.Short(TestGitFixture.Git(apiDir, "rev-parse", "HEAD"));
        var webHead = WorkflowEngine.Short(TestGitFixture.Git(webDir, "rev-parse", "HEAD"));
        TestGitFixture.Write(
            Path.Combine(changeFolder, "build", "review.md"),
            $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-27", "commits": { "api": "{{apiHead}}", "web": "{{webHead}}" } }
            ```

            No findings.
            """);

        var (stopOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                session_id = poSession,
                cwd = masterDir,
                stop_hook_active = false,
                background_tasks = Array.Empty<object>(),
            })),
            rolesRoot: null);
        Assert.NotNull(stopOut);
        Assert.Contains("review is committed", stopOut!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(repo.ReadLedger().WorkflowReviews);
    }

    [Theory]
    [InlineData("intent", "The intent for change")]
    [InlineData("fix", "Light-track fix")]
    public void OtherSession_CannotTakeOverOwnedDocumentOrFixStep_AndIsToldOwner(string skill, string briefMarker)
    {
        using var tmp = new TempDir($"hoshin-r23-{skill}-");
        var masterDir = CreateEmptyWorkspace(tmp.Path);
        var orchestrator = TestServices.HookOrchestrator;

        const string ownerSession = "owner-session";
        var (ownerOut, _) = orchestrator.HandleStart(
            StartEvent(ownerSession, masterDir, $"hoshin:{skill}", $"/hoshin:{skill} add-shout"),
            rolesRoot: null);
        Assert.NotNull(ownerOut);
        Assert.Contains(briefMarker, ExtractContext(ownerOut!), StringComparison.OrdinalIgnoreCase);

        var (secondOut, _) = orchestrator.HandleStart(
            StartEvent("second-session", masterDir, $"hoshin:{skill}", $"/hoshin:{skill} add-shout"),
            rolesRoot: null);
        Assert.NotNull(secondOut);
        var secondCtx = ExtractContext(secondOut!);
        Assert.Contains(ownerSession, secondCtx, StringComparison.Ordinal);
        Assert.DoesNotContain(briefMarker, secondCtx, StringComparison.OrdinalIgnoreCase);

        var resolved = TestServices.Default.HookRunResolver.Resolve(StartEvent(ownerSession, masterDir, $"hoshin:{skill}", $"/hoshin:{skill}"));
        Assert.NotNull(resolved);
        Assert.Equal(ownerSession, resolved!.WorkflowRepo.ReadRunMarker()?.SessionId);
    }

    [Fact]
    public void HandoffStepKind_IsDistinctFromDone_HasNoOwner_AndFormatsInstructions()
    {
        var doc = new WorkflowDocState("plan.md", true, true, false, "accepted", "accepted");
        var plan = new WorkflowPlan(
            "c1",
            "shop",
            "main",
            [
                new WorkflowTask("T1", "Api task", "Do T1", ["node", "--test"], Repo: "api"),
                new WorkflowTask("T2", "Web task", "Do T2", ["node", "--test"], Repo: "web"),
            ],
            []);
        var ledger = Hoshin.Workflow.Events.LedgerStore.Empty() with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "h1", true, "observed", [], DateTimeOffset.UtcNow)],
        };
        var stateNextRepo = new WorkflowState("repo", "change", plan, "h1", ledger, null, doc, doc, doc, SessionRepo: "api");
        var handoffNextRepo = Assert.IsType<Handoff>(WorkflowEngine.Next(stateNextRepo));
        Assert.Equal(StepKind.Handoff, handoffNextRepo.Kind);
        Assert.Null(WorkflowEngine.OwnerOf(handoffNextRepo));
        Assert.False(WorkflowEngine.HasOwner(StepKind.Handoff));
        Assert.False(WorkflowEngine.HasOwner(StepKind.Done));
        Assert.True(WorkflowEngine.HasOwner(StepKind.WriteDocument));
        Assert.True(WorkflowEngine.HasOwner(StepKind.Review));
        Assert.True(WorkflowEngine.HasOwner(StepKind.Fix));
        Assert.True(WorkflowEngine.HasOwner(StepKind.Implement));
        Assert.True(WorkflowEngine.HasOwner(StepKind.NeedsApproval));
        Assert.Contains("all tasks for `api` are built", WorkflowEngine.Describe(handoffNextRepo), StringComparison.Ordinal);
        Assert.Contains("all tasks for `api` are built", HookInstructions.For(handoffNextRepo, HookHarness.ClaudeCode), StringComparison.Ordinal);

        var allBuiltLedger = ledger with
        {
            Verdicts =
            [
                new WorkflowVerdict("T1", 1, "h1", true, "observed", [], DateTimeOffset.UtcNow),
                new WorkflowVerdict("T2", 1, "h2", true, "observed", [], DateTimeOffset.UtcNow),
            ],
            Checks = [new WorkflowCheckRun("h2", true, "ok", DateTimeOffset.UtcNow)],
        };
        var stateReview = new WorkflowState("repo", "change", plan, "h2", allBuiltLedger, null, doc, doc, doc, SessionRepo: "web");
        var handoffReview = Assert.IsType<Handoff>(WorkflowEngine.Next(stateReview));
        Assert.Contains("review", handoffReview.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static NormalizedHookEvent StartEvent(string sessionId, string cwd, string commandName, string prompt) =>
        TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            session_id = sessionId,
            cwd,
            command_name = commandName,
            command_args = prompt.Contains(' ', StringComparison.Ordinal) ? prompt[(prompt.IndexOf(' ', StringComparison.Ordinal) + 1)..] : "",
            prompt,
        }));

    private static string ExtractContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }

    private static string CreateEmptyWorkspace(string root)
    {
        var masterDir = Path.Combine(root, "ai-sdlc");
        var apiDir = Path.Combine(root, "api");
        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": {
                    "shop": { "repos": ["api"] }
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(apiDir, new Dictionary<string, string> { ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n" });
        return masterDir;
    }

    private static (string Master, string Api, string Web) CreateWorkspaceWithBothTasksBuilt(
        string root,
        out string copyRoot,
        out string changeFolder)
    {
        var masterDir = Path.Combine(root, "ai-sdlc");
        var apiDir = Path.Combine(root, "api");
        var webDir = Path.Combine(root, "web");
        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": {
                    "shop": { "repos": ["api", "web"] }
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(apiDir, new Dictionary<string, string> { ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n" });
        TestGitFixture.InitRepo(webDir, new Dictionary<string, string> { ["src/greet.mjs"] = "export const greet = (n) => `Hello, ${n}`;\n" });

        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "add-shout");
        Assert.True(started.Ok, started.Error);
        copyRoot = started.CopyRoot!;
        changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        TestGitFixture.Write(
            Path.Combine(changeFolder, "intent.md"),
            """
            ```jsonc
            { "title": "Add shout", "system": "shop", "author": "Pat", "date": "2026-11-27", "status": "accepted", "scope": "shop" }
            ```

            # Intent: Add shout
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept intent {changeName}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(changeFolder, "spec.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Add shout

            ```gherkin
            Feature: Add shout

              @R1 @from:intent
              Rule: Calculator multiplies
                @R1.1
                Scenario: Multiply
                  Given 3 and 4
                  When multiplied
                  Then 12

              @R2 @from:intent
              Rule: Greeting shouts
                @R2.1
                Scenario: Shout
                  Given Ada
                  When shouted
                  Then Hello Ada!
            ```
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept spec {changeName}", "-m", "Generated-By: hoshin");
        var specSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(changeFolder, "plan.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Add shout

            ## Tasks

            ### T1: Multiply

            ```jsonc
            { "id": "T1", "repo": "api", "depends_on": [], "covers": ["R1.1"], "test": ["node", "--test", "test/calc.test.mjs"] }
            ```

            #### Change
            Add `mul`.

            #### Tests
            Test `mul`.

            ### T2: Shout

            ```jsonc
            { "id": "T2", "repo": "web", "depends_on": [], "covers": ["R2.1"], "test": ["node", "--test", "test/shout.test.mjs"] }
            ```

            #### Change
            Add `shout`.

            #### Tests
            Test `shout`.
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept plan {changeName}", "-m", "Generated-By: hoshin");

        TestGitFixture.Git(apiDir, "switch", "-q", "-c", $"change/{changeName}");
        TestGitFixture.Write(Path.Combine(apiDir, "src", "calc.mjs"), "export const add = (a, b) => a + b;\nexport const mul = (a, b) => a * b;\n");
        TestGitFixture.Git(apiDir, "add", ".");
        TestGitFixture.Git(apiDir, "commit", "-q", "-m", "feat(shop): multiply", "-m", "Plan-Task: T1\nGenerated-By: hoshin");
        var apiCommit = TestGitFixture.Git(apiDir, "rev-parse", "--short", "HEAD");

        TestGitFixture.Git(webDir, "switch", "-q", "-c", $"change/{changeName}");
        TestGitFixture.Write(Path.Combine(webDir, "src", "greet.mjs"), "export const greet = (n) => `Hello, ${n}`;\nexport const shout = (n) => `${greet(n)}!`;\n");
        TestGitFixture.Git(webDir, "add", ".");
        TestGitFixture.Git(webDir, "commit", "-q", "-m", "feat(shop): shout", "-m", "Plan-Task: T2\nGenerated-By: hoshin");
        var webCommit = TestGitFixture.Git(webDir, "rev-parse", "--short", "HEAD");

        TestGitFixture.Write(
            Path.Combine(changeFolder, "build", "api.md"),
            $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Build log: api

            ### T1

            ```jsonc
            { "task": "T1", "commits": ["{{apiCommit}}"], "red": "observed" }
            ```
            """);
        TestGitFixture.Write(
            Path.Combine(changeFolder, "build", "web.md"),
            $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Build log: web

            ### T2

            ```jsonc
            { "task": "T2", "commits": ["{{webCommit}}"], "red": "observed" }
            ```
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): log T1 and T2 {changeName}", "-m", "Generated-By: hoshin");

        var repo = TestServices.Default.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        var state = TestServices.Default.WorkflowStateReader.Read(repo, null)!;
        repo.UpdateLedger(l => l with
        {
            Checks = [new WorkflowCheckRun(state.EffectiveCodeHead, true, "ok", DateTimeOffset.UtcNow)],
        });

        return (masterDir, apiDir, webDir);
    }
}
