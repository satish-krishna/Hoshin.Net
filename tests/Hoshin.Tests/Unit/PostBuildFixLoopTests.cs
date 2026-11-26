using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R06 (W3): Post-build fix loops for failing <c>build.finish</c> checks (<c>onFailure: "fix"</c> bounded by <c>gateRetries</c>)
/// and high-severity review findings (<c>/hoshin:build &lt;instructions&gt;</c> from the target repository), with a composite
/// code head across all touched repositories.
/// </summary>
public sealed class PostBuildFixLoopTests
{
    private static readonly Regex RepositoryLine = new(@"^Repository: (?<path>.+?)\s*$", RegexOptions.Multiline);

    [Fact]
    public void ReviewWithHighFinding_BuildWithInstructionsFromProductRepo_CommitsFixRunsChecksAdvancesToReview2AndNeedsApproval()
    {
        using var tmp = new TempDir("hoshin-r06-review-");
        var wsRoot = Path.Combine(tmp.Path, "ws");
        var masterDir = Path.Combine(wsRoot, "ai-sdlc");
        var webDir = Path.Combine(wsRoot, "web");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": { "shop": { "repos": ["web"] } },
                  "hooks": {
                    "build.finish": [
                      { "id": "unit", "run": ["node", "--test"] }
                    ]
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(webDir, new Dictionary<string, string>
        {
            ["package.json"] = """{ "name": "web", "type": "module", "private": true }""",
            ["src/greet.mjs"] = "export const greet = (name) => `Hello, ${name}`;\n",
            ["test/greet.test.mjs"] = """
                import test from 'node:test';
                import assert from 'node:assert/strict';
                import { greet } from '../src/greet.mjs';
                test('greet', () => { assert.equal(greet('Ada'), 'Hello, Ada'); });
                """,
        });

        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "fix-review");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        SeedAcceptedPlan(
            copyRoot,
            changeFolder,
            changeName,
            "web",
            """{ "id": "T1", "repo": "web", "depends_on": [], "covers": ["R1.1"], "test": ["node", "--test", "test/shout.test.mjs"] }""");

        // Complete T1 in `web`.
        var startT1 = RunStart("web-s1", webDir, "build", "");
        Assert.Contains("# Task T1:", startT1, StringComparison.Ordinal);
        var t1Prompt = ExtractSubagentPrompt(startT1);
        TestGitFixture.Write(Path.Combine(webDir, "test", "shout.test.mjs"), """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout', () => { assert.equal(shout('Ada'), 'Hello, Ada!'); });
            """);
        TestGitFixture.Write(Path.Combine(webDir, "src", "greet.mjs"), "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name)}!`;\n");
        var t1Result = RunResult("web-s1", webDir, t1Prompt);
        Assert.Contains("T1 attempt 1 is accepted", t1Result, StringComparison.Ordinal);
        RunStop("web-s1", webDir);

        // Review 1 in master records one high finding F1 in `web`.
        var review1Start = RunStart("po-s1", masterDir, "review", "");
        Assert.Contains("(Review 1)", review1Start, StringComparison.Ordinal);
        var webHeadBeforeFix = TestGitFixture.Git(webDir, "rev-parse", "HEAD");
        var reviewPath = Path.Combine(changeFolder, "build", "review.md");
        TestGitFixture.Write(reviewPath, $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-26", "commits": { "web": "{{webHeadBeforeFix[..7]}}" } }
            ```

            ### F1

            ```jsonc
            { "id": "F1", "severity": "high", "repo": "web", "concern": "bug", "file": "src/greet.mjs", "line": 2 }
            ```

            `shout('  Ada ')` does not trim whitespace.
            """);
        var (review1StopOut, _) = TestServices.HookOrchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("po-s1", masterDir)),
            rolesRoot: null);
        Assert.Contains("review 1 has 1 high findings", review1StopOut ?? "", StringComparison.Ordinal);
        RunStop("po-s1", masterDir);

        // Engineer runs `/hoshin:build fix F1 from review 1: shout must trim the name` inside `web`.
        var fixStart = RunStart("web-s2", webDir, "build", "fix F1 from review 1: shout must trim the name");
        Assert.Contains(HookInstructions.BeginMarker, fixStart, StringComparison.Ordinal);
        var fixPrompt = ExtractSubagentPrompt(fixStart);
        Assert.Contains("F1", fixPrompt, StringComparison.Ordinal);
        Assert.Contains("shout must trim the name", fixPrompt, StringComparison.Ordinal);
        var repoMatch = RepositoryLine.Match(fixPrompt);
        Assert.True(repoMatch.Success, $"Fix prompt must name target repository:\n{fixPrompt}");
        Assert.Equal(Path.GetFullPath(webDir), Path.GetFullPath(repoMatch.Groups["path"].Value.Trim()), ignoreCase: OperatingSystem.IsWindows());

        // Subagent writes regression test + fix in `web` and returns.
        TestGitFixture.Write(Path.Combine(webDir, "test", "shout-trim.test.mjs"), """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout trims', () => { assert.equal(shout('  Ada '), 'Hello, Ada!'); });
            """);
        TestGitFixture.Write(Path.Combine(webDir, "src", "greet.mjs"), "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name.trim())}!`;\n");

        var fixResult = RunResult("web-s2", webDir, fixPrompt);
        Assert.DoesNotContain("is not accepted", fixResult, StringComparison.Ordinal);
        var webHeadAfterFix = TestGitFixture.Git(webDir, "rev-parse", "HEAD");
        Assert.NotEqual(webHeadBeforeFix, webHeadAfterFix);
        var changedFiles = TestGitFixture.Git(webDir, "log", "--name-only", "--format=", $"{webHeadBeforeFix}..HEAD");
        Assert.Contains("shout-trim.test.mjs", changedFiles, StringComparison.Ordinal);
        RunStop("web-s2", webDir);

        // Review 2 in master is now requested (composite code head moved!) and clean Review 2 leads to NeedsApproval.
        var review2Start = RunStart("po-s2", masterDir, "review", "");
        Assert.Contains("(Review 2)", review2Start, StringComparison.Ordinal);
        var existingReview = File.ReadAllText(reviewPath).TrimEnd();
        TestGitFixture.Write(reviewPath, existingReview + "\n\n" + $$"""
            ## Review 2

            ```jsonc
            { "review": 2, "reviewed_at": "2026-11-26", "commits": { "web": "{{webHeadAfterFix[..7]}}" } }
            ```

            No findings.
            """ + "\n");
        var (review2StopOut, _) = TestServices.HookOrchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload("po-s2", masterDir)),
            rolesRoot: null);
        Assert.Contains("/hoshin:accept", review2StopOut ?? "", StringComparison.Ordinal);

