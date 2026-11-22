using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// Phase 5 exit-gate tests: multi-turn scripted hook tests for <c>intent</c>, <c>spec</c>, <c>plan</c>, <c>review</c>,
/// and <c>fix</c> across BOTH Claude Code and Google Antigravity payloads, plus strict skill lint and 10-skill
/// dual-bundle packaging verification.
/// </summary>
public sealed class AuthoringAndReviewHookTests
{
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");
    private static readonly string PackagerScript = Path.Combine(RepoRoot, "packaging", "package.ps1");

    private static (int ExitCode, string Stdout, string Stderr) RunHook(string cwd, string verb, object payload)
    {
        var args = new List<string> { HoshinDll };
        args.AddRange(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (verb.StartsWith("hook ", StringComparison.Ordinal))
        {
            args.Add("--roles");
            args.Add(SkillsDir);
        }

        var env = new Dictionary<string, string>(TestGitFixture.GitEnv) { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };
        var res = ProcessCommandRunner.Default.Run(new CommandRequest(
            WorkingDirectory: cwd,
            Argv: ["dotnet", .. args],
            StandardInput: JsonSerializer.Serialize(payload),
            ExtraEnvironment: env));

        Assert.Equal(0, res.ExitCode);
        return (res.ExitCode ?? -1, res.Stdout, res.Stderr);
    }

    private static object StartPayload(HookHarness harness, string cwd, string sessionId, string skill, string args = "") =>
        harness == HookHarness.Antigravity
            ? new
            {
                hookEventName = "PreInvocation",
                conversationId = sessionId,
                cwd,
                workspacePaths = new[] { cwd },
                invocationNum = 0,
                lastUserInput = string.IsNullOrWhiteSpace(args) ? $"/{skill}" : $"/{skill} {args}",
            }
            : new
            {
                hook_event_name = "UserPromptExpansion",
                cwd,
                session_id = sessionId,
                command_name = $"hoshin:{skill}",
                command_args = args,
                prompt = string.IsNullOrWhiteSpace(args) ? $"/hoshin:{skill}" : $"/hoshin:{skill} {args}",
            };

    private static object StopPayload(HookHarness harness, string cwd, string sessionId) =>
        harness == HookHarness.Antigravity
            ? new { hookEventName = "Stop", conversationId = sessionId, cwd, fullyIdle = true }
            : new { hook_event_name = "Stop", cwd, session_id = sessionId, stop_hook_active = false };

    private static object GuardEditPayload(HookHarness harness, string cwd, string sessionId, string path) =>
        harness == HookHarness.Antigravity
            ? new
            {
                hookEventName = "PreToolUse",
                conversationId = sessionId,
                cwd,
                toolCall = new { id = "e1", name = "write_file", args = new { Path = path } },
            }
            : new
            {
                hook_event_name = "PreToolUse",
                cwd,
                session_id = sessionId,
                tool_name = "Write",
                tool_input = new { file_path = path },
            };

    private static object GuardShellPayload(HookHarness harness, string cwd, string sessionId, string cmd) =>
        harness == HookHarness.Antigravity
            ? new
            {
                hookEventName = "PreToolUse",
                conversationId = sessionId,
                cwd,
                toolCall = new { id = "s1", name = "run_command", args = new { CommandLine = cmd } },
            }
            : new
            {
                hook_event_name = "PreToolUse",
                cwd,
                session_id = sessionId,
                tool_name = "Bash",
                tool_input = new { command = cmd },
            };

    private static string ExtractStartText(HookHarness harness, string stdout)
    {
        var root = JsonDocument.Parse(stdout).RootElement;
        return harness == HookHarness.Antigravity
            ? root.GetProperty("injectSteps")[0].GetProperty("systemMessage").GetProperty("systemMessage").GetString()!
            : root.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    private static void AssertSilentStop(HookHarness harness, string stdout)
    {
        if (harness == HookHarness.Antigravity)
        {
            Assert.Equal("{}", stdout);
        }
        else
        {
            Assert.Equal("", stdout);
        }
    }

    private static string AssertBlockedStop(HookHarness harness, string stdout)
    {
        var root = JsonDocument.Parse(stdout).RootElement;
        var expectedDecision = harness == HookHarness.Antigravity ? "continue" : "block";
        Assert.Equal(expectedDecision, root.GetProperty("decision").GetString());
        return root.GetProperty("reason").GetString()!;
    }

    private static void AssertGuardAllowed(HookHarness harness, string stdout)
    {
        if (harness == HookHarness.Antigravity)
        {
            Assert.Equal("allow", JsonDocument.Parse(stdout).RootElement.GetProperty("decision").GetString());
        }
        else
        {
            Assert.Equal("", stdout);
        }
    }

    private static void AssertGuardDenied(HookHarness harness, string stdout)
    {
        var root = JsonDocument.Parse(stdout).RootElement;
        if (harness == HookHarness.Antigravity)
        {
            Assert.Equal("deny", root.GetProperty("decision").GetString());
        }
        else
        {
            Assert.Equal("deny", root.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());
        }
    }

    private static string CreateFreshAuthoringRepo(TempDir tmp)
    {
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/greet.mjs"] = "export function greet(name) { return `Hello, ${name}`; }\n",
            [".hoshin/change.jsonc"] = """
                {
                  "version": 1,
                  "change": "2026-11-21-authoring",
                  "branch": "feat/authoring",
                  "type": "feat",
                  "scope": "greet",
                  "base": "main"
                }
                """,
        });
        return repoDir;
    }

