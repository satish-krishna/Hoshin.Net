using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R03 (W4, W5, W13): One WorkspaceLocator without hardcoded "master", ledger coordinates derived from
/// intent/&lt;system&gt;/&lt;change&gt; (never scope), and product-repository sessions build only their own tasks.
/// </summary>
public sealed class WorkspaceLocatorAndRepoSessionTests
{
    [Fact]
    public void BuildFromProductRepo_WithMasterNamedAiSdlc_DispatchesOnlyOwnRepoAndEndsQuietlyWithNextRepoHint()
    {
        using var tmp = new TempDir("hoshin-r03-repo-");
        var (masterDir, apiDir, webDir) = CreateAiSdlcWorkspace(tmp.Path);
        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "add-shout");
        Assert.True(started.Ok, started.Error);
        SeedTwoRepoAcceptedPlan(started.CopyRoot!, started.ChangeFolder!, started.ChangeName!);

        var orchestrator = TestServices.HookOrchestrator;

        // 1. Start build from `web` (even though T1 in `api` comes first in plan.md) -> must dispatch ONLY T2 for `web`.
        const string webSession = "web-r03";
        var (webStartOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = webSession,
                cwd = webDir,
                command_name = "hoshin:build",
                command_args = "",
                prompt = "/hoshin:build",
            })),
            rolesRoot: null);

        Assert.NotNull(webStartOut);
        var webStartCtx = ExtractContext(webStartOut!);
        Assert.Contains("# Task T2:", webStartCtx, StringComparison.Ordinal);
        Assert.Contains(webDir, webStartCtx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("# Task T1:", webStartCtx, StringComparison.Ordinal);

        // 2. Implement T2 in `web` and submit result from `web`.
        var repo = TestServices.Default.HookRunResolver.Resolve(TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            session_id = webSession,
            cwd = webDir,
        })))!.WorkflowRepo;
        var nonceT2 = repo.ReadStep()?.DispatchNonce;
        Assert.NotNull(nonceT2);

        TestGitFixture.Write(
            Path.Combine(webDir, "test", "shout.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout', () => { assert.equal(shout('Ada'), 'Hello, Ada!'); });
            """);
        TestGitFixture.Write(Path.Combine(webDir, "src", "greet.mjs"), "export const greet = (n) => `Hello, ${n}`;\nexport const shout = (n) => `${greet(n)}!`;\n");

        var (webResOut, webResDecision) = orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = webSession,
                cwd = webDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Build T2\n{nonceT2}" },
                tool_response = new { status = "completed" },
            })),
            rolesRoot: null);

        Assert.NotNull(webResOut);
        Assert.Contains("verdict passed", webResDecision, StringComparison.Ordinal);
        var webResCtx = ExtractContext(webResOut!);
        Assert.DoesNotContain(HookInstructions.BeginMarker, webResCtx, StringComparison.Ordinal);
        Assert.Contains("api", webResCtx, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("build", webResCtx, StringComparison.OrdinalIgnoreCase);

        // 3. Stop in `web` is silent.
        var (webStopOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                session_id = webSession,
                cwd = webDir,
                stop_hook_active = false,
                background_tasks = Array.Empty<object>(),
            })),
            rolesRoot: null);
        Assert.Null(webStopOut);

        // 4. Now build from `api` -> dispatches T1, completes, and names `review` as the next step.
        const string apiSession = "api-r03";
        var (apiStartOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = apiSession,
                cwd = apiDir,
                command_name = "hoshin:build",
                command_args = "",
                prompt = "/hoshin:build",
            })),
            rolesRoot: null);
        Assert.NotNull(apiStartOut);
        var apiStartCtx = ExtractContext(apiStartOut!);
        Assert.Contains("# Task T1:", apiStartCtx, StringComparison.Ordinal);
        Assert.Contains(apiDir, apiStartCtx, StringComparison.OrdinalIgnoreCase);

        var nonceT1 = repo.ReadStep()?.DispatchNonce;
        Assert.NotNull(nonceT1);

        TestGitFixture.Write(
            Path.Combine(apiDir, "test", "calc.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { mul } from '../src/calc.mjs';
            test('mul', () => { assert.equal(mul(3, 4), 12); });
            """);
        TestGitFixture.Write(Path.Combine(apiDir, "src", "calc.mjs"), "export const add = (a, b) => a + b;\nexport const mul = (a, b) => a * b;\n");

        var (apiResOut, _) = orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = apiSession,
                cwd = apiDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Build T1\n{nonceT1}" },
                tool_response = new { status = "completed" },
            })),
            rolesRoot: null);
        Assert.NotNull(apiResOut);
        var apiResCtx = ExtractContext(apiResOut!);
        Assert.DoesNotContain(HookInstructions.BeginMarker, apiResCtx, StringComparison.Ordinal);
        Assert.Contains("review", apiResCtx, StringComparison.OrdinalIgnoreCase);

        var (apiStopOut, _) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                session_id = apiSession,
                cwd = apiDir,
                stop_hook_active = false,
                background_tasks = Array.Empty<object>(),
            })),
            rolesRoot: null);
        Assert.Null(apiStopOut);
    }

    [Fact]
    public void HookStartedChange_LedgerCoordinatesComeFromIntentPath_NeverFromScope()
    {
        using var tmp = new TempDir("hoshin-r03-coords-");
        var (masterDir, apiDir, _) = CreateAiSdlcWorkspace(tmp.Path, commitScope: "shop-core");
        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "add-shout");
        Assert.True(started.Ok, started.Error);

        // Even if called with a root that does not directly contain the copy (e.g., master main checkout),
        // ChangeLedgerPaths must extract system="shop" from the intent/<system>/<change> path, never scope="shop-core".
        var coordsFromMain = ChangeLedgerPaths.ResolveCoordinates(masterDir, started.ChangeFolder!, TestServices.FileSystem);
        var coordsFromCopy = ChangeLedgerPaths.ResolveCoordinates(started.CopyRoot!, started.ChangeFolder!, TestServices.FileSystem);
        var systemAFolder = TestServices.Ledger.LedgerFolder(masterDir, "shop", started.ChangeName!);

        Assert.Equal("shop", coordsFromMain.System);
        Assert.Equal("shop", coordsFromCopy.System);
        Assert.Equal(started.ChangeName, coordsFromMain.Change);
        Assert.Equal(coordsFromMain.System, coordsFromCopy.System);
        Assert.Equal(coordsFromMain.Change, coordsFromCopy.Change);

        var repo = TestServices.Default.HookRunResolver.ResolveAt(started.CopyRoot!, started.ChangeFolder!)!;
        Assert.Equal(Path.GetFullPath(systemAFolder), Path.GetFullPath(repo.LedgerFolder));

        // Seed intent.md and confirm ChangeState from inside `api` finds the same system and change.
        TestGitFixture.Write(
            Path.Combine(started.ChangeFolder!, "intent.md"),
            """
            ```jsonc
            { "title": "Add shout", "system": "shop", "author": "Pat", "date": "2026-11-26", "status": "draft", "scope": "shop-core" }
            ```

            # Intent: Add shout
            """);
        var state = TestServices.Default.ChangeState.Compute(apiDir, new StateOptions());
        Assert.Equal("shop", state.System);
        Assert.Contains(state.Changes, c => c.Change == started.ChangeName);
    }

    [Fact]
    public void ProductRepoSession_WaitingOnCrossRepoDependency_ReturnsHandoffAndWorkspaceLocatorHandlesNonWorkspace()
    {
        var doc = new WorkflowDocState("plan.md", true, true, false, "accepted", "accepted");
        var plan = new WorkflowPlan(
            "c1",
            "shop",
            "main",
            [
                new WorkflowTask("T1", "Api task", "Do T1", ["node", "--test"], Repo: "api"),
                new WorkflowTask("T2", "Web task", "Do T2", ["node", "--test"], Repo: "web", DependsOn: ["T1"]),
            ],
            []);
        var state = new WorkflowState(
            "repo",
            "change",
            plan,
            "head1",
            Hoshin.Workflow.Events.LedgerStore.Empty(),
            null,
            doc,
            doc,
            doc,
            SessionRepo: "web");

        var step = WorkflowEngine.Next(state);
        var done = Assert.IsType<Done>(step);
        Assert.True(done.Success);
        Assert.Contains("waiting on", done.Outcome, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T1 in `api`", done.Outcome, StringComparison.Ordinal);

        using var tmp = new TempDir("hoshin-r03-nonws-");
        Assert.False(TestServices.Default.WorkspaceLocator.HasWorkspaceMarker(tmp.Path));
        Assert.Null(TestServices.Default.WorkspaceLocator.TryResolve(tmp.Path));
    }

    private static (string Master, string Api, string Web) CreateAiSdlcWorkspace(string root, string? commitScope = null)
    {
        var masterDir = Path.Combine(root, "ai-sdlc");
        var apiDir = Path.Combine(root, "api");
        var webDir = Path.Combine(root, "web");

        var systemJson = commitScope is null
            ? """{ "repos": ["api", "web"] }"""
            : $$"""{ "repos": ["api", "web"], "commitScope": "{{commitScope}}" }""";

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = $$"""
                {
                  "version": 1,
                  "systems": {
                    "shop": {{systemJson}}
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(apiDir, new Dictionary<string, string> { ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n" });
        TestGitFixture.InitRepo(webDir, new Dictionary<string, string> { ["src/greet.mjs"] = "export const greet = (n) => `Hello, ${n}`;\n" });
        return (masterDir, apiDir, webDir);
    }

    private static void SeedTwoRepoAcceptedPlan(string copyRoot, string changeFolder, string changeName)
    {
        TestGitFixture.Write(
            Path.Combine(changeFolder, "intent.md"),
            """
            ```jsonc
            { "title": "Add shout", "system": "shop", "author": "Pat", "date": "2026-11-26", "status": "accepted", "scope": "shop" }
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
    }

    private static string ExtractContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }
}
