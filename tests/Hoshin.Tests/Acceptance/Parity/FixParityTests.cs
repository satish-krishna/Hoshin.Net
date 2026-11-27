using System.Text.RegularExpressions;
using Hoshin.Tests.Acceptance.Harness;
using Xunit;

namespace Hoshin.Tests.Acceptance.Parity;

/// <summary>
/// B03 parity gate for the light track (evals/README.md, "Where each original case went"). Written by the reviewer, not
/// the implementer; the implementer must not edit this file, its harness, or <c>Harness/</c>, and raises a gate dispute
/// instead. Each test plays the original eval case's mechanism with typed-user payloads (the original's "accept in
/// advance" becomes a typed accept) in a single-repository workspace, and judges only what an engineer can see: the
/// commits on the change's branch, the documents at HEAD, and what the hooks told the session.
/// </summary>
public static class FixParityTests
{
    private static readonly Regex TaskCap = new(
        @"\b(?:three|3)\b[\s-]+(?:plan\s+)?tasks?\b|\b(?:cap|limit)\b.{0,40}\b(?:three|3)\b|\btask\s+cap\b|\bat\s+most\s+(?:three|3)\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex Closed = new(@"\bclosed\b", RegexOptions.IgnoreCase);

    private static string TaskSection(string plan, string task) =>
        plan[plan.IndexOf($"### {task}", StringComparison.Ordinal)..];

    private static string WithoutStatus(string text) => Regex.Replace(text, "\"status\"\\s*:\\s*\"[a-z]+\"", "\"status\": \"-\"");

    /// <summary>Row 7, original fix-accept-refuses-a-grown-plan, B14 item 1: the boundary is rechecked at a typed <c>fix --accept</c>; a plan that grew to six tasks is refused and nothing is accepted.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-accept-refuses-a-grown-plan")]
    public sealed class Fix07_AcceptRefusesAGrownPlan
    {
        [Fact]
        public void TypedAccept_OfAPlanThatGrewPastThreeTasks_IsRefused_AndAcceptsNothing()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            FixParityHarness.GrowPlan(fix, 6);
            ws.HandCommit(fix, $"docs(shop): edit plan of {fix.Name}", fix.Plan);

            var told = dev.Prompt("fix", "--accept");

            Assert.True(ws.AcceptCommits(fix.Root).Length == 0, dev.Transcript($"a six-task fix must not be accepted, but the branch holds: {string.Join(" | ", ws.AcceptCommits(fix.Root))}"));
            Assert.True(ws.IsStatus(fix.Plan, "draft"), dev.Transcript("the plan stays a draft at HEAD"));
            Assert.True(TaskCap.IsMatch(dev.Told(told)), dev.Transcript($"the refusal must name the three-task cap, but the engineer was told: {dev.Told(told)}"));
        }
    }

    /// <summary>Row 8, original fix-accept-refuses-a-rejected-fix, B14 and B15: a typed accept of a rejected fix says the change is closed and commits nothing.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-accept-refuses-a-rejected-fix")]
    public sealed class Fix08_AcceptRefusesARejectedFix
    {
        [Fact]
        public void TypedAccept_OfARejectedFix_SaysTheChangeIsClosed_AndCommitsNothing()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            dev.Turn(dev.Prompt("reject"));
            var branch = $"change/{fix.Name}";
            var tip = ws.Git(ws.Master, "rev-parse", branch);
            Assert.Contains("reject", ws.Git(ws.Master, "log", "-1", "--format=%s", branch), StringComparison.OrdinalIgnoreCase);

            var told = dev.Prompt("fix", $"--accept {fix.Name}");