    // =========================================================================
    // 1. Multi-Turn Intent -> Spec -> Plan Authoring Chain (Both Harnesses)
    // =========================================================================

    [Theory]
    [InlineData(HookHarness.ClaudeCode)]
    [InlineData(HookHarness.Antigravity)]
    public void AuthoringChain_IntentInterview_SpecGherkinPin_PlanCrossCheckPin_WorksOnBothHarnesses(HookHarness harness)
    {
        using var tmp = new TempDir("hoshin-auth-");
        var repoDir = CreateFreshAuthoringRepo(tmp);
        var session = $"sess-{harness}";

        // --- INTENT: multi-turn interview ---
        var (_, intentStart, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session, "intent", "Add shouting greeting"));
        var intentBrief = ExtractStartText(harness, intentStart);
        Assert.Contains("Add shouting greeting", intentBrief);
        Assert.Contains(".hoshin/intent.md", intentBrief);

        // Two interview turns without writing intent.md -> Stop stays completely silent!
        AssertSilentStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        AssertSilentStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);

        // Guard blocks editing product code and blocks manual git commit, but allows .hoshin/intent.md.
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, "src", "greet.mjs"))).Stdout);
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardShellPayload(harness, repoDir, session, "git commit -m 'manual'")).Stdout);
        AssertGuardAllowed(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, ".hoshin", "intent.md"))).Stdout);

        // Write incomplete intent.md (missing ## Open questions) -> Stop blocks with diagnostics.
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "intent.md"), """
            ```jsonc
            {
              "title": "Shout greeting",
              "system": "greet",
              "author": "PO",
              "date": "2026-11-21",
              "status": "draft",
              "scope": "greet"
            }
            ```

            # Intent: Shout greeting

            ## Problem

            No shout mode.

            ## Proposed outcome

            Users can shout.

            ## Affected users and systems

            Greeting callers.

            ## Constraints

            Keep existing greet unchanged.

            ## Decisions

            D1: shout appends `!`.
            """);

        var intentBlockReason = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        Assert.Contains("## Open questions", intentBlockReason);

        // Fix intent.md using BARE line-1 JSONC front matter (no fences!) -> Stop auto-wraps & commits!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "intent.md"), """
            {
              "title": "Shout greeting",
              "system": "greet",
              "author": "PO",
              "date": "2026-11-21",
              "status": "draft",
              "scope": "greet"
            }

            # Intent: Shout greeting

            ## Problem

            No shout mode.

            ## Proposed outcome

            Users can shout.

            ## Affected users and systems

            Greeting callers.

            ## Constraints

            Keep existing greet unchanged.

            ## Decisions

            D1: shout appends `!`.

            ## Open questions

            None.
            """);

        var intentCommittedReason = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        Assert.Contains("intent draft is committed", intentCommittedReason);
        Assert.StartsWith("```jsonc\n{", File.ReadAllText(Path.Combine(repoDir, ".hoshin", "intent.md")));

        // Out-of-order `spec` is refused while intent awaits acceptance.
        var (_, refusedSpec, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session + "-o", "spec"));
        Assert.Contains("cannot run", ExtractStartText(harness, refusedSpec));

        // Accept intent -> commits status: "accepted".
        var (_, acceptIntent, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session + "-a1", "accept"));
        Assert.Contains("intent is accepted", ExtractStartText(harness, acceptIntent));
        var intentSha = TestGitFixture.Git(repoDir, "log", "-1", "--format=%H", "--", ".hoshin/intent.md");

        // --- SPEC: Gherkin rule failure -> fix -> auto-wrap -> pin source_intent_commit & commit ---
        var (_, specStart, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session + "-s", "spec"));
        Assert.Contains(".hoshin/spec.md", ExtractStartText(harness, specStart));

        // Guard allows only spec.md.
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session + "-s", Path.Combine(repoDir, ".hoshin", "intent.md"))).Stdout);
        AssertGuardAllowed(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session + "-s", Path.Combine(repoDir, ".hoshin", "spec.md"))).Stdout);

        // Spec missing @from: tag -> blocked at Stop!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "spec.md"), """
            ```jsonc
            { "layout": 2, "title": "Shout greeting", "system": "greet", "date": "2026-11-21", "status": "draft" }
            ```

            # Spec: Shout greeting

            ## Requirements

            ```gherkin
            Feature: Shout greeting
              @R1
              Rule: Shout appends exclamation
                @R1.1
                Scenario: Shouting Ada
                  Given the name "Ada"
                  When shouting
                  Then the result is "Hello, Ada!"
            ```
            """);

        var specBlock = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session + "-s")).Stdout);
        Assert.Contains("@from:", specBlock);

        // Fix spec.md with @from:D1 and bare JSONC front matter -> auto-wrapped, pinned, committed!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "spec.md"), """
            { "layout": 2, "title": "Shout greeting", "system": "greet", "date": "2026-11-21", "status": "draft" }

            # Spec: Shout greeting

            ## Requirements

            ```gherkin
            Feature: Shout greeting
              @R1 @from:D1
              Rule: Shout appends exclamation
                @R1.1
                Scenario: Shouting Ada
                  Given the name "Ada"
                  When shouting
                  Then the result is "Hello, Ada!"
            ```
            """);

        var specCommitted = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session + "-s")).Stdout);
        Assert.Contains("spec draft is committed", specCommitted);
        var specText = File.ReadAllText(Path.Combine(repoDir, ".hoshin", "spec.md"));
        Assert.StartsWith("```jsonc\n{", specText);
        Assert.Equal(intentSha, FrontMatter.TryGetScalar(specText, "spec.md", "source_intent_commit"));

        // Accept spec.
        var (_, acceptSpec, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session + "-a2", "accept"));
        Assert.Contains("spec is accepted", ExtractStartText(harness, acceptSpec));
        var specSha = TestGitFixture.Git(repoDir, "log", "-1", "--format=%H", "--", ".hoshin/spec.md");

        // --- PLAN: cross-check scenario coverage failure -> fix -> pin source_spec_commit & commit ---
        var (_, planStart, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session + "-p", "plan"));
        Assert.Contains(".hoshin/plan.md", ExtractStartText(harness, planStart));

        // Plan that omits covers: ["R1.1"] -> blocked by CrossChecker at Stop!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "plan.md"), """
            ```jsonc
            { "layout": 2, "system": "greet", "date": "2026-11-21", "status": "draft" }
            ```

            # Plan: Shout greeting

            ## Tasks

            ### T1: Add shout

            ```jsonc
            {
              "id": "T1",
              "repo": "repo",
              "test": ["node", "--test", "test/t1.test.mjs"]
            }
            ```

            #### Change

            Add `shout`.

            #### Tests

            Test `shout`.
            """);

        var planBlock = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session + "-p")).Stdout);
        Assert.Contains("scenario R1.1 is covered by no task", planBlock);

        // Fix plan.md covering R1.1 -> pinned with source_spec_commit & committed!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "plan.md"), """
            { "layout": 2, "system": "greet", "date": "2026-11-21", "status": "draft" }

            # Plan: Shout greeting

            ## Tasks

            ### T1: Add shout

            ```jsonc
            {
              "id": "T1",
              "repo": "repo",
              "covers": ["R1.1"],
              "test": ["node", "--test", "test/t1.test.mjs"]
            }
            ```

            #### Change

            Add `shout`.

            #### Tests

            Test `shout`.
            """);

        var planCommitted = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session + "-p")).Stdout);
        Assert.Contains("plan draft is committed", planCommitted);
        var planText = File.ReadAllText(Path.Combine(repoDir, ".hoshin", "plan.md"));
        Assert.StartsWith("```jsonc\n{", planText);
        Assert.Equal(specSha, FrontMatter.TryGetScalar(planText, "plan.md", "source_spec_commit"));

        // Accept plan -> unlocks build!
        var (_, acceptPlan, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session + "-a3", "accept"));
        var acceptPlanText = ExtractStartText(harness, acceptPlan);
        Assert.Contains("plan is accepted", acceptPlanText);
        var repo = WorkflowRepo.Find(repoDir)!;
        Assert.IsType<Implement>(WorkflowEngine.Next(WorkflowState.Read(repo)!));
    }

    // =========================================================================
    // 2. Review Hook Lifecycle (Guard, Silent Stop, Validation Block, Commit)
    // =========================================================================

    [Theory]
    [InlineData(HookHarness.ClaudeCode)]
    [InlineData(HookHarness.Antigravity)]
    public void ReviewHook_RestrictsEditsValidatesFindingsAndCommitsReview(HookHarness harness)
    {
        using var tmp = new TempDir("hoshin-rev-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/greet.mjs"] = "export function greet(name) { return `Hello, ${name}`; }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "2026-11-21-rev", "branch": "feat/rev", "type": "feat", "scope": "greet", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "greet", "status": "accepted" }
                ```

                # Plan: Rev

                ## Tasks

                ### T1: Task one

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Tests

                Test it.
                """,
        });

        var repo = WorkflowRepo.Find(repoDir)!;
        var head = repo.Head();
        repo.WithLedger(l =>
        {
            l.Verdicts.Add(new WorkflowVerdict("T1", 1, head, true, "observed", [], DateTimeOffset.UtcNow));
            l.Checks.Add(new WorkflowCheckRun(head, true, "ok", DateTimeOffset.UtcNow));
            return 0;
        });

        var session = $"rev-{harness}";
        var (_, startOut, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session, "review"));
        Assert.Contains(".hoshin/build/review.md", ExtractStartText(harness, startOut));

        // Guard allows ONLY build/review.md.
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, "src", "greet.mjs"))).Stdout);
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardShellPayload(harness, repoDir, session, "git commit -m 'manual'")).Stdout);
        AssertGuardAllowed(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, ".hoshin", "build", "review.md"))).Stdout);

        // Silent Stop before review.md is written.
        AssertSilentStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);

        // Invalid convention finding (no rule) -> blocked!
        var shortHead = WorkflowEngine.Short(head);
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "build", "review.md"), $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-21", "commits": { "repo": "{{shortHead}}" } }
            ```

            ### F1

            ```jsonc
            { "id": "F1", "severity": "low", "repo": "repo", "concern": "convention", "file": "src/greet.mjs", "line": 1 }
            ```

            Missing JSDoc.
            """);

        var block = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        Assert.Contains("convention finding with no rule", block);

        // Valid review with bare JSONC front matter -> auto-wrapped, committed, and recorded in ledger!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "build", "review.md"), $$"""
            { "layout": 2 }

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-21", "commits": { "repo": "{{shortHead}}" } }
            ```

            ### F1

            ```jsonc
            { "id": "F1", "severity": "low", "repo": "repo", "concern": "convention", "rule": "CONTRIBUTING.md:10", "file": "src/greet.mjs", "line": 1 }
            ```

            Missing doc comment (CONTRIBUTING.md:10).
            """);

        var committed = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        Assert.Contains("review is committed", committed);
        var reviewRun = Assert.Single(repo.ReadLedger().Reviews);
        Assert.Equal(1, reviewRun.ReviewNumber);
        Assert.Equal(1, reviewRun.FindingCount);
        Assert.Equal(0, reviewRun.HighCount);
    }

    // =========================================================================
    // 3. Fix Light-Track Hook Lifecycle (Both Harnesses)
    // =========================================================================

    [Theory]
    [InlineData(HookHarness.ClaudeCode)]
    [InlineData(HookHarness.Antigravity)]
    public void FixHook_EnforcesBoundaryAndCommitsThreeDocuments(HookHarness harness)
    {
        using var tmp = new TempDir("hoshin-fix-");
        var repoDir = CreateFreshAuthoringRepo(tmp);
        var session = $"fix-{harness}";

        var (_, startOut, _) = RunHook(repoDir, "hook start", StartPayload(harness, repoDir, session, "fix", "Fix empty name"));
        Assert.Contains("Light-track fix", ExtractStartText(harness, startOut));

        // Guard allows intent/spec/plan, denies product files and manual git commits.
        AssertGuardAllowed(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, ".hoshin", "intent.md"))).Stdout);
        AssertGuardAllowed(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, ".hoshin", "spec.md"))).Stdout);
        AssertGuardAllowed(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, ".hoshin", "plan.md"))).Stdout);
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardEditPayload(harness, repoDir, session, Path.Combine(repoDir, "src", "greet.mjs"))).Stdout);
        AssertGuardDenied(harness, RunHook(repoDir, "guard", GuardShellPayload(harness, repoDir, session, "git commit -m 'manual'")).Stdout);

        // Silent Stop before writing anything.
        AssertSilentStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);

        // Write valid intent + spec, but a 4-task plan -> blocked by light-track boundary!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "intent.md"), """
            { "title": "Fix empty name", "system": "greet", "author": "Eng", "date": "2026-11-21", "status": "draft", "scope": "greet" }

            # Intent: Fix empty name

            ## Problem

            Empty names render poorly.

            ## Proposed outcome

            Fallback to friend.

            ## Affected users and systems

            Greeting callers.

            ## Constraints

            One repo.

            ## Decisions

            D1: empty becomes friend.

            ## Open questions

            None.
            """);
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "spec.md"), """
            { "layout": 2, "title": "Fix empty name", "system": "greet", "date": "2026-11-21", "status": "draft" }

            # Spec: Fix empty name

            ## Requirements

            ```gherkin
            Feature: Empty name fallback
              @R1 @from:D1
              Rule: Blank name defaults to friend
                @R1.1
                Scenario: Blank string
                  Given an empty name
                  When greeting
                  Then it returns "Hello, friend"
            ```
            """);
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "plan.md"), """
            ```jsonc
            { "layout": 2, "system": "greet", "date": "2026-11-21", "status": "draft" }
            ```

            # Plan: Fix empty name

            ## Tasks

            ### T1: Step 1
            ```jsonc
            { "id": "T1", "repo": "repo", "covers": ["R1.1"], "test": ["node", "--test"] }
            ```
            #### Tests
            Test 1.

            ### T2: Step 2
            ```jsonc
            { "id": "T2", "repo": "repo", "test": ["node", "--test"] }
            ```
            #### Tests
            Test 2.

            ### T3: Step 3
            ```jsonc
            { "id": "T3", "repo": "repo", "test": ["node", "--test"] }
            ```
            #### Tests
            Test 3.

            ### T4: Step 4
            ```jsonc
            { "id": "T4", "repo": "repo", "test": ["node", "--test"] }
            ```
            #### Tests
            Test 4.
            """);

        var boundaryBlock = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        Assert.Contains("at most 3 tasks", boundaryBlock);

        // Reduce to 1 task -> clean light-track draft auto-wrapped and committed!
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "plan.md"), """
            { "layout": 2, "system": "greet", "date": "2026-11-21", "status": "draft" }

            # Plan: Fix empty name

            ## Tasks

            ### T1: Fallback empty name

            ```jsonc
            { "id": "T1", "repo": "repo", "covers": ["R1.1"], "test": ["node", "--test"] }
            ```

            #### Change

            Default empty name to `friend`.

            #### Tests

            Test `greet("") === "Hello, friend"`.
            """);

        var committed = AssertBlockedStop(harness, RunHook(repoDir, "hook stop", StopPayload(harness, repoDir, session)).Stdout);
        Assert.Contains("light-track fix draft is committed", committed);
        Assert.StartsWith("```jsonc\n{", File.ReadAllText(Path.Combine(repoDir, ".hoshin", "intent.md")));
        Assert.StartsWith("```jsonc\n{", File.ReadAllText(Path.Combine(repoDir, ".hoshin", "spec.md")));
        Assert.StartsWith("```jsonc\n{", File.ReadAllText(Path.Combine(repoDir, ".hoshin", "plan.md")));
    }

    // =========================================================================
    // 4. Phase 5 Exit Gate: Strict Skill Lint + 10-Skill Dual-Bundle Packaging
    // =========================================================================

    [Fact]
    public void AllSkills_PassStrictLint_NoYamlNoCliNoHarnessToolNames()
    {
        var expectedSkills = new[]
        {
            "intent", "spec", "plan", "build", "review", "fix", "done", "accept", "setup", "context", "implement",
        };
        foreach (var name in expectedSkills)
        {
            Assert.True(File.Exists(Path.Combine(SkillsDir, name, "procedure.md")), $"Missing skills/{name}/procedure.md");
        }

        var files = Directory.GetFiles(SkillsDir, "*.md", SearchOption.AllDirectories);
        var forbidden = new Regex(
            @"\b(hoshin\.dll|hoshin\.ts|hoshin\s+(check|read|commit|run|state|guard|hook|migrate|resolve|instructions|baseline|red|stale|change)|git\s+(add|commit|push|checkout|status|diff|log|rev-parse)|invoke_subagent|run_command|write_file|edit_file|write_to_file|replace_file_content|multi_replace_file_content|view_file|read_file|manage_task|manage_subagents|Bash|PreToolUse|PostToolUse|SubagentStop|UserPromptExpansion|PreInvocation)\b|`Agent`",
            RegexOptions.Compiled);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.False(text.TrimStart().StartsWith("---", StringComparison.Ordinal), $"{file} contains YAML front matter");
            Assert.False(forbidden.IsMatch(text), $"{file} contains forbidden token: '{forbidden.Match(text).Value}'");
        }
    }

    [Fact]
    public void Packager_EmitsOneLineSkillWrappersForAll10SkillsInBothBundles()
    {
        using var outDir = new TempDir("hoshin-pkg-");
        IReadOnlyList<string> argv = OperatingSystem.IsWindows()
            ? ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", PackagerScript, "-Configuration", "Debug", "-OutputRoot", outDir.Path]
            : ["pwsh", "-NoProfile", "-File", PackagerScript, "-Configuration", "Debug", "-OutputRoot", outDir.Path];
        var res = ProcessCommandRunner.Default.Run(new CommandRequest(
            WorkingDirectory: RepoRoot,
            Argv: argv,
            Timeout: TimeSpan.FromMinutes(3)));
        Assert.True(res.Ok, $"package.ps1 failed:\n{res.Stdout}\n{res.Stderr}");

        // Verify portable CLI + skills bundle and Hoshin.Workflow NuGet package.
        Assert.True(File.Exists(Path.Combine(outDir.Path, "portable", "lib", "hoshin.dll")));
        Assert.True(File.Exists(Path.Combine(outDir.Path, "portable", "lib", "Hoshin.Workflow.dll")));
        Assert.True(File.Exists(Path.Combine(outDir.Path, "portable", "lib", "Markdig.dll")));
        Assert.True(File.Exists(Path.Combine(outDir.Path, "portable", "lib", "Gherkin.dll")));
        Assert.True(File.Exists(Path.Combine(outDir.Path, "portable", "skills", "implement", "procedure.md")));

        var nupkgPath = Path.Combine(outDir.Path, "nuget", "Hoshin.Workflow.2.0.0.nupkg");
        Assert.True(File.Exists(nupkgPath), $"Missing NuGet package: {nupkgPath}");
        using (var zip = System.IO.Compression.ZipFile.OpenRead(nupkgPath))
        {
            var nuspecEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(nuspecEntry);
            using var sr = new StreamReader(nuspecEntry!.Open());
            var nuspec = sr.ReadToEnd();
            Assert.Contains("id=\"Markdig\"", nuspec);
            Assert.Contains("id=\"Gherkin\"", nuspec);
            Assert.DoesNotContain("Claude", nuspec, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Antigravity", nuspec, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Yaml", nuspec, StringComparison.OrdinalIgnoreCase);
        }

        // Verify Hoshin.Workflow assembly embeds all 11 role/skill procedures.
        var embeddedNames = typeof(WorkflowEngine).Assembly.GetManifestResourceNames();
        var tenSkills = new[] { "intent", "spec", "plan", "build", "review", "fix", "done", "accept", "setup", "context" };
        foreach (var role in tenSkills.Append("implement"))
        {
            Assert.Contains(embeddedNames, n => n.EndsWith($".skills.{role}.procedure.md", StringComparison.OrdinalIgnoreCase));
        }

        foreach (var bundleRoot in new[]
        {
            Path.Combine(outDir.Path, "claude-code", "skills"),
            Path.Combine(outDir.Path, "antigravity", ".agents", "skills"),
        })
        {
            foreach (var skill in tenSkills)
            {
                var skillMd = Path.Combine(bundleRoot, skill, "SKILL.md");
                var refProc = Path.Combine(bundleRoot, skill, "references", "procedure.md");
                Assert.True(File.Exists(skillMd), $"Missing {skillMd}");
                Assert.True(File.Exists(refProc), $"Missing {refProc}");

                var wrapperText = File.ReadAllText(skillMd);
                Assert.Contains("Read `references/procedure.md` and follow it.", wrapperText);

                // Verbatim copy check.
                var srcProc = File.ReadAllText(Path.Combine(SkillsDir, skill, "procedure.md"));
                Assert.Equal(srcProc, File.ReadAllText(refProc));
            }
        }
    }
}
