using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class WorkflowNextTests
{
    private static WorkflowDocState MissingDoc(string name) =>
        new($"/repo/.hoshin/{name}.md", Exists: false, InHead: false, Uncommitted: false, WorkingStatus: null, HeadStatus: null);

    private static WorkflowDocState Doc(string name, string? status, bool inHead = true, bool uncommitted = false) =>
        new($"/repo/.hoshin/{name}.md", Exists: true, InHead: inHead, Uncommitted: uncommitted, WorkingStatus: status, HeadStatus: inHead ? status : null);

    private static WorkflowPlan SamplePlan(bool hasPlan = true, string? branch = "feat/demo") => new(
        Change: "2026-11-21-demo",
        Scope: "demo",
        Base: "main",
        Tasks: hasPlan
            ? [
                new WorkflowTask("T1", "First task", "Do T1", ["node", "--test", "test/t1.test.mjs"]),
                new WorkflowTask("T2", "Second task", "Do T2", ["node", "--test", "test/t2.test.mjs"]),
              ]
            : [],
        Checks: [["node", "--test"]],
        HasPlan: hasPlan,
        Branch: branch);

    private static WorkflowState MakeState(
        WorkflowDocState? intent = null,
        WorkflowDocState? spec = null,
        WorkflowDocState? planDoc = null,
        WorkflowPlan? plan = null,
        WorkflowLedger? ledger = null,
        string head = "aaaaaaa1111111") =>
        new(
            RepoRoot: "/repo",
            ChangeFolder: "/repo/.hoshin",
            Plan: plan ?? SamplePlan(),
            Head: head,
            Ledger: ledger ?? new WorkflowLedger(),
            SkillsRoot: null,
            Intent: intent ?? MissingDoc("intent"),
            Spec: spec ?? MissingDoc("spec"),
            PlanDoc: planDoc ?? Doc("plan", "accepted"));

    // =========================================================================
    // 1. Full Authoring -> Build -> Checks -> Approval -> Publish -> Done Chain
    // =========================================================================

    [Fact]
    public void Next_FullLifecycle_TransitionsInExactOrder()
    {
        // 1. No intent & no plan -> WriteIntent
        var s1 = MakeState(intent: MissingDoc("intent"), spec: MissingDoc("spec"), planDoc: MissingDoc("plan"), plan: SamplePlan(hasPlan: false));
        var step1 = Assert.IsType<WriteDocument>(WorkflowEngine.Next(s1));
        Assert.Equal("intent", step1.Document);

        // 2. Uncommitted intent -> still WriteIntent
        var s1b = s1 with { Intent = Doc("intent", "draft", inHead: false, uncommitted: true) };
        Assert.Equal("intent", Assert.IsType<WriteDocument>(WorkflowEngine.Next(s1b)).Document);

        // 3. Committed draft intent -> AwaitAccept(intent)
        var s2 = s1 with { Intent = Doc("intent", "draft", inHead: true, uncommitted: false) };
        var step2 = Assert.IsType<AwaitAccept>(WorkflowEngine.Next(s2));
        Assert.Equal("intent", step2.Document);

        // 4. Accepted intent, missing spec -> WriteSpec
        var s3 = s2 with { Intent = Doc("intent", "accepted") };
        var step3 = Assert.IsType<WriteDocument>(WorkflowEngine.Next(s3));
        Assert.Equal("spec", step3.Document);

        // 5. Committed draft spec -> AwaitAccept(spec)
        var s4 = s3 with { Spec = Doc("spec", "draft") };
        var step4 = Assert.IsType<AwaitAccept>(WorkflowEngine.Next(s4));
        Assert.Equal("spec", step4.Document);

        // 6. Accepted spec, missing plan -> WritePlan
        var s5 = s4 with { Spec = Doc("spec", "accepted") };
        var step5 = Assert.IsType<WriteDocument>(WorkflowEngine.Next(s5));
        Assert.Equal("plan", step5.Document);

        // 7. Committed draft plan -> AwaitAccept(plan)
        var s6 = s5 with { Plan = SamplePlan(hasPlan: true), PlanDoc = Doc("plan", "draft") };
        var step6 = Assert.IsType<AwaitAccept>(WorkflowEngine.Next(s6));
        Assert.Equal("plan", step6.Document);

        // 8. Accepted plan -> Implement T1 (attempt 1)
        var s7 = s6 with { PlanDoc = Doc("plan", "accepted") };
        var step7 = Assert.IsType<Implement>(WorkflowEngine.Next(s7));
        Assert.Equal("T1", step7.Agent.Task);
        Assert.Equal(1, step7.Agent.Attempt);

        // 9. Failed attempt 1 on T1 -> Retry T1 (attempt 2) with handoff
        var ledger = new WorkflowLedger();
        ledger.Verdicts.Add(new WorkflowVerdict("T1", 1, s7.Head, false, "not observed", ["red: test passed"], DateTimeOffset.UtcNow));
        var s8 = s7 with { Ledger = ledger };
        var step8 = Assert.IsType<Retry>(WorkflowEngine.Next(s8));
        Assert.Equal("T1", step8.Agent.Task);
        Assert.Equal(2, step8.Agent.Attempt);
        Assert.Contains("red: test passed", step8.Handoff);

        // 10. T1 passes -> Implement T2 (attempt 1)
        ledger.Verdicts.Add(new WorkflowVerdict("T1", 2, "bbbbbbb2222222", true, "observed", [], DateTimeOffset.UtcNow));
        var s9 = s8 with { Head = "bbbbbbb2222222" };
        var step9 = Assert.IsType<Implement>(WorkflowEngine.Next(s9));
        Assert.Equal("T2", step9.Agent.Task);
        Assert.Equal(1, step9.Agent.Attempt);

        // 11. T2 passes -> RunChecks at current HEAD
        ledger.Verdicts.Add(new WorkflowVerdict("T2", 1, "ccccccc3333333", true, "observed", [], DateTimeOffset.UtcNow));
        var s10 = s9 with { Head = "ccccccc3333333" };
        var step10 = Assert.IsType<RunChecks>(WorkflowEngine.Next(s10));
        Assert.Equal("ccccccc3333333", step10.Head);

        // 12. Checks pass -> ReviewDocument
        ledger.Checks.Add(new WorkflowCheckRun("ccccccc3333333", true, "ok", DateTimeOffset.UtcNow));
        var stepReview = Assert.IsType<ReviewDocument>(WorkflowEngine.Next(s10));
        Assert.Equal("ccccccc3333333", stepReview.Head);

        // 13. Clean review -> NeedsApproval
        ledger.Reviews.Add(new WorkflowReviewRun("ccccccc3333333", 1, 0, 0, DateTimeOffset.UtcNow));
        var step11 = Assert.IsType<NeedsApproval>(WorkflowEngine.Next(s10));
        Assert.Equal("ccccccc3333333", step11.Head);

        // 14. Approved -> Publish
        ledger.Approvals.Add(new WorkflowApproval("ccccccc3333333", true, "approved", DateTimeOffset.UtcNow));
        var step12 = Assert.IsType<Publish>(WorkflowEngine.Next(s10));
        Assert.Equal("ccccccc3333333", step12.Head);
        Assert.Equal("refs/heads/feat/demo", step12.Ref);

        // 15. Published -> Done(Success: true)
        ledger.Published.Add(new WorkflowPublished("ccccccc3333333", "refs/heads/feat/demo", DateTimeOffset.UtcNow));
        var step13 = Assert.IsType<Done>(WorkflowEngine.Next(s10));
        Assert.True(step13.Success);
        Assert.Contains("published", step13.Outcome);
    }

    [Fact]
    public void Next_StopsWithDoneFalse_WhenRetryCapExceeded_ChecksFail_OrRejected()
    {
        // Retry cap (MaxAttempts = 3)
        var ledgerMax = new WorkflowLedger();
        for (var i = 1; i <= WorkflowEngine.MaxAttempts; i++)
        {
            ledgerMax.Verdicts.Add(new WorkflowVerdict("T1", i, "head1", false, "not observed", [$"fail {i}"], DateTimeOffset.UtcNow));
        }

        var doneRetry = Assert.IsType<Done>(WorkflowEngine.Next(MakeState(ledger: ledgerMax)));
        Assert.False(doneRetry.Success);
        Assert.Contains("T1 failed 3 attempts", doneRetry.Outcome);
        Assert.Contains("fail 3", doneRetry.Outcome);

        // Failed checks at HEAD
        var ledgerChecks = new WorkflowLedger();
        ledgerChecks.Verdicts.Add(new WorkflowVerdict("T1", 1, "h1", true, "observed", [], DateTimeOffset.UtcNow));
        ledgerChecks.Verdicts.Add(new WorkflowVerdict("T2", 1, "h2", true, "observed", [], DateTimeOffset.UtcNow));
        ledgerChecks.Checks.Add(new WorkflowCheckRun("h2", false, "boom", DateTimeOffset.UtcNow));
        var doneChecks = Assert.IsType<Done>(WorkflowEngine.Next(MakeState(ledger: ledgerChecks, head: "h2")));
        Assert.False(doneChecks.Success);
        Assert.Contains("checks failed", doneChecks.Outcome);

        // Engineer rejected at approval
        var ledgerReject = new WorkflowLedger();
        ledgerReject.Verdicts.Add(new WorkflowVerdict("T1", 1, "h1", true, "observed", [], DateTimeOffset.UtcNow));
        ledgerReject.Verdicts.Add(new WorkflowVerdict("T2", 1, "h2", true, "observed", [], DateTimeOffset.UtcNow));
        ledgerReject.Checks.Add(new WorkflowCheckRun("h2", true, "ok", DateTimeOffset.UtcNow));
        ledgerReject.Reviews.Add(new WorkflowReviewRun("h2", 1, 0, 0, DateTimeOffset.UtcNow));
        ledgerReject.Approvals.Add(new WorkflowApproval("h2", false, "needs redesign", DateTimeOffset.UtcNow));
        var doneReject = Assert.IsType<Done>(WorkflowEngine.Next(MakeState(ledger: ledgerReject, head: "h2")));
        Assert.False(doneReject.Success);
        Assert.Contains("needs redesign", doneReject.Outcome);

        // Rejected documents stop immediately
        foreach (var docName in new[] { "intent", "spec", "plan" })
        {
            var st = MakeState(
                intent: docName == "intent" ? Doc("intent", "rejected") : Doc("intent", "accepted"),
                spec: docName == "spec" ? Doc("spec", "rejected") : Doc("spec", "accepted"),
                planDoc: docName == "plan" ? Doc("plan", "rejected") : Doc("plan", "accepted"));
            var d = Assert.IsType<Done>(WorkflowEngine.Next(st));
            Assert.False(d.Success);
            Assert.Contains($"the {docName} is rejected", d.Outcome);
        }
    }

    [Fact]
    public void Gate_AllowsOwningSkillAndRefusesOutOfOrderSkills()
    {
        var awaitIntent = MakeState(intent: Doc("intent", "draft"), spec: MissingDoc("spec"), planDoc: MissingDoc("plan"), plan: SamplePlan(hasPlan: false));
        Assert.Null(WorkflowEngine.Gate(awaitIntent, "accept"));
        Assert.Contains("cannot run", WorkflowEngine.Gate(awaitIntent, "spec")!);
        Assert.Contains("cannot run", WorkflowEngine.Gate(awaitIntent, "plan")!);
        Assert.Contains("cannot run", WorkflowEngine.Gate(awaitIntent, "build")!);

        var readyToBuild = MakeState();
        Assert.Null(WorkflowEngine.Gate(readyToBuild, "build"));
        Assert.Contains("cannot run", WorkflowEngine.Gate(readyToBuild, "accept")!);
        Assert.Contains("cannot run", WorkflowEngine.Gate(readyToBuild, "intent")!);
    }

    // =========================================================================
    // 2. Strongly-Typed RedClassifier & AllowListedCommandRunner & Auto-Wrap
    // =========================================================================

    [Fact]
    public void RedClassifier_AcceptsAssertionAndMissingSymbol_RejectsSyntaxPassAndTimeout()
    {
        var assertion = RedClassifier.Classify(new AllowListedCommandResult(1, "AssertionError: expected 2 to equal 3", false, TimeSpan.Zero));
        Assert.True(assertion.IsValidRed);
        Assert.Equal(RedKind.AssertionFailure, assertion.Kind);

        foreach (var missingOutput in new[]
        {
            "error CS0246: The type or namespace name 'Widget' could not be found",
            "error CS0103: The name 'ComputeTotal' does not exist in the current context",
            "error CS1061: 'Order' does not contain a definition for 'ApplyDiscount'",
            "error TS2304: Cannot find name 'formatInvoice'.",
            "SyntaxError: The requested module '../src/math.mjs' does not provide an export named 'multiply'",
        })
        {
            var missing = RedClassifier.Classify(new AllowListedCommandResult(1, missingOutput, false, TimeSpan.Zero));
            Assert.True(missing.IsValidRed, missingOutput);
            Assert.Equal(RedKind.MissingSymbol, missing.Kind);
        }

        foreach (var syntaxOutput in new[]
        {
            "error CS1002: ; expected",
            "error CS1513: } expected",
            "SyntaxError: Unexpected token '}'",
        })
        {
            var syntax = RedClassifier.Classify(new AllowListedCommandResult(1, syntaxOutput, false, TimeSpan.Zero));
            Assert.False(syntax.IsValidRed, syntaxOutput);
            Assert.Equal(RedKind.SyntaxError, syntax.Kind);
        }

        var passed = RedClassifier.Classify(new AllowListedCommandResult(0, "All tests passed", false, TimeSpan.Zero));
        Assert.False(passed.IsValidRed);
        Assert.Equal(RedKind.PassedUnexpectedly, passed.Kind);

        var timedOut = RedClassifier.Classify(new AllowListedCommandResult(-1, "", true, TimeSpan.FromMinutes(5)));
        Assert.False(timedOut.IsValidRed);
        Assert.Equal(RedKind.TimedOut, timedOut.Kind);
    }

    [Fact]
    public void AllowListedCommandRunner_StripsSecretEnvironmentVariables()
    {
        var prevAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var prevGhToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        var prevSsh = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        var prevProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-secret-must-not-leak");
            Environment.SetEnvironmentVariable("GH_TOKEN", "ghp_secret_must_not_leak");
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", "/tmp/ssh-agent-sock");
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://x");
            var res = TestServices.AllowListedRunner.Run(
                Directory.GetCurrentDirectory(),
                ["node", "-e", "process.stdout.write([process.env.ANTHROPIC_API_KEY || 'STRIPPED', process.env.GH_TOKEN || 'STRIPPED', process.env.SSH_AUTH_SOCK || 'STRIPPED', process.env.HTTPS_PROXY || 'MISSING'].join('|'))"]);
            Assert.True(res.Ok, res.Output);
            Assert.Equal("STRIPPED|STRIPPED|STRIPPED|http://x", res.Output.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prevAnthropic);
            Environment.SetEnvironmentVariable("GH_TOKEN", prevGhToken);
            Environment.SetEnvironmentVariable("SSH_AUTH_SOCK", prevSsh);
            Environment.SetEnvironmentVariable("HTTPS_PROXY", prevProxy);
        }
    }

    [Fact]
    public void AutoWrapBareJsonFrontMatter_WrapsLine1ObjectAndIgnoresAlreadyFenced()
    {
        const string bare = """
            {
              "layout": 2,
              "system": "demo"
            }

            # Spec: Demo
            """;
        var wrapped = WorkflowRunner.TryAutoWrapBareJsonFrontMatter(bare);
        Assert.NotNull(wrapped);
        Assert.StartsWith("```jsonc\n{", wrapped);
        Assert.Contains("}\n```\n\n# Spec: Demo", wrapped);

        Assert.Null(WorkflowRunner.TryAutoWrapBareJsonFrontMatter(wrapped!));
    }

    // =========================================================================
    // 3. WorkflowRunner Two-Phase Red/Green Commit Split & Zero-Commit Failures
    // =========================================================================

    [Fact]
    public void EvaluateAndCommitTask_SplitsTestAndProdCommits_AndCommitsNothingOnFailure()
    {
        using var tmp = new TempDir("hoshin-runner-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export function add(a, b) { return a + b; }\n",
            [".hoshin/change.jsonc"] = """
                {
                  "version": 1,
                  "change": "2026-11-21-math",
                  "branch": "feat/math",
                  "type": "feat",
                  "scope": "math",
                  "base": "main"
                }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                {
                  "layout": 2,
                  "system": "math",
                  "status": "accepted"
                }
                ```

                # Plan: Math

                ## Tasks

                ### T1: Add multiply

                ```jsonc
                {
                  "id": "T1",
                  "repo": "repo",
                  "test": ["node", "--test", "test/t1.test.mjs"]
                }
                ```

                #### Change

                Export `multiply(a, b)`.

                #### Tests

                Verify `multiply(2, 3) === 6`.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var startHead = repo.Head();
        var runner = TestServices.WorkflowRunner;

        // Attempt 1 (BAD): test passes even without prod change -> Red not observed -> ZERO commits made!
        TestGitFixture.Write(Path.Combine(repoDir, "test", "t1.test.mjs"), """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            test('always passes', () => { assert.equal(1, 1); });
            """);
        TestGitFixture.Write(Path.Combine(repoDir, "src", "math.mjs"), "export function add(a, b) { return a + b; }\nexport function multiply(a, b) { return a * b; }\n");

        var state1 = TestServices.ReadState(repo)!;
        var v1 = runner.EvaluateAndCommitTask(repo, state1, "T1", 1);
        Assert.False(v1.Passed);
        Assert.Equal(startHead, repo.Head()); // ZERO commits on failure!
        Assert.Contains(v1.Reasons, r => r.Contains("not observed"));
        Assert.True(File.Exists(Path.Combine(repoDir, "test", "t1.test.mjs"))); // Working tree preserved for Retry!

        // Attempt 2 (GOOD): test asserts multiply(2, 3) === 6 -> fails on detached worktree without prod, passes with prod!
        TestGitFixture.Write(Path.Combine(repoDir, "test", "t1.test.mjs"), """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { multiply } from '../src/math.mjs';
            test('multiply', () => { assert.equal(multiply(2, 3), 6); });
            """);

        var state2 = TestServices.ReadState(repo)!;
        var v2 = runner.EvaluateAndCommitTask(repo, state2, "T1", 2);
        Assert.True(v2.Passed, string.Join("; ", v2.Reasons));
        Assert.NotNull(v2.TestCommit);
        Assert.StartsWith("observed", v2.Red);

        // Verify two separate commits were created in exact order with Plan-Task & Generated-By trailers.
        var log = TestGitFixture.Git(repoDir, "log", "--reverse", "--format=%s|%b", $"{startHead}..HEAD");
        var commits = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("test(math): Add multiply", log);
        Assert.Contains("feat(math): Add multiply", log);
        Assert.Contains("Plan-Task: T1", log);
        Assert.Contains("Generated-By: hoshin", log);

        // Verify test commit touched ONLY the test file, and prod commit touched ONLY the prod file.
        var testFiles = TestGitFixture.Git(repoDir, "show", "--name-only", "--format=", v2.TestCommit!);
        Assert.Equal("test/t1.test.mjs", testFiles.Trim());
        var prodFiles = TestGitFixture.Git(repoDir, "show", "--name-only", "--format=", v2.Head);
        Assert.Equal("src/math.mjs", prodFiles.Trim());

        // Cached detached worktree exists for reuse.
        Assert.True(Directory.Exists(repo.RedWorktreePath));
    }

    [Fact]
    public void EvaluateAndCommitTask_AcceptsStronglyTypedMissingSymbolRed()
    {
        using var tmp = new TempDir("hoshin-typed-red-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/widget.txt"] = "base\n",
            ["scripts/fake-dotnet-test.mjs"] = """
                import { existsSync, readFileSync } from 'node:fs';
                const testSrc = readFileSync('tests/WidgetTests.cs', 'utf8');
                if (testSrc.includes('SYNTAX_BROKEN')) {
                  console.error('WidgetTests.cs(4,12): error CS1002: ; expected');
                  process.exit(1);
                }
                if (!existsSync('src/Widget.cs')) {
                  console.error("WidgetTests.cs(5,9): error CS0246: The type or namespace name 'Widget' could not be found");
                  process.exit(1);
                }
                console.log('Passed! - Failed: 0, Passed: 1');
                """,
            [".hoshin/change.jsonc"] = """
                {
                  "version": 1,
                  "change": "2026-11-21-widget",
                  "branch": "feat/widget",
                  "type": "feat",
                  "scope": "widget",
                  "base": "main"
                }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                {
                  "layout": 2,
                  "system": "widget",
                  "status": "accepted"
                }
                ```

                # Plan: Widget

                ## Tasks

                ### T1: Add Widget

                ```jsonc
                {
                  "id": "T1",
                  "repo": "repo",
                  "test": ["node", "scripts/fake-dotnet-test.mjs"]
                }
                ```

                #### Change

                Create `src/Widget.cs`.

                #### Tests

                Create `tests/WidgetTests.cs`.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        TestGitFixture.Write(Path.Combine(repoDir, "tests", "WidgetTests.cs"), "public class WidgetTests { Widget w = new(); }\n");
        TestGitFixture.Write(Path.Combine(repoDir, "src", "Widget.cs"), "public sealed class Widget {}\n");

        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
        Assert.Contains("CS0246", verdict.Red);
    }
}