        var masterRepo = TestServices.Default.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        var finalState = TestServices.ReadState(masterRepo)!;
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(finalState));
    }

    [Fact]
    public void FailingBuildFinishCheckWithOnFailureFix_DispatchesFixBoundedByGateRetries_AndPassesOnRetry()
    {
        using var tmp = new TempDir("hoshin-r06-check-");
        var wsRoot = Path.Combine(tmp.Path, "ws");
        var masterDir = Path.Combine(wsRoot, "ai-sdlc");
        var appDir = Path.Combine(wsRoot, "app");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "gateRetries": 2,
                  "systems": { "shop": { "repos": ["app"] } },
                  "hooks": {
                    "build.finish": [
                      { "id": "no-todo", "onFailure": "fix", "run": ["node", "check-todo.mjs"] }
                    ]
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(appDir, new Dictionary<string, string>
        {
            ["package.json"] = """{ "name": "app", "type": "module", "private": true }""",
            ["check-todo.mjs"] = """
                import { readFileSync } from 'node:fs';
                const text = readFileSync('src/app.mjs', 'utf8');
                if (text.includes('TODO')) { console.error('Found forbidden TODO in src/app.mjs'); process.exit(1); }
                """,
            ["src/app.mjs"] = "export const add = (a, b) => a + b;\n",
        });

        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "fix-check");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        SeedAcceptedPlan(
            copyRoot,
            changeFolder,
            changeName,
            "app",
            """{ "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1.1"], "test": ["node", "--test", "test/sub.test.mjs"] }""");

        // Build T1 with a TODO comment that passes T1's unit test but fails the `no-todo` build.finish check.
        var startT1 = RunStart("app-s1", appDir, "build", "");
        var t1Prompt = ExtractSubagentPrompt(startT1);
        TestGitFixture.Write(Path.Combine(appDir, "test", "sub.test.mjs"), """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { sub } from '../src/app.mjs';
            test('sub', () => { assert.equal(sub(5, 2), 3); });
            """);
        TestGitFixture.Write(Path.Combine(appDir, "src", "app.mjs"), "// TODO: remove me\nexport const add = (a, b) => a + b;\nexport const sub = (a, b) => a - b;\n");

        // T1 succeeds -> build.finish fails `no-todo` (onFailure: fix) -> dispatches fix attempt 1 of 2!
        var t1Result = RunResult("app-s1", appDir, t1Prompt);
        Assert.Contains("T1 attempt 1 is accepted", t1Result, StringComparison.Ordinal);
        Assert.Contains(HookInstructions.BeginMarker, t1Result, StringComparison.Ordinal);
        var fix1Prompt = ExtractSubagentPrompt(t1Result);
        Assert.Contains("no-todo", fix1Prompt, StringComparison.Ordinal);

        // Fix attempt 1 still leaves a TODO -> check fails again -> dispatches fix attempt 2 of 2!
        TestGitFixture.Write(Path.Combine(appDir, "src", "app.mjs"), "// TODO: still here\nexport const add = (a, b) => a + b;\nexport const sub = (a, b) => a - b;\n");
        var fix1Result = RunResult("app-s1", appDir, fix1Prompt);
        Assert.Contains(HookInstructions.BeginMarker, fix1Result, StringComparison.Ordinal);
        var fix2Prompt = ExtractSubagentPrompt(fix1Result);
        Assert.Contains("no-todo", fix2Prompt, StringComparison.Ordinal);

        // Fix attempt 2 removes TODO -> check re-runs and PASSES -> session finishes with review handoff!
        TestGitFixture.Write(Path.Combine(appDir, "src", "app.mjs"), "export const add = (a, b) => a + b;\nexport const sub = (a, b) => a - b;\n");
        var fix2Result = RunResult("app-s1", appDir, fix2Prompt);
        Assert.DoesNotContain(HookInstructions.BeginMarker, fix2Result, StringComparison.Ordinal);
        Assert.Contains("review", fix2Result, StringComparison.OrdinalIgnoreCase);

        var masterRepo = TestServices.Default.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        var state = TestServices.ReadState(masterRepo)!;
        Assert.IsType<ReviewDocument>(WorkflowEngine.Next(state));
    }

    [Fact]
    public void FailingCheck_WhenGateRetriesExhausted_EndsInDoneFailure()
    {
        var doc = new WorkflowDocState("plan.md", true, true, false, "accepted", "accepted");
        var now = DateTimeOffset.UtcNow;
        var ledger = LedgerStore.Empty() with
        {
            Checks =
            [
                new WorkflowCheckRun("h1", false, "err1", now, "no-todo", "fix", "app", GateRetries: 2),
                new WorkflowCheckRun("h2", false, "err2", now, "no-todo", "fix", "app", GateRetries: 2),
                new WorkflowCheckRun("h3", false, "err3", now, "no-todo", "fix", "app", GateRetries: 2),
            ],
        };
        var state = new WorkflowState(
            "repo",
            "change",
            new WorkflowPlan("c1", "shop", "main", [], []),
            "h3",
            ledger,
            null,
            doc,
            doc,
            doc,
            CodeHead: "h3");

        var step = WorkflowEngine.Next(state);
        var done = Assert.IsType<Done>(step);
        Assert.False(done.Success);
        Assert.Contains("no-todo", done.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void PostBuildFixEngineAndBriefs_CoverHandoffRetryCapConsecutiveResetAndRepoResolutionBranches()
    {
        var doc = new WorkflowDocState("plan.md", true, true, false, "accepted", "accepted");
        var now = DateTimeOffset.UtcNow;
        var plan = new WorkflowPlan(
            "c1",
            "shop",
            "main",
            [new WorkflowTask("T1", "Api task", "Do T1", ["node", "--test"], Repo: "api")],
            [],
            MaxAttempts: 2);

        // 1. Check failure in `api` while session is in `web` hands off cleanly to `api`.
        var checkLedger = LedgerStore.Empty() with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "h1", true, "observed", [], now)],
            Checks =
            [
                new WorkflowCheckRun("h0", true, "ok", now),
                new WorkflowCheckRun("h1", false, "lint err", now, "lint", "fix", "api", GateRetries: 2),
            ],
        };
        var handoffState = new WorkflowState("master", "change", plan, "h1", checkLedger, null, doc, doc, doc, CodeHead: "h1", SessionRepo: "web");
        var handoff = Assert.IsType<Done>(WorkflowEngine.Next(handoffState));
        Assert.True(handoff.Success);
        Assert.Contains("run `build` in `api`", handoff.Outcome, StringComparison.Ordinal);

        // 2. Check fix retry (attempt 2) includes handoff reasons, and exhausting MaxAttempts returns Done(false).
        var retryCheckLedger = checkLedger with
        {
            Verdicts =
            [
                new WorkflowVerdict("T1", 1, "h1", true, "observed", [], now),
                new WorkflowVerdict("FIX-CHECK-1", 1, "h1", false, "skipped", ["no changes"], now),
            ],
        };
        var retryStep = Assert.IsType<Retry>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h1", retryCheckLedger, null, doc, doc, doc, CodeHead: "h1")));
        Assert.Equal("FIX-CHECK-1", retryStep.Agent.Task);
        Assert.Contains("the previous fix attempt was not accepted", retryStep.Agent.Brief, StringComparison.Ordinal);

        var exhaustedCheckLedger = retryCheckLedger with
        {
            Verdicts =
            [
                .. retryCheckLedger.WorkflowVerdicts,
                new WorkflowVerdict("FIX-CHECK-1", 2, "h1", false, "skipped", ["still empty"], now),
            ],
        };
        var exhaustedCheck = Assert.IsType<Done>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h1", exhaustedCheckLedger, null, doc, doc, doc, CodeHead: "h1")));
        Assert.False(exhaustedCheck.Success);
        Assert.Contains("FIX-CHECK-1 failed 2 attempts", exhaustedCheck.Outcome, StringComparison.Ordinal);

        // 3. Consecutive check failure counter stops at non-fix or different hookId.
        var mixedChecks = LedgerStore.Empty() with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "h3", true, "observed", [], now)],
            Checks =
            [
                new WorkflowCheckRun("h1", false, "stop err", now, "lint", "stop", "api", GateRetries: 2),
                new WorkflowCheckRun("h2", false, "other err", now, "other", "fix", "api", GateRetries: 2),
                new WorkflowCheckRun("h3", false, "lint err", now, "lint", "fix", "api", GateRetries: 2),
            ],
        };
        var firstRetry = Assert.IsType<Implement>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h3", mixedChecks, null, doc, doc, doc, CodeHead: "h3")));
        Assert.Equal("FIX-CHECK-1", firstRetry.Agent.Task);

        // 4. Review fix with SessionRepo == null resolves single finding repo, falls back on multi-repo findings, handles retry & cap.
        var reviewLedger = LedgerStore.Empty() with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "h1", true, "observed", [], now)],
            Checks = [new WorkflowCheckRun("h1", true, "ok", now)],
            Reviews = [new WorkflowReviewRun("h1", 1, 2, 2, now)],
        };
        var singleRepoFindings = new List<ReviewFindingSummary> { new("F1", "web", "src/a.mjs", 1, "bug") };
        var singleRepoFix = Assert.IsType<Implement>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h1", reviewLedger, null, doc, doc, doc, CodeHead: "h1", BuildInstructions: "fix F1", HighReviewFindings: singleRepoFindings)));
        Assert.Equal("web", singleRepoFix.Agent.TargetRepo);

        var multiRepoFindings = new List<ReviewFindingSummary>
        {
            new("F1", "api", "src/a.mjs", 1, "bug1"),
            new("F2", "web", "src/b.mjs", 2, "bug2"),
        };
        var fallbackFix = Assert.IsType<Implement>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h1", reviewLedger, null, doc, doc, doc, CodeHead: "h1", BuildInstructions: "fix all", HighReviewFindings: multiRepoFindings)));
        Assert.Equal("api", fallbackFix.Agent.TargetRepo);

        var noFindingsFix = WorkflowBriefs.FixReviewFindings(
            new WorkflowState("master", "change", plan, "h1", reviewLedger, null, doc, doc, doc, CodeHead: "h1", HighReviewFindings: []),
            reviewLedger.WorkflowReviews[0],
            "FIX-R1",
            1,
            targetRepo: null,
            "fix it",
            []);
        Assert.Equal("FIX-R1", noFindingsFix.Task);

        var nullTargetWithFindings = WorkflowBriefs.FixReviewFindings(
            new WorkflowState("master", "change", plan, "h1", reviewLedger, null, doc, doc, doc, CodeHead: "h1", HighReviewFindings: singleRepoFindings),
            reviewLedger.WorkflowReviews[0],
            "FIX-R1",
            1,
            targetRepo: null,
            "fix it",
            []);
        Assert.Contains("F1", nullTargetWithFindings.Brief, StringComparison.Ordinal);

        var retryReviewLedger = reviewLedger with
        {
            Verdicts =
            [
                .. reviewLedger.WorkflowVerdicts,
                new WorkflowVerdict("FIX-R1", 1, "h1", false, "skipped", ["no diff"], now),
            ],
        };
        Assert.IsType<Retry>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h1", retryReviewLedger, null, doc, doc, doc, CodeHead: "h1", BuildInstructions: "fix F1")));

        var capReviewLedger = retryReviewLedger with
        {
            Verdicts =
            [
                .. retryReviewLedger.WorkflowVerdicts,
                new WorkflowVerdict("FIX-R1", 2, "h1", false, "skipped", ["still no diff"], now),
            ],
        };
        var capReviewDone = Assert.IsType<Done>(WorkflowEngine.Next(new WorkflowState("master", "change", plan, "h1", capReviewLedger, null, doc, doc, doc, CodeHead: "h1", BuildInstructions: "fix F1")));
        Assert.False(capReviewDone.Success);

        // 5. Small helpers: WorkflowCodeHead.Short("") and GetGateRetries on non-workspace bridge.
        Assert.Equal("", WorkflowCodeHead.Short(""));
        Assert.Equal(3, new NullWorkflowEventBridge().GetGateRetries(null!));
    }

    [Fact]
    public void PostBuildFixEvaluation_HandlesEmptyChangesCachedVerdictAndCommitFailures()
    {
        using var tmp = new TempDir("hoshin-r06-eval-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/app.mjs"] = "export const a = 1;\n",
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "r06-eval", "type": "feat", "scope": "shop", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "shop", "status": "accepted" }
                ```

                # Plan: Eval

                ## Tasks

                ### T1: First

                ```jsonc
                { "id": "T1", "repo": "repo", "test": null }
                ```

                #### Change
                Change.

                #### Tests
                Tests.
                """,
        });

        // Corrupt build/review.md is tolerated by WorkflowStateReader (returns null findings).
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "build", "review.md"), "not valid review");
        var repo = TestServices.FindRepo(repoDir)!;
        Assert.Equal(0, TestServices.Default.WorkflowEventBridge.GetGateRetries(repo));
        var state = TestServices.ReadState(repo)!;
        Assert.Null(state.HighReviewFindings);

        var fixStep = new Implement(new AgentTask("implement", "FIX-R1", 1, "instr", "brief"));
        TestServices.Default.TaskEvaluator.Issue(repo, state, fixStep, "s1");

        // 1. No working tree changes -> early failed verdict; calling same attempt again returns cached verdict.
        var emptyVerdict = TestServices.Default.TaskEvaluator.EvaluateAndCommitTask(repo, state, "FIX-R1", 1);
        Assert.False(emptyVerdict.Passed);
        var cachedVerdict = TestServices.Default.TaskEvaluator.EvaluateAndCommitTask(repo, state, "FIX-R1", 1);
        Assert.Equal(emptyVerdict.At, cachedVerdict.At);

        // 2. Failing test commit and failing fix commit reset mixed and return failed verdicts.
        TestGitFixture.Write(Path.Combine(repoDir, "test", "a.test.mjs"), "test();\n");
        TestGitFixture.Write(Path.Combine(repoDir, "src", "app.mjs"), "export const a = 2;\n");

        var failFirst = new FailingCommitter(failOnCall: 1);
        var evalFailTest = new TaskEvaluator(
            TestServices.Git,
            TestServices.Git,
            TestServices.Git,
            failFirst,
            TestServices.AllowListedRunner,
            TestServices.Default.BuildLogWriter,
            new NullWorkflowEventBridge(),
            new IdentityTaskRepoResolver(),
            TestServices.Time);
        evalFailTest.Issue(repo, state, new Implement(new AgentTask("implement", "FIX-R1", 2, "instr", "brief")), "s1");
        var testFailVerdict = evalFailTest.EvaluateAndCommitTask(repo, state, "FIX-R1", 2);
        Assert.False(testFailVerdict.Passed);
        Assert.Contains("test commit failed", testFailVerdict.Reasons[0], StringComparison.Ordinal);
        Assert.Equal(1, failFirst.ResetCalls);

        var failSecond = new FailingCommitter(failOnCall: 2);
        var evalFailProd = new TaskEvaluator(
            TestServices.Git,
            TestServices.Git,
            TestServices.Git,
            failSecond,
            TestServices.AllowListedRunner,
            TestServices.Default.BuildLogWriter,
            new NullWorkflowEventBridge(),
            new IdentityTaskRepoResolver(),
            TestServices.Time);
        evalFailProd.Issue(repo, state, new Implement(new AgentTask("implement", "FIX-R1", 3, "instr", "brief")), "s1");
        var prodFailVerdict = evalFailProd.EvaluateAndCommitTask(repo, state, "FIX-R1", 3);
        Assert.False(prodFailVerdict.Passed);
        Assert.Contains("fix commit failed", prodFailVerdict.Reasons[0], StringComparison.Ordinal);
        Assert.Equal(1, failSecond.ResetCalls);
    }

    private sealed class FailingCommitter(int failOnCall) : Hoshin.Workflow.Git.IGitCommitter
    {
        private int _calls;
        public int ResetCalls { get; private set; }

        public Hoshin.Workflow.Git.GitCommitOutcome CommitPaths(string dir, IReadOnlyList<string> paths, IReadOnlyList<string> paragraphs)
        {
            _calls++;
            return _calls == failOnCall
                ? new Hoshin.Workflow.Git.GitCommitOutcome(false, null, "simulated commit failure", "simulated commit failure")
                : new Hoshin.Workflow.Git.GitCommitOutcome(true, "deadbeef", null, "ok");
        }

        public Hoshin.Workflow.Git.GitResult ResetMixed(string dir, string rev)
        {
            ResetCalls++;
            return new Hoshin.Workflow.Git.GitResult(true, 0, "", "");
        }

        public Hoshin.Workflow.Git.GitResult Push(string dir, string remote, string refSpec) =>
            new(true, 0, "", "");
    }

    private static void SeedAcceptedPlan(string copyRoot, string changeFolder, string changeName, string repoName, string taskJson)
    {
        TestGitFixture.Write(Path.Combine(changeFolder, "intent.md"), "```jsonc\n{ \"title\": \"Fix loop\", \"system\": \"shop\", \"author\": \"Pat\", \"date\": \"2026-11-26\", \"status\": \"accepted\", \"scope\": \"shop\" }\n```\n\n# Intent: Fix loop\n");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept intent {changeName}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(Path.Combine(changeFolder, "spec.md"), $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Fix loop

            ```gherkin
            Feature: Fix loop
              @R1 @from:intent
              Rule: Behavior
                @R1.1
                Scenario: Works
                  Given input
                  When invoked
                  Then succeeds
            ```
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept spec {changeName}", "-m", "Generated-By: hoshin");
        var specSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(Path.Combine(changeFolder, "plan.md"), $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Fix loop

            ## Tasks

            ### T1: Implement behavior

            ```jsonc
            {{taskJson}}
            ```

            #### Change
            Implement behavior in `{{repoName}}`.

            #### Tests
            Verify behavior in `{{repoName}}`.
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept plan {changeName}", "-m", "Generated-By: hoshin");
    }

    private static string RunStart(string sessionId, string cwd, string skill, string args)
    {
        var (stdout, _) = TestServices.HookOrchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = sessionId,
                cwd,
                command_name = $"hoshin:{skill}",
                command_args = args,
                prompt = args.Length == 0 ? $"/hoshin:{skill}" : $"/hoshin:{skill} {args}",
            })),
            rolesRoot: null);
        Assert.NotNull(stdout);
        using var doc = JsonDocument.Parse(stdout!);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    private static string RunResult(string sessionId, string cwd, string prompt)
    {
        var (stdout, _) = TestServices.HookOrchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = sessionId,
                cwd,
                tool_name = "Agent",
                tool_use_id = "toolu_agent",
                tool_input = new { subagent_type = "general-purpose", description = "Build", prompt },
                tool_response = new { status = "completed", content = new[] { new { type = "text", text = "Done." } } },
            })),
            rolesRoot: null);
        Assert.NotNull(stdout);
        using var doc = JsonDocument.Parse(stdout!);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    private static void RunStop(string sessionId, string cwd)
    {
        TestServices.HookOrchestrator.HandleStop(
            TestServices.HookAdapter.Parse(StopPayload(sessionId, cwd)),
            rolesRoot: null);
    }

    private static string StopPayload(string sessionId, string cwd) => JsonSerializer.Serialize(new
    {
        hook_event_name = "Stop",
        session_id = sessionId,
        cwd,
        stop_hook_active = false,
    });

    private static string ExtractSubagentPrompt(string context)
    {
        var start = context.IndexOf(HookInstructions.BeginMarker, StringComparison.Ordinal);
        var end = context.IndexOf(HookInstructions.EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"Expected subagent markers in:\n{context}");
        return context[(start + HookInstructions.BeginMarker.Length)..end].Trim('\r', '\n');
    }
}