            Assert.Equal(tip, ws.Git(ws.Master, "rev-parse", branch));
            Assert.True(ws.Git(ws.Master, "log", "--format=%s", $"main..{branch}").Split('\n').All(s => !s.Contains(": accept ", StringComparison.Ordinal)), dev.Transcript("a rejected fix accepts nothing"));
            Assert.True(Closed.IsMatch(dev.Told(told)), dev.Transcript($"the engineer must be told the change is closed, but was told: {dev.Told(told)}"));
        }
    }

    /// <summary>Row 11, original fix-accepts-a-bug-fix (parity half), B14: the typed accept commits the intent, spec and plan acceptances in order, with pins that hold, and no product code.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-accepts-a-bug-fix")]
    public sealed class Fix11_AcceptsABugFix
    {
        [Fact]
        public void TypedAccept_CommitsIntentSpecAndPlan_InOrder_WithPinsThatHold()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);

            dev.Turn(dev.Prompt("fix", "--accept"));

            var oldestFirst = ws.ChangeSubjects(fix.Root).Reverse().ToArray();
            var at = new[] { "draft fix", "accept intent", "accept spec", "accept plan" }
                .Select(w => Array.FindIndex(oldestFirst, s => s.Contains($"{w} {fix.Name}", StringComparison.Ordinal))).ToArray();
            Assert.True(at.All(i => i >= 0) && at.SequenceEqual(at.Order()), dev.Transcript($"the branch must hold the draft, then the acceptances of the intent, the spec and the plan, in that order, but holds: {string.Join(" | ", oldestFirst)}"));
            Assert.True(fix.Documents.All(d => ws.IsStatus(d, "accepted")), dev.Transcript("all three documents are accepted at HEAD"));
            ws.AssertPinsHold(fix, dev.Transcript("the spec pins the commit that accepted the intent, the plan the commit that accepted the spec"));
            FixParityHarness.AssertNoAdvanceAcceptance(ws, fix);
            var changed = ws.Git(fix.Root, "diff", "--name-only", "main", "HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(changed.All(f => f.StartsWith("intent/", StringComparison.Ordinal)), dev.Transcript($"a fix's acceptance touches no product code, but the branch changed: {string.Join(", ", changed)}"));
        }
    }

    /// <summary>Row 12, original fix-accepts-a-task-with-a-protecting-test, B14 and R24: a task with one new failing test and a protecting test covering a second scenario is one behavior, so the typed accept accepts it in order.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-accepts-a-task-with-a-protecting-test")]
    public sealed class Fix12_AcceptsATaskWithAProtectingTest
    {
        [Fact]
        public void TypedAccept_OfATaskWithAProtectingTest_AcceptsIntentSpecAndPlanInOrder()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            FixParityHarness.AddProtectingTest(fix);
            ws.HandCommit(fix, $"docs(shop): edit spec and plan of {fix.Name}", fix.Spec, fix.Plan);

            dev.Turn(dev.Prompt("fix", "--accept"));

            var accepted = ws.AcceptCommits(fix.Root).Reverse().Select(s => Regex.Match(s, "accept (intent|spec|plan)").Groups[1].Value).ToArray();
            Assert.True(accepted.SequenceEqual(["intent", "spec", "plan"]), dev.Transcript($"one task with a protecting test is one task: the intent, spec and plan are accepted in order, but the acceptances are: {string.Join(", ", accepted)}"));
            ws.AssertPinsHold(fix, dev.Transcript("the pins hold after the acceptance"));
        }
    }

    /// <summary>Row 13, original fix-accept-stops-at-unrecorded-changes, B14 item 4: a typed accept over an uncommitted hand edit records the edit as a draft and accepts nothing.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-accept-stops-at-unrecorded-changes")]
    public sealed class Fix13_AcceptStopsAtUnrecordedChanges
    {
        [Fact]
        public void TypedAccept_OverAHandEdit_RecordsTheEditAsADraft_AndAcceptsNothing()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            FixParityHarness.EditSpecByHand(fix);

            dev.Turn(dev.Prompt("fix", "--accept"));

            Assert.True(ws.AcceptCommits(fix.Root).Length == 0, dev.Transcript("an edit nobody read must not be accepted in the same run"));
            Assert.True(ws.CommitsWith(fix.Root, "draft fix").Length == 2, dev.Transcript($"the edit is recorded in exactly one more draft commit, but the branch holds: {string.Join(" | ", ws.ChangeSubjects(fix.Root))}"));
            Assert.Contains("nickname", ws.AtHead(fix.Spec), StringComparison.Ordinal);
            Assert.True(ws.Git(fix.Root, "status", "--porcelain").Length == 0, dev.Transcript("nothing is left uncommitted"));
            FixParityHarness.AssertNoAdvanceAcceptance(ws, fix);
        }
    }

    /// <summary>Row 16, original fix-finishes-an-uncommitted-draft, B14: a draft that stopped before the Stop hook committed anything is finished: one draft commit holds all three documents and nothing is accepted.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-finishes-an-uncommitted-draft")]
    public sealed class Fix16_FinishesAnUncommittedDraft
    {
        [Fact]
        public void AStoppedDraft_IsFinished_InOneDraftCommit_AndAcceptsNothing()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var first = ws.Session("stopped-session", ws.Master);
            var fix = ws.DraftWrittenButNotCommitted(first);
            Assert.True(ws.CommitsWith(fix.Root, "draft fix").Length == 0, "setup: the stopped session committed no draft");

            var next = ws.Session("next-session", ws.Master);
            next.Turn(next.Prompt("fix", fix.Name));

            var drafts = ws.CommitsWith(fix.Root, "draft fix");
            Assert.True(drafts.Length == 1, next.Transcript($"the three written documents are committed in one draft commit, but the branch holds: {string.Join(" | ", ws.ChangeSubjects(fix.Root))}"));
            var files = ws.Git(fix.Root, "show", "--name-only", "--format=", "HEAD~0").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var inDraft = ws.Git(fix.Root, "log", "--format=%H", "--grep", "draft fix", "-1");
            var named = ws.Git(fix.Root, "show", "--name-only", "--format=", inDraft).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.True(new[] { "intent.md", "spec.md", "plan.md" }.All(f => named.Any(n => n.EndsWith("/" + f, StringComparison.Ordinal))), next.Transcript($"the draft commit holds intent.md, spec.md and plan.md, but holds: {string.Join(", ", named)} (HEAD holds {string.Join(", ", files)})"));
            Assert.True(ws.AcceptCommits(fix.Root).Length == 0, next.Transcript("a finished draft accepts nothing"));
            Assert.True(fix.Documents.All(d => ws.IsStatus(d, "draft")), next.Transcript("all three documents are drafts at HEAD"));
        }
    }

    /// <summary>Row 17, original fix-finishes-a-stopped-acceptance, B14: a typed accept over an acceptance that stopped after the intent's status line finishes it in order, with the pins written and no second draft commit.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-finishes-a-stopped-acceptance")]
    public sealed class Fix17_FinishesAStoppedAcceptance
    {
        [Fact]
        public void TypedAccept_OverAHalfDoneAcceptance_FinishesItInOrder()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            FixParityHarness.Edit(fix.Intent, t => t.Replace("\"status\": \"draft\"", "\"status\": \"accepted\"", StringComparison.Ordinal));

            dev.Turn(dev.Prompt("fix", "--accept"));

            var accepted = ws.AcceptCommits(fix.Root).Reverse().Select(s => Regex.Match(s, "accept (intent|spec|plan)").Groups[1].Value).ToArray();
            Assert.True(accepted.SequenceEqual(["intent", "spec", "plan"]), dev.Transcript($"the stopped acceptance is finished: intent, spec, plan in order, but the acceptances are: {string.Join(", ", accepted)}"));
            Assert.True(ws.CommitsWith(fix.Root, "draft fix").Length == 1, dev.Transcript("finishing an acceptance makes no second draft commit"));
            Assert.True(fix.Documents.All(d => ws.IsStatus(d, "accepted")), dev.Transcript("all three documents are accepted at HEAD"));
            ws.AssertPinsHold(fix, dev.Transcript("the pins are written"));
            FixParityHarness.AssertNoAdvanceAcceptance(ws, fix);
            Assert.True(ws.Git(fix.Root, "status", "--porcelain").Length == 0, dev.Transcript("nothing is left uncommitted"));
        }
    }

    /// <summary>Row 19, original fix-records-the-engineers-edits, B14 item 4: finishing a draft the engineer edited by hand keeps the edit's words in exactly one more draft commit and accepts nothing.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-records-the-engineers-edits")]
    public sealed class Fix19_RecordsTheEngineersEdits
    {
        [Fact]
        public void FinishingADraft_KeepsTheEngineersEdit_InOneMoreDraftCommit()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            FixParityHarness.EditSpecByHand(fix);

            dev.Turn(dev.Prompt("fix", fix.Name));

            Assert.Contains("nickname", ws.AtHead(fix.Spec), StringComparison.Ordinal);
            Assert.True(ws.CommitsWith(fix.Root, "draft fix").Length == 2, dev.Transcript($"the edit is recorded in exactly one more draft commit, but the branch holds: {string.Join(" | ", ws.ChangeSubjects(fix.Root))}"));
            Assert.True(ws.AcceptCommits(fix.Root).Length == 0, dev.Transcript("finishing a draft accepts nothing"));
            Assert.True(ws.Git(fix.Root, "status", "--porcelain").Length == 0, dev.Transcript("nothing is left uncommitted"));
            FixParityHarness.AssertNoAdvanceAcceptance(ws, fix);
        }
    }

    /// <summary>Row 23, original fix-rejects-a-drafted-fix, B15: a typed <c>fix --reject</c> commits the rejection as the branch tip, removes the change's copy, accepts nothing and leaves the product branch alone.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-rejects-a-drafted-fix")]
    public sealed class Fix23_RejectsADraftedFix
    {
        [Fact]
        public void TypedFixReject_CommitsTheRejection_AndAbandonsTheChange()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            var main = ws.Git(ws.Master, "rev-parse", "main");

            dev.Turn(dev.Prompt("fix", "--reject"));

            var branch = $"change/{fix.Name}";
            Assert.True(ws.Git(ws.Master, "log", "-1", "--format=%s", branch).Contains("reject", StringComparison.OrdinalIgnoreCase), dev.Transcript($"the rejection must be the branch tip, but the tip is `{ws.Git(ws.Master, "log", "-1", "--format=%s", branch)}`"));
            Assert.Matches("\"status\"\\s*:\\s*\"rejected\"", ws.Git(ws.Master, "show", $"{branch}:{FixParityHarness.Rel(fix.Intent)}"));
            Assert.True(ws.Git(ws.Master, "log", "--format=%s", $"main..{branch}").Split('\n').All(s => !s.Contains(": accept ", StringComparison.Ordinal)), dev.Transcript("a rejected fix accepts nothing"));
            Assert.False(Directory.Exists(fix.Root), dev.Transcript($"a rejected change's copy must be removed, but {fix.Root} still exists"));
            Assert.Equal(main, ws.Git(ws.Master, "rev-parse", "main"));
        }
    }

    /// <summary>Row 24, original fix-reopen-broken-pin-waits, B15: reopening on a broken plan pin reopens and stops, with one reopen commit and no acceptance or rewritten plan after it.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-reopen-broken-pin-waits")]
    public sealed class Fix24_ReopenBrokenPinWaits
    {
        [Fact]
        public void TypedReopen_OnABrokenPin_ReopensThePlanAndStops()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            dev.Turn(dev.Prompt("fix", "--accept"));
            FixParityHarness.Edit(fix.Spec, t => t.TrimEnd() + "\n\n## Out of scope\n\n- A greeting for a visitor whose name has accents.\n");
            ws.HandCommit(fix, $"docs(shop): widen the out of scope list in the spec of {fix.Name}", fix.Spec);
            var plan = ws.AtHead(fix.Plan);
            Assert.NotEqual(Regex.Match(plan, "\"source_spec_commit\"\\s*:\\s*\"(?<sha>[0-9a-f]+)\"").Groups["sha"].Value, ws.Git(fix.Root, "log", "-1", "--format=%H", "--", FixParityHarness.Rel(fix.Spec)));
            var before = ws.ChangeSubjects(fix.Root).Length;

            dev.Turn(dev.Prompt("reopen", "plan"));

            var after = ws.ChangeSubjects(fix.Root);
            Assert.True(after.Length == before + 1 && after[0].Contains("reopen", StringComparison.Ordinal), dev.Transcript($"one reopen commit and nothing after it, but the newest commits are: {string.Join(" | ", after.Take(3))}"));
            Assert.True(ws.IsStatus(fix.Plan, "draft"), dev.Transcript("the plan is a draft again"));
            Assert.True(ws.IsStatus(fix.Spec, "accepted") && ws.IsStatus(fix.Intent, "accepted"), dev.Transcript("the intent and the spec stay accepted"));
            Assert.Equal(WithoutStatus(plan), WithoutStatus(ws.AtHead(fix.Plan)));
            FixParityHarness.AssertNoAdvanceAcceptance(ws, fix);
        }
    }

    /// <summary>Row 25, original fix-reopen-keeps-built-tasks (parity half), B14 and B15: reopening an accepted fix whose T1 is built keeps T1 built: its commits, its build-log entry and its plan section stay as they were.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-reopen-keeps-built-tasks")]
    public sealed class Fix25_ReopenKeepsBuiltTasks
    {
        [Fact]
        public void TypedReopen_WithT1Built_KeepsT1Built()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            dev.Turn(dev.Prompt("fix", "--accept"));
            dev.Turn(dev.Prompt("build"));
            ws.AssertTaskCommitted(ws.BuildFolder("app"), "T1", dev);
            var builtCommits = ws.TaskCommits(fix.Root);
            var t1 = TaskSection(ws.AtHead(fix.Plan), "T1");
            var before = ws.ChangeSubjects(fix.Root).Length;

            dev.Turn(dev.Prompt("reopen", "spec"));

            Assert.True(ws.IsStatus(fix.Spec, "draft"), dev.Transcript("the engineer's reopen must reopen the accepted spec although T1 is built"));
            Assert.True(ws.ChangeSubjects(fix.Root).Length == before + 1, dev.Transcript("the reopen is one commit"));
            Assert.Equal(builtCommits, ws.TaskCommits(fix.Root));
            ws.AssertBuildLogHas(fix.Folder, "app", "T1", dev);
            Assert.Equal(t1, TaskSection(ws.AtHead(fix.Plan), "T1"));
            Assert.True(ws.AcceptCommits(fix.Root).Length == 3, dev.Transcript("a reopen accepts nothing"));
        }
    }

    /// <summary>Row 26, original fix-reopens-an-accepted-fix (parity half), B14 and B15: reopening an accepted fix's spec puts the spec and the plan back to draft, keeps the intent accepted, and is one reopen commit with no acceptance after it.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-reopens-an-accepted-fix")]
    public sealed class Fix26_ReopensAnAcceptedFix
    {
        [Fact]
        public void TypedReopen_PutsSpecAndPlanBackToDraft_KeepsTheIntent_InOneCommit()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            dev.Turn(dev.Prompt("fix", "--accept"));
            var intent = ws.AtHead(fix.Intent);
            var before = ws.ChangeSubjects(fix.Root).Length;

            dev.Turn(dev.Prompt("reopen", "spec"));

            var after = ws.ChangeSubjects(fix.Root);
            Assert.True(after.Length == before + 1 && after[0].Contains("reopen", StringComparison.Ordinal), dev.Transcript($"one reopen commit and nothing after it, but the newest commits are: {string.Join(" | ", after.Take(3))}"));
            Assert.True(ws.IsStatus(fix.Spec, "draft"), dev.Transcript("the spec is a draft again"));
            Assert.True(ws.IsStatus(fix.Plan, "draft"), dev.Transcript("the plan, which was accepted against the old spec, is a draft again"));
            Assert.True(ws.IsStatus(fix.Intent, "accepted"), dev.Transcript("the intent stays accepted"));
            Assert.Equal(intent, ws.AtHead(fix.Intent));
            Assert.True(ws.AcceptCommits(fix.Root).Length == 3, dev.Transcript("a reopen accepts nothing"));
        }
    }

    /// <summary>Row 27, original fix-waits-for-workbench-acceptance, B14: a model-invoked accept is refused, whatever its wording, and nothing is committed.</summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "fix-waits-for-workbench-acceptance")]
    public sealed class Fix27_WaitsForWorkbenchAcceptance
    {
        [Fact]
        public void ModelInvokedAccept_IsRefused_AndNothingIsCommitted()
        {
            using var ws = FixParityHarness.CreateWorkspace();
            var dev = ws.Session("dev-session", ws.Master);
            var fix = ws.Draft(dev);
            var before = ws.ChangeSubjects(fix.Root);

            var fixAccept = dev.ModelInvokedSkill("fix", "--accept");
            dev.Turn(fixAccept);
            var accept = dev.ModelInvokedSkill("accept");
            dev.Turn(accept);

            Assert.Contains("engineer", fixAccept ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("engineer", accept ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, ws.ChangeSubjects(fix.Root));
            Assert.True(fix.Documents.All(d => ws.IsStatus(d, "draft")), dev.Transcript("the documents stay drafts at HEAD"));
            Assert.True(ws.Git(fix.Root, "status", "--porcelain").Length == 0, dev.Transcript("nothing is left uncommitted"));
            FixParityHarness.AssertNoAdvanceAcceptance(ws, fix);
        }
    }
}
