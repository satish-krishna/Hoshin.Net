using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// A8: hook-driven verification of reject, reopen, and light-track <c>fix --accept</c> flows
/// (ported from <c>state.test.mjs</c> and <c>fix-state.test.mjs</c>).
/// </summary>
public sealed class RejectReopenFixAcceptHookTests
{
    private const string Change = "2026-11-22-fix-greeting";
    private const string FolderRel = $"intent/demo/{Change}";

    private static (string Master, string ChangeFolder) CreateWorkspace(
        TempDir tmp,
        string track,
        string intentStatus = "draft",
        bool withSpec = false,
        bool withPlan = false,
        bool acceptAll = false)
    {
        var master = Path.Combine(tmp.Path, "master");
        TestGitFixture.InitRepo(master, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": { "demo": { "apps": { "app": "." } } }
                }
                """,
            ["src/greet.mjs"] = "export function greet() { return 'Hello'; }\n",
        });

        TestGitFixture.Git(master, "checkout", "-q", "-b", $"change/{Change}");

        var changeFolder = Path.Combine(master, "intent", "demo", Change);
        Directory.CreateDirectory(changeFolder);

        TestGitFixture.Write(Path.Combine(changeFolder, "change.jsonc"), $$"""
            {
              "version": 1,
              "change": "{{Change}}",
              "branch": "change/{{Change}}",
              "type": "{{(track == "fix" ? "fix" : "feat")}}",
              "scope": "demo",
              "base": "main"
            }
            """);

        TestGitFixture.Write(Path.Combine(changeFolder, "intent.md"), IntentText(intentStatus, track));
        TestGitFixture.Git(master, "add", "--", $"{FolderRel}/change.jsonc", $"{FolderRel}/intent.md");
        TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): draft intent {Change}", "-m", "Generated-By: hoshin");

        if (acceptAll)
        {
            TestGitFixture.Write(Path.Combine(changeFolder, "intent.md"), IntentText("accepted", track));
            TestGitFixture.Git(master, "add", "--", $"{FolderRel}/intent.md");
            TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): accept intent {Change}", "-m", "Generated-By: hoshin");
        }

        var intentSha = TestServices.Git.CommitOf(master, "HEAD")!;

        if (withSpec)
        {
            var specStatus = acceptAll ? "accepted" : "draft";
            var specPin = acceptAll ? intentSha : "pending acceptance";
            TestGitFixture.Write(Path.Combine(changeFolder, "spec.md"), SpecText(specStatus, specPin));
            TestGitFixture.Git(master, "add", "--", $"{FolderRel}/spec.md");
            TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): {(acceptAll ? "accept" : "draft")} spec {Change}", "-m", "Generated-By: hoshin");
        }

        var specSha = TestServices.Git.CommitOf(master, "HEAD")!;

        if (withPlan)
        {
            var planStatus = acceptAll ? "accepted" : "draft";
            var planPin = acceptAll ? specSha : "pending acceptance";
            TestGitFixture.Write(Path.Combine(changeFolder, "plan.md"), PlanText(planStatus, planPin));
            TestGitFixture.Git(master, "add", "--", $"{FolderRel}/plan.md");
            TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): {(acceptAll ? "accept" : "draft")} plan {Change}", "-m", "Generated-By: hoshin");
        }

        return (master, changeFolder);
    }

    private static string IntentText(string status, string track) => $$"""
        ```jsonc
        {
          "title": "Fix greeting",
          "system": "demo",
          "author": "Engineer",
          "date": "2026-11-22",
          "status": "{{status}}",
          "scope": "demo",
          "track": "{{track}}"
        }
        ```

        # Intent: Fix greeting

        ## Problem

        Greeting has a typo.

        ## Proposed outcome

        Greeting is spelled correctly.

        ## Affected users and systems

        Demo callers.

        ## Constraints

        Single repository.

        ## Decisions

        D1: return Hello.

        ## Open questions

        None.
        """;

    private static string SpecText(string status, string pin) => $$"""
        ```jsonc
        {
          "layout": 2,
          "source_intent": "{{Change}}/intent.md",
          "source_intent_commit": "{{pin}}",
          "system": "demo",
          "date": "2026-11-22",
          "status": "{{status}}"
        }
        ```

        # Spec: Fix greeting

        ## Requirements

        ```gherkin
        Feature: Fix greeting
          @R1 @from:D1
          Rule: Greeting says Hello
            @R1.1
            Scenario: Returns Hello
              Given a caller
              When greet is called
              Then it returns Hello
        ```
        """;

    private static string PlanText(string status, string pin) => $$"""
        ```jsonc
        {
          "layout": 2,
          "source_spec": "{{Change}}/spec.md",
          "source_spec_commit": "{{pin}}",
          "system": "demo",
          "date": "2026-11-22",
          "status": "{{status}}"
        }
        ```

        # Plan: Fix greeting

        ## Tasks

        ### T1: Fix greeting

        ```jsonc
        {
          "id": "T1",
          "repo": "app",
          "depends_on": [],
          "covers": ["R1.1"],
          "test": ["node", "-e", "process.exit(0)"]
        }
        ```

        #### Change

        Return Hello.

        #### Tests

        Verify Hello.
        """;

    private static string ExtractContext(string? output)
    {
        Assert.False(string.IsNullOrWhiteSpace(output));
        return JsonDocument.Parse(output!).RootElement
            .GetProperty("hookSpecificOutput")
            .GetProperty("additionalContext")
            .GetString()!;
    }

    private static NormalizedHookEvent UserStart(string cwd, string sessionId, string skill, string args = "")
    {
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd,
            session_id = sessionId,
            command_name = $"hoshin:{skill}",
            command_args = args,
            prompt = string.IsNullOrWhiteSpace(args) ? $"/hoshin:{skill}" : $"/hoshin:{skill} {args}",
        });
        return TestServices.HookAdapter.Parse(payload);
    }

    private static NormalizedHookEvent ModelStart(string cwd, string sessionId, string skill, string args = "")
    {
        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            cwd,
            session_id = sessionId,
            tool_name = "Skill",
            tool_input = new { skill = $"hoshin:{skill}", args },
        });
        return TestServices.HookAdapter.Parse(payload);
    }

    [Fact]
    public void RejectHook_RefusesWhenUncommittedOrBuiltOrModelSourced_AndCommitsRejectionWhenClean()
    {
        using var tmp = new TempDir("hoshin-a8-reject-");
        var (master, changeFolder) = CreateWorkspace(tmp, track: "full");
        var orch = TestServices.CreateOrchestrator();

        // Model-sourced reject is refused.
        var (modelOut, _) = orch.HandleStart(ModelStart(master, "s-reject", "reject"), null);
        Assert.Contains("Only the engineer", ExtractContext(modelOut));

        // Uncommitted edit to intent.md -> refused with ReadinessMap's exact stop message.
        var intentPath = Path.Combine(changeFolder, "intent.md");
        var originalIntent = File.ReadAllText(intentPath);
        File.WriteAllText(intentPath, originalIntent + "\nUncommitted note.\n");
        var (dirtyOut, _) = orch.HandleStart(UserStart(master, "s-reject", "reject"), null);
        Assert.Contains("has uncommitted changes. Commit or discard them first, so the rejection does not carry them unread.", ExtractContext(dirtyOut));
        File.WriteAllText(intentPath, originalIntent);

        // Build log present -> refused with ReadinessMap's exact build-work stop message.
        var buildDir = Path.Combine(changeFolder, "build");
        Directory.CreateDirectory(buildDir);
        var buildLogPath = Path.Combine(buildDir, "app.md");
        TestGitFixture.Write(buildLogPath, """
            ```jsonc
            { "layout": 2 }
            ```

            # Build log: app

            ### T1

            ```jsonc
            { "task": "T1", "commits": ["abc1234"], "departure": null }
            ```
            """);
        TestGitFixture.Git(master, "add", "--", $"{FolderRel}/build/app.md");
        TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): log T1 {Change}", "-m", "Generated-By: hoshin");

        var (builtOut, _) = orch.HandleStart(UserStart(master, "s-reject", "reject"), null);
        Assert.Contains("has build work (a build log or a review), so it is not rejected.", ExtractContext(builtOut));

        // Remove build log and commit clean state -> human reject commits status: "rejected".
        TestGitFixture.Git(master, "rm", "-q", "--", $"{FolderRel}/build/app.md");
        TestGitFixture.Git(master, "commit", "-q", "-m", "chore(demo): clear build log", "-m", "Generated-By: hoshin");

        var (rejectOut, _) = orch.HandleStart(UserStart(master, "s-reject", "reject"), null);
        Assert.Contains("rejected", ExtractContext(rejectOut), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("rejected", FrontMatter.TryGetScalar(File.ReadAllText(intentPath), intentPath, "status"));

        // Second reject is refused as already closed.
        var (againOut, _) = orch.HandleStart(UserStart(master, "s-reject", "reject"), null);
        Assert.Contains("is closed: its intent was rejected.", ExtractContext(againOut));
    }

    [Fact]
    public void ReopenHook_ReopensAcceptedIntent_RefusesDownstreamWithStateReasons_AndCascadesRegeneration()
    {
        using var tmp = new TempDir("hoshin-a8-reopen-");
        var (master, changeFolder) = CreateWorkspace(tmp, track: "full", withSpec: true, withPlan: true, acceptAll: true);
        var orch = TestServices.CreateOrchestrator();

        // Model-sourced reopen is refused.
        var (modelOut, _) = orch.HandleStart(ModelStart(master, "s-reopen", "reopen", "intent"), null);
        Assert.Contains("Only the engineer", ExtractContext(modelOut));

        // Human reopens intent -> intent.md is committed with status: "draft".
        var (reopenOut, _) = orch.HandleStart(UserStart(master, "s-reopen", "reopen", "intent"), null);
        Assert.Contains("reopened", ExtractContext(reopenOut), StringComparison.OrdinalIgnoreCase);

        var intentPath = Path.Combine(changeFolder, "intent.md");
        Assert.Equal("draft", FrontMatter.TryGetScalar(File.ReadAllText(intentPath), intentPath, "status"));

        // Downstream plan and build refuse with exact ChangeStateService / ReadinessMap text.
        var (planOut, _) = orch.HandleStart(UserStart(master, "s-reopen", "plan"), null);
        Assert.Contains("The intent was reopened (`draft`). The spec must be regenerated and accepted again before planning.", ExtractContext(planOut));

        var (buildOut, _) = orch.HandleStart(UserStart(master, "s-reopen", "build"), null);
        Assert.Contains("`intent.md` was reopened (`draft`). Rerun the skills after it, then accept the plan again.", ExtractContext(buildOut));

        // Revise reopened intent content and commit the updated draft, then human re-accepts intent -> breaks spec's pin -> Next routes to WriteDocument(Spec).
        File.WriteAllText(intentPath, File.ReadAllText(intentPath).Replace("Greeting is spelled correctly.", "Greeting is spelled correctly and includes punctuation."));
        TestGitFixture.Git(master, "add", "--", $"{FolderRel}/intent.md");
        TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): revise intent {Change}", "-m", "Generated-By: hoshin");

        var (acceptOut, _) = orch.HandleStart(UserStart(master, "s-reopen", "accept"), null);
        Assert.Contains("intent is accepted", ExtractContext(acceptOut), StringComparison.OrdinalIgnoreCase);

        var (staleBuildOut, _) = orch.HandleStart(UserStart(master, "s-reopen", "build"), null);
        Assert.Contains("The intent changed after this spec was written. Rerun the spec skill, then the plan skill.", ExtractContext(staleBuildOut));

        var repo = TestServices.FindRepo(master, changeFolder)!;
        var state = TestServices.WorkflowStateReader.Read(repo)!;
        var next = WorkflowEngine.Next(state);
        var writeSpec = Assert.IsType<WriteDocument>(next);
        Assert.Equal(WorkflowDocument.Spec, writeSpec.Document);
    }

    [Fact]
    public void FixAcceptHook_FollowsFixStateRows_AcceptsAllThreeWithPins_AndAdvancesToBuild()
    {
        using var tmp = new TempDir("hoshin-a8-fix-");
        var (master, changeFolder) = CreateWorkspace(tmp, track: "fix", withSpec: true, withPlan: true, acceptAll: false);
        var orch = TestServices.CreateOrchestrator();

        // Light-track stops spec and plan skills with exact ReadinessMap message.
        var (specOut, _) = orch.HandleStart(UserStart(master, "s-fix", "spec"), null);
        Assert.Contains("is on the light track; `fix --accept", ExtractContext(specOut));

        // Row 3: uncommitted edit on spec.md blocks fix --accept.
        var specPath = Path.Combine(changeFolder, "spec.md");
        var originalSpec = File.ReadAllText(specPath);
        File.WriteAllText(specPath, originalSpec + "\nUnrecorded note.\n");
        var (dirtyOut, _) = orch.HandleStart(UserStart(master, "s-fix", "fix", "--accept"), null);
        Assert.DoesNotContain("accepted", ExtractContext(dirtyOut), StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(specPath, originalSpec);

        // Model-sourced fix --accept is refused.
        var (modelOut, _) = orch.HandleStart(ModelStart(master, "s-fix", "fix", "--accept"), null);
        Assert.Contains("Only the engineer", ExtractContext(modelOut));

        // Row 6 -> human runs /hoshin:fix --accept -> accepts intent, spec (pinned), plan (pinned) -> Row 8 -> ready to build!
        var (acceptOut, _) = orch.HandleStart(UserStart(master, "s-fix", "fix", "--accept"), null);
        var acceptMsg = ExtractContext(acceptOut);
        Assert.Contains("accepted", acceptMsg, StringComparison.OrdinalIgnoreCase);

        var fixState = TestServices.Default.FixState.Compute(changeFolder);
        Assert.Equal(8, fixState.Row);
        Assert.Equal("all", fixState.Facts.Accepted);

        var repo = TestServices.FindRepo(master, changeFolder)!;
        var state = TestServices.WorkflowStateReader.Read(repo)!;
        Assert.Equal("holds", state.ChangeState!.Documents.Spec.Pin);
        Assert.Equal("holds", state.ChangeState.Documents.Plan.Pin);
        Assert.Null(state.ChangeState.Ready.Build.Stop);
        Assert.IsType<Implement>(WorkflowEngine.Next(state));
    }

    [Fact]
    public void FixAcceptHook_ResumesPartialAcceptanceFromRow5ToRow8()
    {
        using var tmp = new TempDir("hoshin-a8-fix-row5-");
        var (master, changeFolder) = CreateWorkspace(tmp, track: "fix", withSpec: true, withPlan: true, acceptAll: false);
        var intentPath = Path.Combine(changeFolder, "intent.md");
        TestGitFixture.Write(intentPath, IntentText("accepted", "fix"));
        TestGitFixture.Git(master, "add", "--", $"{FolderRel}/intent.md");
        TestGitFixture.Git(master, "commit", "-q", "-m", $"docs(demo): accept intent {Change}", "-m", "Generated-By: hoshin");

        // Confirm starting at Row 5 (leading accepted, acceptance commit last).
        Assert.Equal(5, TestServices.Default.FixState.Compute(changeFolder).Row);

        var orch = TestServices.CreateOrchestrator();
        var (acceptOut, _) = orch.HandleStart(UserStart(master, "s-fix-5", "accept"), null);
        Assert.Contains("accepted", ExtractContext(acceptOut), StringComparison.OrdinalIgnoreCase);

        var after = TestServices.Default.FixState.Compute(changeFolder);
        Assert.Equal(8, after.Row);
        Assert.Equal("all", after.Facts.Accepted);

        var repo = TestServices.FindRepo(master, changeFolder)!;
        var state = TestServices.WorkflowStateReader.Read(repo)!;
        Assert.Equal("holds", state.ChangeState!.Documents.Spec.Pin);
        Assert.Equal("holds", state.ChangeState.Documents.Plan.Pin);
        Assert.IsType<Implement>(WorkflowEngine.Next(state));
    }
}
