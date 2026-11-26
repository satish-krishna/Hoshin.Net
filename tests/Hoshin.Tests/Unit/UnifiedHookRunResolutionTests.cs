using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R02 (W11, W12, W13): Every hook verb and the guard resolve the active run and change checkout
/// consistently across master, copy worktrees, and product repositories.
/// </summary>
public sealed class UnifiedHookRunResolutionTests
{
    [Fact]
    public void HookStartAndStop_InWorkspaceCopy_CommitsIntentDraftInCopy()
    {
        using var tmp = new TempDir("hoshin-r02-intent-");
        var (masterDir, _, _) = CreateWorkspace(tmp.Path);
        var orchestrator = TestServices.HookOrchestrator;
        const string session = "po-r02";

        var (startOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = session,
                cwd = masterDir,
                command_name = "hoshin:intent",
                command_args = "add-shout",
                prompt = "/hoshin:intent add-shout",
            })),
            rolesRoot: null);

        Assert.NotNull(startOut);
        var brief = ExtractContext(startOut!);
        var copiesDir = Path.Combine(tmp.Path, ".hoshin", "copies");
        var copyRoot = Assert.Single(Directory.GetDirectories(copiesDir));
        var changeName = Path.GetFileName(copyRoot);
        Assert.Contains(copyRoot, brief, StringComparison.OrdinalIgnoreCase);

        var intentPath = Path.Combine(copyRoot, "intent", "shop", changeName, "intent.md");
        TestGitFixture.Write(
            intentPath,
            """
            ```jsonc
            { "title": "Add shout", "system": "shop", "author": "Pat", "date": "2026-11-26", "status": "draft", "scope": "shop" }
            ```

            # Intent: Add shout

            ## Problem

            No shout exists.

            ## Proposed outcome

            Shout exists.

            ## Affected users and systems

            Shop clients.

            ## Constraints

            None.

            ## Decisions

            D1: Add shout.

            ## Open questions

            None.
            """);

        var (stopOut, decision) = orchestrator.HandleStop(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                session_id = session,
                cwd = masterDir,
                stop_hook_active = false,
                background_tasks = Array.Empty<object>(),
                last_assistant_message = "Wrote the intent draft.",
            })),
            rolesRoot: null);

        Assert.NotNull(stopOut);
        Assert.Contains("committed", decision, StringComparison.OrdinalIgnoreCase);
        var status = TestGitFixture.Git(copyRoot, "status", "--porcelain", "--", $"intent/shop/{changeName}/intent.md");
        Assert.Equal("", status);
        var committed = TestGitFixture.Git(copyRoot, "log", "--format=%H", "-1", "--", $"intent/shop/{changeName}/intent.md");
        Assert.NotEmpty(committed);
    }

    [Fact]
    public void BuildInCopy_FromMaster_JudgesAndCommitsTask_AndGuardInProductRepoDeniesGitCommit()
    {
        using var tmp = new TempDir("hoshin-r02-build-");
        var (masterDir, apiDir, _) = CreateWorkspace(tmp.Path);
        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "add-shout");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        SeedAcceptedPlan(copyRoot, changeFolder, changeName);

        var orchestrator = TestServices.HookOrchestrator;
        const string session = "build-r02";

        // 1. /hoshin:build from master dispatches T1.
        var (startOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = session,
                cwd = masterDir,
                command_name = "hoshin:build",
                command_args = "",
                prompt = "/hoshin:build",
            })),
            rolesRoot: null);

        Assert.NotNull(startOut);
        var startCtx = ExtractContext(startOut!);
        Assert.Contains("T1", startCtx, StringComparison.Ordinal);

        // 2. Guard invoked from inside product repo `api` for the active session denies `git commit`.
        var (_, guardReason) = TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            session_id = session,
            cwd = apiDir,
            tool_name = "Bash",
            tool_input = new { command = "git commit -m \"manual\"" },
        })));
        Assert.NotNull(guardReason);

        // 3. Subagent completes T1 in api; `hook result` from master judges and commits without crashing.
        TestGitFixture.Write(
            Path.Combine(apiDir, "test", "calc.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { mul } from '../src/calc.mjs';
            test('mul', () => { assert.equal(mul(3, 4), 12); });
            """);
        TestGitFixture.Write(Path.Combine(apiDir, "src", "calc.mjs"), "export const add = (a, b) => a + b;\nexport const mul = (a, b) => a * b;\n");

        var masterRepo = TestServices.FindRepo(masterDir)!;
        var nonce = masterRepo.ReadStep()?.DispatchNonce;
        Assert.NotNull(nonce);

        var (resOut, resDecision) = orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = session,
                cwd = masterDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Implement T1\n{nonce}" },
                tool_response = new { status = "completed" },
            })),
            rolesRoot: null);

        Assert.NotNull(resOut);
        Assert.Contains("verdict passed", resDecision, StringComparison.Ordinal);
        var apiLog = TestGitFixture.Git(apiDir, "log", "main..HEAD", "--format=%s%n%b");
        Assert.Contains("Plan-Task: T1", apiLog, StringComparison.Ordinal);
    }

    [Fact]
    public void HookResult_WhenStateCannotBeRead_ReportsClearInternalErrorInsteadOfNullReference()
    {
        using var tmp = new TempDir("hoshin-r02-nullstate-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string> { ["README.md"] = "# plain\n" });

        var repo = TestServices.FindRepo(repoDir)!;
        const string session = "sess-nullstate";
        repo.WriteRunMarker(new RunMarker(TestServices.Time.GetUtcNow(), session, "claude-code", repo.ChangeFolder, "missing"));
        repo.WriteStep(new CurrentStep(StepKind.Implement, "T1", 1, repo.Head(), TestServices.Time.GetUtcNow(), SessionId: session, ChangeFolder: repo.ChangeFolder, DispatchNonce: "nonce-1"));

        var cmd = new Hoshin.Cli.Commands.HookCommand(
            TestServices.HookAdapter,
            TestServices.HookOrchestrator,
            TestServices.Default.HookRunResolver);

        var payload = JsonSerializer.Serialize(new
        {
            hook_event_name = "PostToolUse",
            session_id = session,
            cwd = repoDir,
            tool_name = "Agent",
            tool_input = new { prompt = "Build T1\nnonce-1" },
            tool_response = new { status = "completed" },
        });

        string? stdout = null;
        var stderr = new List<string>();
        var exitCode = cmd.Execute(["result"], () => payload, s => stdout = s, stderr.Add);

        Assert.Equal(Hoshin.Workflow.Common.ExitCodes.Pass, exitCode);
        Assert.NotNull(stdout);
        Assert.Contains("InvalidOperationException", stdout!, StringComparison.Ordinal);
        Assert.DoesNotContain("NullReferenceException", stdout!, StringComparison.Ordinal);
    }

    [Fact]
    public void HookRunResolverAndResultHandler_CoverMissingCwdNonGitWorkspaceRootUndeclaredSiblingAndNonBuildSteps()
    {
        var resolver = TestServices.Default.HookRunResolver;
        var adapter = TestServices.HookAdapter;

        // Missing cwd -> null
        var missingPath = Path.Combine(Path.GetTempPath(), $"hoshin-missing-{Guid.NewGuid():N}");
        Assert.Null(resolver.Resolve(adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "Stop", cwd = missingPath }))));

        using var tmp = new TempDir("hoshin-r02-edges-");
        var plainDir = Path.Combine(tmp.Path, "plain-nongit");
        Directory.CreateDirectory(plainDir);
        Assert.Null(resolver.Resolve(adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "Stop", cwd = plainDir }))));

        var wsDir = Path.Combine(tmp.Path, "ws");
        var (masterDir, _, _) = CreateWorkspace(wsDir);
        // Workspace root (non-git parent of master) resolves to the workspace run.
        var fromWsRoot = resolver.Resolve(adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "Stop", cwd = wsDir })));
        Assert.NotNull(fromWsRoot);
        Assert.Null(fromWsRoot!.ProductRepoName);

        // Undeclared sibling git repo falls back to standalone repo (not classified as workspace product repo).
        var extraDir = Path.Combine(wsDir, "undeclared");
        TestGitFixture.InitRepo(extraDir, new Dictionary<string, string> { ["README.md"] = "x\n" });
        var fromUndeclared = resolver.Resolve(adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "Stop", cwd = extraDir })));
        Assert.NotNull(fromUndeclared);
        Assert.Equal(Path.GetFullPath(extraDir), Path.GetFullPath(fromUndeclared!.WorkflowRepo.Root), ignoreCase: OperatingSystem.IsWindows());

        // ResultHandler with no run.json returns "no run"; with WriteDocument step returns "step is WriteDocument".
        var (noRunOut, noRunDecision) = TestServices.HookOrchestrator.HandleResult(
            adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "PostToolUse", session_id = "s1", cwd = extraDir, tool_name = "Agent" })),
            rolesRoot: null);
        Assert.Null(noRunOut);
        Assert.Equal("no run", noRunDecision);

        var extraRepo = fromUndeclared.WorkflowRepo;
        extraRepo.WriteRunMarker(new RunMarker(TestServices.Time.GetUtcNow(), "s1", "claude-code", extraRepo.ChangeFolder));
        extraRepo.WriteStep(new CurrentStep(StepKind.WriteDocument, "intent", 0, null, TestServices.Time.GetUtcNow(), SessionId: "s1", ChangeFolder: extraRepo.ChangeFolder));
        var (wrongStepOut, wrongStepDecision) = TestServices.HookOrchestrator.HandleResult(
            adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "PostToolUse", session_id = "s1", cwd = extraDir, tool_name = "Agent" })),
            rolesRoot: null);
        Assert.Null(wrongStepOut);
        Assert.Equal("step is WriteDocument", wrongStepDecision);

        // Invalid .hoshin.json in workspace falls back cleanly without crashing.
        File.WriteAllText(Path.Combine(masterDir, ".hoshin.json"), "{ invalid json");
        var apiDir = Path.Combine(wsDir, "api");
        var fromCorrupt = resolver.Resolve(adapter.Parse(JsonSerializer.Serialize(new { hook_event_name = "Stop", cwd = apiDir })));
        Assert.NotNull(fromCorrupt);
    }

    private static (string Master, string Api, string Web) CreateWorkspace(string root)
    {
        var masterDir = Path.Combine(root, "master");
        var apiDir = Path.Combine(root, "api");
        var webDir = Path.Combine(root, "web");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": {
                    "shop": {
                      "repos": ["api", "web"]
                    }
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(apiDir, new Dictionary<string, string> { ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n" });
        TestGitFixture.InitRepo(webDir, new Dictionary<string, string> { ["src/greet.mjs"] = "export const greet = (n) => `Hello, ${n}`;\n" });

        return (masterDir, apiDir, webDir);
    }

    private static void SeedAcceptedPlan(string copyRoot, string changeFolder, string changeName)
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
                Scenario: Three times four
                  Given 3 and 4
                  When multiplied
                  Then 12
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

            Add `mul` to `src/calc.mjs`.

            #### Tests

            Test `mul` in `test/calc.test.mjs`.
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
