using Hoshin.Tests.Acceptance.Harness;
using Hoshin.Workflow.Git;
using Unit = Hoshin.Tests.Unit;
using Xunit;
using Xunit.Abstractions;

namespace Hoshin.Tests.Acceptance;

/// <summary>
/// R21 acceptance gate (docs/review/TASKS.md, round 3). Written by the reviewer, not the implementer; the implementer
/// must not edit this file or <c>Harness/</c> to make it pass, and raises a gate dispute instead.
///
/// The R0 gate covers one happy change. Each test here is a path a real user takes that nothing drove end to end
/// before round 3, and each names, in its summary, the R21 item and the task that should turn it green. Like R0, they
/// drive the shipped <c>hoshin.dll</c> with Claude Code hook payloads and judge only what an engineer can see: commits,
/// branches, remote refs, committed documents, folders, pull requests, and what the session was told. None of them
/// reads <c>.git/hoshin</c>.
/// </summary>
public static class UserPathAcceptanceTests
{
    private const string BeginMarker = AcceptanceWorkspace.BeginMarker;

    /// <summary>R21 (1), R05 (W6): T1 built in clone A and pushed; clone B, with no local hoshin state, builds T2 and finishes.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_01_Resume
    {
        [Fact]
        public void ResumeOnAnotherMachine_BuildsTheNextTaskFromWhatWasPushed_AndTheChangeFinishes()
        {
            using var a = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutThenWhisper()));
            var poA = a.Session("po-a", a.Master);
            var plan = a.AuthorAndAcceptAll(poA);

            // Machine A: the engineer builds T1 in web, then stops before T2 and pushes everything that is committed.
            var webA = a.Session("web-a", a.Web);
            var first = webA.Prompt("build");
            Assert.True(first is not null && first.Contains(BeginMarker, StringComparison.Ordinal), webA.Transcript("/hoshin:build in web must dispatch T1"));
            webA.Dispatch(first);
            webA.EndTurnOnce();
            a.AssertTaskCommitted(a.Web, "T1", webA);
            a.AssertBuildLogHas(plan.Folder, "web", "T1", webA);
            foreach (var repo in new[] { plan.Root, a.Api, a.Web })
            {
                var branch = a.Git(repo, "branch", "--show-current");
                if (branch.StartsWith("change/", StringComparison.Ordinal))
                {
                    a.Git(repo, "push", "-q", "-u", "origin", branch);
                }
            }

            // Machine B: fresh clones on the change branches, an empty .git/hoshin everywhere.
            var b = a.CloneOnAnotherMachine("ws-b");
            var webB = b.Session("web-b", b.Web);
            webB.Turn(webB.Prompt("build"));
            Assert.True(b.Dispatches.Count > 0, webB.Transcript("on another machine, /hoshin:build in web must dispatch T2"));
            Assert.True(b.Dispatches[0].Task == "T2", webB.Transcript($"T1 is built, logged and pushed, so another machine must start at T2, but it dispatched {b.Dispatches[0].Task}"));
            Assert.DoesNotContain(b.Dispatches, d => d.Task == "T1");
            b.AssertTaskCommitted(b.Web, "T2", webB);

            // The change finishes from machine B: review, approval, pull requests for web and the master.
            var poB = b.Session("po-b", b.Master);
            var folderB = Path.Combine(b.Master, Path.GetRelativePath(plan.Root, plan.Folder));
            ReviewUnlessAlreadyCommitted(b, poB, folderB, 1);
            poB.Turn(poB.Prompt("accept"));
            var prs = b.PullRequests();
            Assert.True(prs.Count == 2, poB.Transcript($"expected pull requests for web and the master, got {prs.Count}: {string.Join(", ", prs.Select(p => p.Repo))}"));
            Assert.Contains(prs, p => p.Repo == "web");
            Assert.Equal(b.Head(b.Web), b.RemoteHead(b.Web));
        }
    }

    /// <summary>R21 (2), R22 (D6): at approval the engineer declines with a reason; a fix, a review and approval follow, then publishing.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_02_Decline
    {
        [Fact]
        public void DeclineAtApproval_SendsTheChangeBackThroughAFix_ReviewAndApproval()
        {
            const string Reason = "shout must trim the name before shouting";
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            var (_, webDev) = BuildApiAndWeb(ws);
            ReviewUnlessAlreadyCommitted(ws, po, plan.Folder, 1);

            // The engineer declines at approval, with a reason. Nothing is published.
            po.Turn(po.Prompt("decline", Reason));
            Assert.Empty(ws.PullRequests());
            Assert.False(ws.RemoteHasChangeBranch(ws.Web), po.Transcript("a declined change must not be published"));

            // The decline becomes a fix, built from inside web, carrying the engineer's reason.
            ws.FixRequested = true;
            var headBeforeFix = ws.Head(ws.Web);
            webDev.Turn(webDev.Prompt("build"));
            Assert.True(ws.FixApplied, webDev.Transcript("after a decline, /hoshin:build in web must dispatch the fix the decline asked for"));
            Assert.Contains(Reason, ws.Dispatches[^1].Prompt, StringComparison.Ordinal);
            Assert.NotEqual(headBeforeFix, ws.Head(ws.Web));
            Assert.Contains("shout-trim.test.mjs", ws.Git(ws.Web, "log", "--name-only", "--format=", $"{headBeforeFix}..HEAD"));

            // The fixed change is reviewed again, then approved, and approving publishes.
            ReviewUnlessAlreadyCommitted(ws, po, plan.Folder, 2);
            po.Turn(po.Prompt("accept"));
            Assert.True(ws.PullRequests().Count == 3, po.Transcript($"after the decline was fixed, reviewed and approved, every touched repository must have a pull request, got {ws.PullRequests().Count}"));
        }
    }

    /// <summary>R21 (3), R10: a workspace in the original's layout, mid-build with T1 logged, migrates and finishes without re-accepting anything.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_03_Migrate
    {
        [Fact]
        public void MigrateAnInFlightChange_ThenHoshinNetFinishesIt_WithoutReacceptingAnything()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var original = OriginalLayout.WriteInFlightChange(ws);

            var migrate = ws.RunCli(ws.Root, "migrate", "--json", "--write");
            Assert.True(migrate.ExitCode == 0, $"`hoshin migrate --json --write` must migrate an in-flight change of the original:\n{migrate.Stdout}\n{migrate.Stderr}");

            // T1 is built and logged, so web's build is T2, and nothing asks for the intent, spec or plan to be accepted again.
            var webDev = ws.Session("web-session", ws.Web);
            var brief = webDev.Prompt("build");
            Assert.True(brief is not null && brief.Contains("build task T2", StringComparison.Ordinal), webDev.Transcript("after migrating, /hoshin:build in web must dispatch T2: the change's documents are accepted and T1 is logged"));
            webDev.Turn(brief);
            ws.AssertTaskCommitted(ws.Web, "T2", webDev);
            var apiDev = ws.Session("api-session", ws.Api);
            apiDev.Turn(apiDev.Prompt("build"));
            Assert.DoesNotContain(ws.Dispatches, d => d.Task == "T1");

            var po = ws.Session("po-session", ws.Master);
            ReviewUnlessAlreadyCommitted(ws, po, original.Folder, 1);
            po.Turn(po.Prompt("accept"));
            foreach (var doc in original.Documents)
            {
                Assert.True(ws.HeadStatusIs(doc, "accepted"), po.Transcript($"{doc.Relative} was accepted before the migration and must stay accepted"));
            }

            Assert.True(ws.PullRequests().Count == 3, po.Transcript($"the migrated change must publish api, web and the master, got {ws.PullRequests().Count}"));
            Assert.DoesNotContain(po.Told.Concat(webDev.Told), t => t.Contains("waits for acceptance", StringComparison.Ordinal));
        }
    }

    /// <summary>R21 (4), B05: <c>/hoshin:start</c> with a one-repository bug hands off to the light track, in the same session, with one change.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_04_StartBug
    {
        [Fact]
        public void Start_HandsABugOffToTheLightTrack_InTheSameSession_WithOneChange()
        {
            const string Bug = "web: greet('  Ada ') keeps the padding around the name; it should trim it";
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.TrimTheGreetingName()));
            var eng = ws.Session("eng-session", ws.Master);

            var triage = eng.Prompt("start", Bug);
            Assert.True(triage is not null, eng.Transcript("/hoshin:start must reach the plugin: the hooks gave the session nothing to triage with"));
            Assert.Empty(ws.ChangeBranches(ws.Master));

            // The model's verdict is the light track; it hands off to fix in the same session.
            var fix = eng.ModelInvokedSkill("fix", Bug);
            Assert.True(fix is not null && fix.Contains("intent.md", StringComparison.Ordinal), eng.Transcript("the hand-off from start must give the light-track brief"));
            eng.Turn(fix);
            Assert.True(ws.ChangeBranches(ws.Master).Length == 1, eng.Transcript($"start and its hand-off must make exactly one change, got: {string.Join(", ", ws.ChangeBranches(ws.Master))}"));
            Assert.True(eng.LastDocument is not null, eng.Transcript("the light-track documents must be written"));
            ws.AssertCommitted(eng.LastDocument!, eng.Transcript("the light-track documents are committed when the turn ends"));
        }
    }

    /// <summary>R21 (4), B05: <c>/hoshin:start</c> with a feature hands off to intent, in the same session, without a second change.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_04_StartFeature
    {
        [Fact]
        public void Start_HandsAFeatureOffToIntent_InTheSameSession()
        {
            const string Feature = "visitors should be able to choose a greeting language, and the api should report usage per language";
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);

            var triage = po.Prompt("start", Feature);
            Assert.True(triage is not null, po.Transcript("/hoshin:start must reach the plugin: the hooks gave the session nothing to triage with"));
            Assert.Empty(ws.ChangeBranches(ws.Master));

            var intent = po.ModelInvokedSkill("intent", Feature);
            Assert.True(intent is not null && intent.Contains("intent", StringComparison.OrdinalIgnoreCase), po.Transcript("the hand-off from start must give the intent brief"));
            Assert.True(ws.ChangeBranches(ws.Master).Length <= 1, po.Transcript($"start and its hand-off must not make a second change, got: {string.Join(", ", ws.ChangeBranches(ws.Master))}"));
        }
    }

    /// <summary>R21 (5), B15: a typed reject closes the change: the rejection is the branch tip, the change's copy is gone, and nothing is issued for it.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_05_Reject
    {
        [Fact]
        public void Reject_ClosesTheChange()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            po.Turn(po.Prompt("intent", ws.Script.Slug));
            var intent = po.LastDocument;
            Assert.True(intent?.FileName == "intent.md", po.Transcript("/hoshin:intent must ask for intent.md to be written"));
            ws.AssertCommitted(intent!, po.Transcript("the intent draft is committed when the turn ends"));
            var copy = intent!.Root;
            Assert.False(AcceptanceWorkspace.PathsEqual(copy, ws.Master), "setup: the change has its own copy of the master");

            po.Turn(po.Prompt("reject"));
            var name = ws.ChangeName(ws.Master);
            var tip = ws.Git(ws.Master, "log", "-1", "--format=%s", $"change/{name}");
            Assert.True(tip.Contains("reject", StringComparison.OrdinalIgnoreCase), po.Transcript($"the rejection must be the change branch's last commit, but it is `{tip}`"));
            Assert.False(Directory.Exists(copy), po.Transcript($"a rejected change's copy must be removed, but {copy} still exists"));

            var after = po.Prompt("spec");
            Assert.True(after is null || !after.Contains("Write it to", StringComparison.Ordinal), po.Transcript("a rejected change must not be given a spec to write"));
        }
    }

    /// <summary>R21 (5), B15: a typed reopen of the accepted spec, with T1 built, keeps T1; re-acceptance builds on to publishing.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_05_Reopen
    {
        [Fact]
        public void ReopenTheSpec_AfterT1IsBuilt_KeepsT1_AndReacceptanceBuildsOn()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            var spec = plan with { Relative = Path.Combine(Path.GetDirectoryName(plan.Relative)!, "spec.md") };
            var apiDev = ws.Session("api-session", ws.Api);
            apiDev.Turn(apiDev.Prompt("build"));
            ws.AssertTaskCommitted(ws.Api, "T1", apiDev);
            var apiHead = ws.Head(ws.Api);

            po.Turn(po.Prompt("reopen", "spec"));
            Assert.True(ws.HeadStatusIs(spec, "draft"), po.Transcript("the engineer's /hoshin:reopen spec must reopen the accepted spec although T1 is built"));

            ws.AuthorAndAccept(po, "spec");
            if (!ws.HeadStatusIs(plan, "accepted"))
            {
                ws.AuthorAndAccept(po, "plan");
            }

            // Building on: T1 stays built, T2 is built, and the change publishes.
            var dispatchedBefore = ws.Dispatches.Count;
            apiDev.Turn(apiDev.Prompt("build"));
            Assert.Equal(apiHead, ws.Head(ws.Api));
            var webDev = ws.Session("web-session", ws.Web);
            webDev.Turn(webDev.Prompt("build"));
            Assert.DoesNotContain(ws.Dispatches.Skip(dispatchedBefore), d => d.Task == "T1");
            ws.AssertTaskCommitted(ws.Web, "T2", webDev);
            ReviewUnlessAlreadyCommitted(ws, po, plan.Folder, 1);
            po.Turn(po.Prompt("accept"));
            Assert.True(ws.PullRequests().Count == 3, po.Transcript($"the reopened and re-accepted change must publish api, web and the master, got {ws.PullRequests().Count}"));
        }
    }

    /// <summary>R21 (6), B14: the light track end to end in a single-repository workspace: fix, accept, build, review, done, one pull request.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_06_LightTrack
    {
        [Fact]
        public void LightTrack_InASingleRepositoryWorkspace_FromFixToOnePullRequest()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.SingleRepoShop(ChangeScripts.TrimTheGreetingName()));
            var dev = ws.Session("dev-session", ws.Master);

            dev.Turn(dev.Prompt("fix", "greet('  Ada ') keeps the padding around the name; it should trim it"));
            var drafted = dev.LastDocument;
            Assert.True(drafted is not null, dev.Transcript("/hoshin:fix must ask for the light-track documents"));
            ws.AssertCommitted(drafted!, dev.Transcript("the light-track documents are committed when the turn ends"));

            dev.Turn(dev.Prompt("fix", "--accept"));
            dev.Turn(dev.Prompt("build"));
            Assert.True(ws.Dispatches.Any(d => d.Task == "T1"), dev.Transcript("after acceptance, /hoshin:build must dispatch T1"));
            var copy = ws.BuildFolder("app");
            ws.AssertTaskCommitted(copy, "T1", dev);

            ReviewUnlessAlreadyCommitted(ws, dev, drafted!.Folder, 1);
            dev.Turn(dev.Prompt("done"));
            var prs = ws.PullRequests();
            Assert.True(prs.Count == 1, dev.Transcript($"a single-repository change opens exactly one pull request, got {prs.Count}"));
            Assert.Equal(ws.Head(copy), ws.RemoteHead(ws.Master));
        }
    }

    /// <summary>R21 (7), R12: a task whose repository is not checked out fails with a message naming it, and nothing is committed in the master.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_07_MissingCheckout
    {
        [Fact]
        public void MultiRepository_ATaskWhoseRepositoryIsNotCheckedOut_FailsNamingIt_AndCommitsNothingInTheMaster()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            var apiDev = ws.Session("api-session", ws.Api);
            apiDev.Turn(apiDev.Prompt("build"));
            ws.AssertTaskCommitted(ws.Api, "T1", apiDev);

            // web is not checked out on this machine.
            var expected = ws.Web;
            ws.RemoveCheckout("web");
            var masterHead = ws.Head(plan.Root);
            var told = po.Prompt("build");
            Assert.True(told is not null && !told.Contains(BeginMarker, StringComparison.Ordinal), po.Transcript("T2 belongs to web, which is not checked out: nothing may be dispatched for it"));
            Assert.True(told.Contains("web", StringComparison.Ordinal) && Mentions(told, expected), po.Transcript($"the message must name web and where it was expected ({expected}):\n{told}"));
            po.EndTurnOnce();
            Assert.Empty(ws.TaskCommits(plan.Root));
            Assert.DoesNotContain(ws.Dispatches, d => d.Task == "T2");
            Assert.Empty(ws.Git(plan.Root, "log", "--format=%H", "--grep", "Plan-Task: ", $"{masterHead}..HEAD"));
        }
    }

    /// <summary>R21 (7), R12: a <c>build.start</c> refused on a dirty repository issues no task, and <c>main</c> gains no commit.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_07_DirtyRepository
    {
        [Fact]
        public void MultiRepository_ABuildStartRefusedOnADirtyRepository_IssuesNoTask()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAcceptAll(po);

            // The engineer has an uncommitted edit to a tracked file in api.
            ChangeScripts.Write(ws.Api, "src/calc.mjs", "export const add = (a, b) => a + b; // checking something\n");
            var mainBefore = ws.Git(ws.Api, "rev-parse", "main");
            var apiDev = ws.Session("api-session", ws.Api);
            var told = apiDev.Prompt("build");
            Assert.True(told is not null, apiDev.Transcript("a refused build.start must be explained to the engineer, not met with silence"));
            Assert.True(!told.Contains(BeginMarker, StringComparison.Ordinal), apiDev.Transcript("build.start refused on a dirty repository: no task may be issued"));
            apiDev.EndTurnOnce();
            Assert.Equal(mainBefore, ws.Git(ws.Api, "rev-parse", "main"));
            Assert.Empty(ws.Git(ws.Api, "log", "--all", "--format=%H", "--grep", "Plan-Task: "));
            Assert.Empty(ws.Dispatches);
        }
    }

    /// <summary>R21 (8), R11: the engineer commits by hand during attempt 1; attempt 2 starts from the task's start and passes.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_08_StrayCommit
    {
        [Fact]
        public void AStrayCommitDuringAttempt1_DoesNotBurnEveryAttempt()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAcceptAll(po);

            string? stray = null;
            ws.RejectionsExpected = true;
            ws.AfterTask = (repo, _, attempt) =>
            {
                if (attempt == 1 && stray is null)
                {
                    ws.Git(repo, "add", "-A");
                    ws.Git(repo, "commit", "-q", "-m", "wip: shout");
                    stray = ws.Head(repo);
                }
            };

            var webDev = ws.Session("web-session", ws.Web);
            webDev.Turn(webDev.Prompt("build"));
            Assert.NotNull(stray);
            var attempts = ws.Dispatches.Where(d => d.Task == "T2").Select(d => d.Attempt).ToList();
            Assert.True(attempts.SequenceEqual([1, 2]), webDev.Transcript($"attempt 1 fails because the engineer committed by hand; attempt 2 must start from T2's start and pass, but the attempts were [{string.Join(", ", attempts)}]"));
            ws.AssertTaskCommitted(ws.Web, "T2", webDev);
            Assert.DoesNotContain("wip: shout", ws.Git(ws.Web, "log", "--format=%s", "main..HEAD"), StringComparison.Ordinal);
            Assert.True(webDev.Told.Any(t => t.Contains(stray![..7], StringComparison.Ordinal)), webDev.Transcript($"the verdict must name the commit it moved past ({stray![..7]}), so nothing is lost"));
        }
    }

    /// <summary>R21 (9), R25: a co-located test, with no <c>testPatterns</c>, is found and the task is accepted on its first attempt.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_09_CoLocatedTests(ITestOutputHelper output)
    {
        [Theory]
        [InlineData("node-spec")]
        [InlineData("node-tests-folder")]
        [InlineData("python")]
        [InlineData("go")]
        public void CoLocatedTests_AreFoundByDefault_AndTheTaskIsAcceptedOnItsFirstAttempt(string language)
        {
            var lib = CoLocated.For(language);
            if (lib.MissingToolchain is { } missing)
            {
                Assert.False(Environment.GetEnvironmentVariable("CI") is { Length: > 0 }, $"{missing} is missing on this CI runner; the co-located {language} case must run in CI");
                output.WriteLine($"skipped: {missing} is not installed on this machine");
                return;
            }

            using var ws = AcceptanceWorkspace.Create(lib.Workspace);
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAcceptAll(po);
            var dev = ws.Session("lib-session", ws.Repo("lib"));
            ws.RejectionsExpected = true;
            dev.Turn(dev.Prompt("build"));

            var attempts = ws.Dispatches.Where(d => d.Task == "T1").Select(d => d.Attempt).ToList();
            Assert.True(attempts.SequenceEqual([1]), dev.Transcript($"`{lib.TestFile}` is a test by the usual convention of its language; T1 must be accepted on its first attempt, but the attempts were [{string.Join(", ", attempts)}]"));
            ws.AssertTaskCommitted(ws.Repo("lib"), "T1", dev);
            var testCommit = ws.Git(ws.Repo("lib"), "log", "--reverse", "--format=%H", "--grep", "Plan-Task: T1", "main..HEAD").Split('\n')[0].Trim();
            Assert.Contains(lib.TestFile, ws.Git(ws.Repo("lib"), "show", "--name-only", "--format=", testCommit), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R21 (10), R23 (S16): while the master session writes the review, a product session with nothing left and a second
    /// master session both type a command. Neither may take the run over: each is told who owns it, and the first
    /// session's review is recorded.
    /// </summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_10_TwoSessions
    {
        [Fact]
        public void TwoSessions_NoOtherSessionTakesOverTheMastersReview_AndTheReviewIsRecorded()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            var (apiDev, _) = BuildApiAndWeb(ws);
            Assert.False(ws.ReviewCommitted(plan.Folder, 1), po.Transcript("setup: review 1 is written from the master session, after both builds"));

            var brief = po.Prompt("review");
            Assert.True(brief is not null && brief.Contains("Review 1", StringComparison.Ordinal), po.Transcript("/hoshin:review in the master must ask for review 1"));

            // Meanwhile the engineer types /hoshin:build in api, whose tasks are all built.
            var told = apiDev.Prompt("build");
            Assert.True(told is not null && !told.Contains(BeginMarker, StringComparison.Ordinal), apiDev.Transcript("api has nothing left to build: it must be told what comes next, and nothing may be dispatched"));

            // And opens a second terminal in the master and types /hoshin:review again.
            var other = ws.Session("po-second-terminal", ws.Master);
            var second = other.Prompt("review");
            Assert.True(second is not null, other.Transcript("a second session asking for a step another session owns must be told who owns it, not met with silence"));
            Assert.True(!second.Contains("Write the review section", StringComparison.Ordinal), other.Transcript("the review belongs to po-session's run: a second session must not be given the same review to write"));

            po.Turn(brief);
            Assert.True(ws.ReviewCommitted(plan.Folder, 1), po.Transcript("the master's review must be recorded although other sessions typed commands meanwhile"));
        }
    }

    /// <summary>R21 (11), B13: <c>/hoshin:done</c> on a two-repository change reports every pull request URL.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class R21_11_Done
    {
        [Fact]
        public void Done_OnATwoRepositoryChange_ReportsEveryPullRequestUrl()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            BuildApiAndWeb(ws);
            ReviewUnlessAlreadyCommitted(ws, po, plan.Folder, 1);

            var toldBefore = po.Told.Count;
            po.Turn(po.Prompt("done"));
            var prs = ws.PullRequests();
            Assert.True(prs.Count == 3, po.Transcript($"expected one pull request per touched repository (api, web, master), got {prs.Count}"));
            var told = string.Join("\n", po.Told.Skip(toldBefore));
            foreach (var pr in prs)
            {
                Assert.True(told.Contains(pr.Url, StringComparison.Ordinal), po.Transcript($"the engineer must be told every pull request; {pr.Repo}'s {pr.Url} is missing from:\n{told}"));
            }
        }
    }

    // ---------------------------------------------------------------- shared steps

    private static (Session Api, Session Web) BuildApiAndWeb(AcceptanceWorkspace ws)
    {
        var apiDev = ws.Session("api-session", ws.Api);
        apiDev.Turn(apiDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Api, "T1", apiDev);
        var webDev = ws.Session("web-session", ws.Web);
        webDev.Turn(webDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Web, "T2", webDev);
        return (apiDev, webDev);
    }

    private static void ReviewUnlessAlreadyCommitted(AcceptanceWorkspace ws, Session master, string changeFolder, int number)
    {
        if (!ws.ReviewCommitted(changeFolder, number))
        {
            master.Turn(master.Prompt("review"));
        }

        Assert.True(ws.ReviewCommitted(changeFolder, number), master.Transcript($"review {number} must be written and committed"));
    }

    private static bool Mentions(string text, string path) =>
        text.Contains(path, StringComparison.OrdinalIgnoreCase)
        || text.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- R21 (3): the original's layout

    /// <summary>
    /// An in-flight change exactly as the original hoshin plugin leaves it (D:\workspaces\hoshin, its eval fixtures and
    /// <c>tools/changes/record.ts</c>): the change's copy of the master at <c>.hoshin/copies/&lt;change&gt;</c> on its branch,
    /// <c>change.yml</c>, YAML front matter, YAML task blocks, every document accepted with its pin holding, T1 built in
    /// api with its <c>Plan-Task</c> trailer and logged in a YAML build log, and T2 still to build.
    /// </summary>
    private static class OriginalLayout
    {
        public sealed record InFlight(string Folder, IReadOnlyList<DocumentRef> Documents);

        public static InFlight WriteInFlightChange(AcceptanceWorkspace ws)
        {
            const string Change = "2026-11-20-add-shout";
            var copy = Path.Combine(ws.Root, ".hoshin", "copies", Change);
            ws.Git(ws.Master, "worktree", "add", "-q", "-b", $"change/{Change}", copy);
            var rel = $"intent/shop/{Change}";
            var folder = Path.Combine(copy, rel);
            void Commit(string message, params string[] files)
            {
                ws.Git(copy, ["add", "--", .. files]);
                ws.Git(copy, "commit", "-q", "-m", message, "-m", "Generated-By: hoshin");
            }

            ChangeScripts.Write(folder, "change.yml", $"""
                # Written by the hoshin program (change start, change rename, change.done); do not edit by hand.
                version: 1
                branch: "change/{Change}"
                type: feat
                scope: "shop"
                base: "main"
                """);
            ChangeScripts.Write(folder, "intent.md", """
                ---
                title: Shout greeting
                system: shop
                author: Pat Owner, Product Owner
                date: 2026-11-20
                status: accepted
                scope: shop
                ---

                # Intent: Shout greeting

                ## Problem

                Callers cannot emphasize a greeting, and the calculator cannot multiply.

                ## Proposed outcome

                The web greeting can shout, and the api calculator can multiply.

                ## Affected users and systems

                - **Users:** web visitors and api clients.
                - **Systems:** api, web.

                ## Constraints

                - Existing greetings and sums are unchanged.

                ## Decisions

                | # | Situation | Decision | What should happen |
                |---|---|---|---|
                | D1 | Multiplying | handle it | The api calculator multiplies two numbers. |
                | D2 | Shouting | handle it | A shouted greeting ends with `!`. |

                ## Open questions

                - None.
                """);
            Commit($"docs(shop): draft intent {Change}", $"{rel}/change.yml", $"{rel}/intent.md");
            var intentCommit = ws.Git(copy, "rev-parse", "HEAD");

            ChangeScripts.Write(folder, "spec.md", $"""
                ---
                layout: 2
                source_intent: {Change}/intent.md
                source_intent_commit: {intentCommit}
                system: shop
                date: 2026-11-20
                status: accepted
                policies_applied: []
                policies_not_checked: [security, compliance, brand, UX]
                ---

                # Spec: Shout greeting

                ## Requirements

                ```gherkin
                {ChangeScripts.ShoutGreetingRequirements.Replace("\n", "\n                ", StringComparison.Ordinal)}
                ```

                ## Design

                The calculator gains `multiply`, and the greeting gains `shout`.

                ## Areas of concern

                1. **No policy skills were applied.** Owner: product owner.
                """);
            Commit($"docs(shop): write spec {Change}", $"{rel}/spec.md");
            var specCommit = ws.Git(copy, "rev-parse", "HEAD");

            ChangeScripts.Write(folder, "plan.md", $"""
                ---
                layout: 2
                source_spec: {Change}/spec.md
                source_spec_commit: {specCommit}
                system: shop
                date: 2026-11-20
                author: Eve Engineer
                status: accepted
                ---

                # Plan: Shout greeting

                ## Approach

                T1 multiplies in api, and T2 shouts in web.

                ## Scenario coverage

                | Scenario | Test | Task |
                |---|---|---|
                | R1.1 | multiply(3, 4) is 12 | T1 |
                | R2.1 | shout('Ada') is 'Hello, Ada!' | T2 |

                ## Shared decisions

                ```yaml
                []
                ```

                ## Tasks

                ### T1: Multiply in the calculator

                ```yaml
                id: T1
                repo: api
                depends_on: []
                uses: []
                files: [src/calc.mjs, test/multiply.test.mjs]
                covers: [R1.1]
                supports: []
                compile: [node, build.mjs]
                test: [node, --test, test/multiply.test.mjs]
                ```

                #### Change

                Add `multiply(a, b)` to `src/calc.mjs`.

                #### Tests

                `test/multiply.test.mjs` checks `multiply(3, 4) === 12` against the built `dist/calc.mjs`.

                #### Done when

                `node --test test/multiply.test.mjs` passes.

                ### T2: Shout the greeting

                ```yaml
                id: T2
                repo: web
                depends_on: []
                uses: []
                files: [src/greet.mjs, test/shout.test.mjs]
                covers: [R2.1]
                supports: []
                test: [node, node_modules/testkit/run.mjs, test/shout.test.mjs]
                ```

                #### Change

                Add `shout(name)` to `src/greet.mjs`.

                #### Tests

                `test/shout.test.mjs` checks `shout('Ada') === 'Hello, Ada!'`.

                #### Done when

                `node node_modules/testkit/run.mjs test/shout.test.mjs` passes.

                ## Risks

                - **What could break:** existing sums and greetings, which their tests protect.
                """);
            Commit($"docs(shop): write plan {Change}", $"{rel}/plan.md");

            // T1, built in api on the change's branch by the original's build, then logged.
            ws.Git(ws.Api, "switch", "-q", "-c", $"change/{Change}");
            ChangeScripts.MultiplyInApi(ws.Api);
            ws.Git(ws.Api, "add", "--", "src/calc.mjs", "test/multiply.test.mjs");
            ws.Git(ws.Api, "commit", "-q", "-m", "feat(api): multiply in the calculator", "-m", "Plan-Task: T1\nGenerated-By: hoshin");
            var built = ws.Git(ws.Api, "rev-parse", "--short", "HEAD");
            ChangeScripts.Write(folder, "build/api.md", $"""
                ---
                layout: 2
                ---

                # Build log: api

                ### T1

                ```yaml
                task: T1
                commits: [{built}]
                ```
                """);
            Commit($"docs(shop): log T1 of {Change}", $"{rel}/build/api.md");

            var documents = new[] { "intent.md", "spec.md", "plan.md" }.Select(f => new DocumentRef(copy, $"{rel}/{f}")).ToList();
            return new InFlight(folder, documents);
        }
    }

    // ---------------------------------------------------------------- R21 (9): co-located tests

    private sealed record CoLocated(WorkspaceSpec Workspace, string TestFile, string? MissingToolchain)
    {
        public static CoLocated For(string language) => language switch
        {
            "node-spec" => Node("src/x.spec.mjs", "./x.mjs"),
            "node-tests-folder" => Node("__tests__/x.test.mjs", "../src/x.mjs"),
            "python" => Python(),
            "go" => Go(),
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
        };

        private static CoLocated Node(string testFile, string import) => Build(
            new Dictionary<string, string>
            {
                ["package.json"] = "{ \"name\": \"lib\", \"type\": \"module\", \"private\": true }\n",
                ["src/x.mjs"] = "export const shout = (s) => s;\n",
            },
            testFile,
            ["node", "--test", testFile],
            root =>
            {
                ChangeScripts.Write(root, testFile, $"""
                    import test from 'node:test';
                    import assert from 'node:assert/strict';
                    import {"{"} shout {"}"} from '{import}';
                    test('shout', () => {"{"} assert.equal(shout('hi'), 'HI!'); {"}"});
                    """);
                ChangeScripts.Write(root, "src/x.mjs", "export const shout = (s) => `${s.toUpperCase()}!`;\n");
            },
            missing: null);

        private static CoLocated Python()
        {
            var python = new[] { "python3", "python" }.FirstOrDefault(Runs);
            const string TestFile = "app/test_x.py";
            return Build(
                new Dictionary<string, string>
                {
                    [".gitignore"] = "__pycache__/\n",
                    ["app/x.py"] = "def shout(s):\n    return s\n",
                },
                TestFile,
                [python ?? "python3", "-m", "unittest", TestFile],
                root =>
                {
                    ChangeScripts.Write(root, TestFile, """
                        import unittest

                        from app.x import shout


                        class ShoutTest(unittest.TestCase):
                            def test_shout(self):
                                self.assertEqual(shout("hi"), "HI!")
                        """);
                    ChangeScripts.Write(root, "app/x.py", "def shout(s):\n    return s.upper() + \"!\"\n");
                },
                missing: python is null ? "python" : null);
        }

        private static CoLocated Go()
        {
            const string TestFile = "greet/greet_test.go";
            return Build(
                new Dictionary<string, string>
                {
                    ["go.mod"] = "module example.com/lib\n\ngo 1.21\n",
                    ["greet/greet.go"] = "package greet\n\n// Shout returns the text as it is.\nfunc Shout(s string) string { return s }\n",
                },
                TestFile,
                ["go", "test", "./greet/"],
                root =>
                {
                    ChangeScripts.Write(root, TestFile, """
                        package greet

                        import "testing"

                        func TestShout(t *testing.T) {
                        	if got := Shout("hi"); got != "HI!" {
                        		t.Fatalf("Shout(%q) = %q, want %q", "hi", got, "HI!")
                        	}
                        }
                        """);
                    ChangeScripts.Write(root, "greet/greet.go", "package greet\n\nimport \"strings\"\n\n// Shout returns the text in capitals, with an exclamation mark.\nfunc Shout(s string) string { return strings.ToUpper(s) + \"!\" }\n");
                },
                missing: Runs("go") ? null : "go");
        }

        private static CoLocated Build(Dictionary<string, string> files, string testFile, string[] test, Action<string> doTask, string? missing)
        {
            var script = new ChangeScript
            {
                Slug = "shout-lib",
                Title = "Shout in the library",
                Problem = "The library cannot shout.",
                Outcome = "`shout` returns the text in capitals with an exclamation mark.",
                Users = "Library callers.",
                Constraints = "None.",
                Decisions = "D1: a shout is the text in capitals followed by `!`.",
                Requirements = """
                    Feature: Shout in the library
                      @R1 @from:D1
                      Rule: Shouting capitalizes and exclaims
                        @R1.1
                        Scenario: Shouting hi
                          Given the text "hi"
                          When it is shouted
                          Then the result is "HI!"
                    """,
                Tasks =
                [
                    new PlanTask("T1", "lib", "Shout", "Make `shout` return the text in capitals with `!`.", $"`{testFile}` checks that shouting \"hi\" gives \"HI!\".", test, doTask)
                    {
                        Covers = ["R1.1"],
                    },
                ],
            };
            var workspace = new WorkspaceSpec(script, [new RepoSpec("lib", files)]) { Check = test };
            return new CoLocated(workspace, testFile, missing);
        }

        private static bool Runs(string tool)
        {
            try
            {
                var r = Unit.TestServices.CommandRunner.Run(new CommandRequest(Path.GetTempPath(), [tool, tool == "go" ? "version" : "--version"]));
                return r.ExitCode == 0;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return false;
            }
        }
    }
}
