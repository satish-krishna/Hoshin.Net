using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Tests.Acceptance.Harness;
using Xunit;

namespace Hoshin.Tests.Acceptance.Parity;

/// <summary>
/// B03 parity tests for the lifecycle group (evals/README.md, "Where each original case went"). Written by the
/// reviewer, not the implementer. Each one ports what an original eval case guaranteed about the engine to the way the
/// acceptance gate drives it: shipped <c>hoshin.dll</c> hooks, typed-user payloads for the human-only steps, and
/// assertions on what an engineer can see (commits, files, branches, remote refs, pull requests, what the session was told).
/// </summary>
public static class LifecycleParityTests
{
    private const string BeginMarker = AcceptanceWorkspace.BeginMarker;

    /// <summary>
    /// Row 1, original <c>build-commits-checked-entry</c>, task B11 (item 5): a task committed with its
    /// <c>Plan-Task</c> trailer but not logged is adopted. Its log entry is committed in the change's copy of the master,
    /// only the build log is in that commit, the product repository gains nothing, and the task is not issued again.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "build-commits-checked-entry")]
    public sealed class BuildCommitsCheckedEntry
    {
        [Fact]
        public void ABuiltButUnloggedTask_GetsItsLogEntryCommittedOnlyInTheChangeCopy_AndIsNotIssuedAgain()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            var dev = ws.Session("api-session", ws.Api);

            // The engineer starts T1 in api, builds it and commits it by hand through the program, test first, with the trailer. Nothing logs it.
            var issued = dev.Prompt("build");
            Assert.True(issued is not null && issued.Contains(BeginMarker, StringComparison.Ordinal), dev.Transcript("setup: /hoshin:build in api must issue T1"));
            var change = ws.ChangeName(ws.Api);
            ChangeScripts.MultiplyInApi(ws.Api);
            ws.CommitTaskWork("api", change, "T1", "test", "multiply test", "test/multiply.test.mjs");
            var built = ws.CommitTaskWork("api", change, "T1", "feat", "multiply", "src/calc.mjs");
            var copyHead = ws.Head(plan.Root);
            var masterMain = ws.Git(ws.Master, "rev-parse", "main");

            var told = dev.Prompt("build");
            Assert.False(
                told is not null && told.Contains(BeginMarker, StringComparison.Ordinal),
                dev.Transcript("T1 is built and committed with its Plan-Task trailer: /hoshin:build must log it, not issue it again"));
            dev.EndTurnOnce();

            ws.AssertBuildLogHas(plan.Folder, "api", "T1", dev);
            Assert.NotEqual(copyHead, ws.Head(plan.Root));
            var logCommit = ws.Subjects(plan.Root, $"{copyHead}..HEAD");
            Assert.True(logCommit.Length == 1, dev.Transcript($"exactly one commit is made in the change's copy, the log entry; got: {string.Join(" | ", logCommit)}"));
            Assert.Contains("log T1", logCommit[0], StringComparison.Ordinal);
            var relativeLog = Path.GetRelativePath(plan.Root, Path.Combine(plan.Folder, "build", "api.md")).Replace('\\', '/');
            Assert.Equal(new[] { relativeLog }, ws.FilesOf(plan.Root, "HEAD"));
            Assert.Equal(built, ws.Head(ws.Api));
            Assert.Equal(masterMain, ws.Git(ws.Master, "rev-parse", "main"));
            Assert.Empty(ws.Dispatches);
        }
    }

    /// <summary>
    /// Row 2, original <c>build-logs-in-the-change-copy</c>, task B11: the build log lands in the change's copy, and the
    /// engineer's checkout of the master (on <c>main</c>) keeps its branch, its HEAD and a clean tree. <c>--master</c>
    /// itself is an accepted difference; resolution finds the copy.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "build-logs-in-the-change-copy")]
    public sealed class BuildLogsInTheChangeCopy
    {
        [Fact]
        public void TheLogEntryIsCommittedInTheChangeCopy_AndTheEngineersCheckoutIsUntouched()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            Assert.False(AcceptanceWorkspace.PathsEqual(plan.Root, ws.Master), "setup: the change has its own copy of the master");
            var branchBefore = ws.Git(ws.Master, "branch", "--show-current");
            var headBefore = ws.Git(ws.Master, "rev-parse", "HEAD");
            var reflogBefore = ws.Git(ws.Master, "reflog", "-n", "50");

            var apiDev = ws.Session("api-session", ws.Api);
            apiDev.Turn(apiDev.Prompt("build"));
            ws.AssertTaskCommitted(ws.Api, "T1", apiDev);

            ws.AssertBuildLogHas(plan.Folder, "api", "T1", apiDev);
            Assert.Equal("main", branchBefore);
            Assert.Equal(branchBefore, ws.Git(ws.Master, "branch", "--show-current"));
            Assert.Equal(headBefore, ws.Git(ws.Master, "rev-parse", "HEAD"));
            Assert.Equal(reflogBefore, ws.Git(ws.Master, "reflog", "-n", "50"));
            Assert.True(ws.IsClean(ws.Master), apiDev.Transcript("the engineer's checkout of the master must have no staged, modified or untracked file after a build"));
            Assert.False(Directory.Exists(Path.Combine(ws.Master, "intent", "shop", ws.ChangeName(ws.Master))), "the change's folder, and its build log, live in the copy, not in the engineer's checkout");
            Assert.Empty(ws.Git(ws.Master, "log", "main", "--format=%s", "--grep", "log T1"));
        }
    }

    /// <summary>
    /// Row 3, original <c>build-prints-the-schedule</c>, task B11 (item 6): with nothing left in the session's repository,
    /// the message comes from the schedule. It names the ready repositories and the waiting one with what it waits on.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "build-prints-the-schedule")]
    public sealed class BuildPrintsTheSchedule
    {
        [Fact]
        public void WithNothingLeftInTheSessionsRepository_TheMessageNamesTheReadyAndTheWaitingRepositories_AndTheirBlockers()
        {
            using var ws = AcceptanceWorkspace.Create(LifecycleParityHarness.ScheduleWorkspace());
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAcceptAll(po);
            var core = ws.Session("core-session", ws.Repo("core"));
            core.Turn(core.Prompt("build"));
            ws.AssertTaskCommitted(ws.Repo("core"), "T1", core);

            var told = core.Prompt("build");

            Assert.True(told is not null, core.Transcript("core has nothing left to build: it must be told what comes next"));
            Assert.DoesNotContain(BeginMarker, told, StringComparison.Ordinal);
            Assert.True(
                Regex.IsMatch(told, @"\bapi\b") && Regex.IsMatch(told, @"\bweb\b"),
                core.Transcript($"api (T2) and web (T3, whose T1 is logged) can be built now, so the message must name both:\n{told}"));
            var docsClause = Regex.Match(told, @"\bdocs\b[^.;\n]*");
            Assert.True(docsClause.Success, core.Transcript($"docs waits for T2 and the message must name it, not only the ready repositories:\n{told}"));
            Assert.True(
                docsClause.Value.Contains("T2", StringComparison.Ordinal) && Regex.IsMatch(docsClause.Value, "wait|blocked", RegexOptions.IgnoreCase),
                core.Transcript($"the message must say docs waits for T2, got: `{docsClause.Value}`"));
        }
    }

    /// <summary>
    /// Row 4, original <c>build-records-red</c>, tasks B11 and R24: the engine observes red itself. The model never runs
    /// <c>red record</c>, and the record the engine keeps is the real run: <c>red show</c> prints the failing test's own
    /// output, not a made-up exit code and the verdict's summary.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "build-records-red")]
    public sealed class BuildRecordsRed
    {
        [Fact]
        public void TheEngineObservesRedItself_AndRecordsTheRealRunOfTheFailingTest()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAcceptAll(po);
            var apiDev = ws.Session("api-session", ws.Api);
            apiDev.Turn(apiDev.Prompt("build"));
            ws.AssertTaskCommitted(ws.Api, "T1", apiDev);
            var subjects = ws.Subjects(ws.Api, "main..HEAD").Where(s => s.StartsWith("test(", StringComparison.Ordinal)).ToList();
            Assert.True(subjects.Count == 1, apiDev.Transcript($"the failing test is committed first, on its own: {string.Join(" | ", subjects)}"));

            var change = ws.ChangeName(ws.Api);
            var show = ws.RunCli(ws.Api, "red", "show", "--json", "--change", change, "--repo", "api", "--task", "T1");
            Assert.True(show.ExitCode == 0, $"`hoshin red show` must work for T1 after its build:\n{show.Stdout}\n{show.Stderr}");
            using var json = JsonDocument.Parse(show.Stdout);
            Assert.True(json.RootElement.GetProperty("observed").GetBoolean(), apiDev.Transcript($"T1's red must be observed after the engine's own run:\n{show.Stdout}"));
            var record = json.RootElement.GetProperty("record");
            Assert.True(record.ValueKind == JsonValueKind.Object, $"red show must report the record the engine kept:\n{show.Stdout}");
            Assert.Equal("failed", record.GetProperty("outcome").GetString());
            var output = record.GetProperty("output").GetString() ?? "";
            Assert.True(
                output.Contains("multiply", StringComparison.Ordinal),
                $"the record must hold the failing test's real output (the missing `multiply`), not a summary the engine made up; it holds:\n{output}");
        }
    }

    /// <summary>
    /// Row 5, original <c>build-refuses-a-stranger-master</c>, task B11 (item 7): a second clone of the master in the
    /// workspace is never taken as the master. The build stops and says why, nothing is issued, and neither clone gains a commit.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "build-refuses-a-stranger-master")]
    public sealed class BuildRefusesAStrangerMaster
    {
        [Fact]
        public void ASecondCloneOfTheMasterInTheWorkspace_IsNeverTakenAsTheMaster()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);

            // Someone clones the master a second time, next to the first.
            var stranger = Path.Combine(ws.Root, "ai-sdlc-clone");
            ws.Git(ws.Root, "clone", "-q", Path.Combine(ws.Remotes, "ai-sdlc.git"), stranger);
            var strangerHead = ws.Git(stranger, "rev-parse", "HEAD");
            var masterHead = ws.Git(ws.Master, "rev-parse", "HEAD");
            var copyHead = ws.Head(plan.Root);
            var apiHead = ws.Head(ws.Api);

            var apiDev = ws.Session("api-session", ws.Api);
            var told = apiDev.Prompt("build");
            apiDev.EndTurnOnce();

            Assert.True(told is not null, apiDev.Transcript("two masters make the workspace ambiguous: the build must say why it stops, not stay silent"));
            Assert.DoesNotContain(BeginMarker, told, StringComparison.Ordinal);
            Assert.True(
                LifecycleParityHarness.Mentions(told, "ai-sdlc-clone") || told.Contains("more than one master", StringComparison.OrdinalIgnoreCase),
                apiDev.Transcript($"the message must name the second clone or say that there is more than one master:\n{told}"));
            Assert.Empty(ws.Dispatches);
            Assert.Equal(strangerHead, ws.Git(stranger, "rev-parse", "HEAD"));
            Assert.False(Directory.Exists(Path.Combine(stranger, "intent", "shop", ws.ChangeName(ws.Master), "build")), "the stranger clone must gain no build log");
            Assert.Equal(masterHead, ws.Git(ws.Master, "rev-parse", "HEAD"));
            Assert.Equal(copyHead, ws.Head(plan.Root));
            Assert.Equal(apiHead, ws.Head(ws.Api));
            Assert.Empty(ws.TaskCommits(ws.Api));
        }
    }

    /// <summary>
    /// Row 6, original <c>done-refuses-an-unreviewed-change</c>, task B13: a typed <c>done</c> on a built change with no
    /// review refuses before anything leaves the machine, and says the review comes first.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "done-refuses-an-unreviewed-change")]
    public sealed class DoneRefusesAnUnreviewedChange
    {
        [Fact]
        public void TypedDone_OnABuiltChangeWithNoReview_RefusesBeforeAnythingIsPushed_AndSaysReviewFirst()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting(findingInReview1: false)));
            var po = ws.Session("po-session", ws.Master);
            var plan = ws.AuthorAndAcceptAll(po);
            ws.BuildApiAndWeb();
            Assert.False(ws.ReviewCommitted(plan.Folder, 1), "setup: the change is built and has no review");
            var apiHead = ws.Head(ws.Api);
            var webHead = ws.Head(ws.Web);
            var copyHead = ws.Head(plan.Root);

            var told = po.Prompt("done");

            Assert.True(told is not null, po.Transcript("a refused done must be explained to the engineer, not met with silence"));
            Assert.Contains("review", told, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(ws.PullRequests());
            Assert.False(ws.RemoteHasChangeBranch(ws.Api), po.Transcript("nothing may be pushed for a change that has no review"));
            Assert.False(ws.RemoteHasChangeBranch(ws.Web), po.Transcript("nothing may be pushed for a change that has no review"));
            Assert.False(ws.RemoteHasChangeBranch(plan.Root), po.Transcript("the master's change branch must not be pushed either"));
            Assert.Equal(apiHead, ws.Head(ws.Api));
            Assert.Equal(webHead, ws.Head(ws.Web));
            Assert.Equal(copyHead, ws.Head(plan.Root));
            Assert.False(ws.ReviewCommitted(plan.Folder, 1), "a refused done must not write the review itself");
        }
    }

    /// <summary>
    /// Row 30 (parity half), original <c>intent-keeps-scope-as-typed</c>, task B06 (item 1): a <c>scope:</c> argument is
    /// kept exactly as typed, separate from the system and from the change's name, in the brief, the intent's front
    /// matter and the draft commit's subject.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "intent-keeps-scope-as-typed")]
    public sealed class IntentKeepsScopeAsTyped
    {
        [Fact]
        public void AScopeArgument_IsKeptAsTyped_InTheBrief_TheFrontMatterAndTheCommitSubject_AndNotSluggedIntoTheName()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);

            var brief = po.Prompt("intent", $"scope:PROJ-123 {ws.Script.Slug}");

            Assert.True(brief is not null, po.Transcript("/hoshin:intent must reach the plugin"));
            Assert.True(
                Regex.IsMatch(brief, "\"scope\"\\s*:\\s*\"PROJ-123\""),
                po.Transcript($"the brief's front matter must carry the scope exactly as typed, `PROJ-123`, not the system's:\n{brief}"));
            po.Turn(brief);
            var intent = po.LastDocument;
            Assert.True(intent?.FileName == "intent.md", po.Transcript("the intent must be written"));
            ws.AssertCommitted(intent!, po.Transcript("the intent draft is committed when the turn ends"));

            var change = ws.ChangeName(ws.Master);
            Assert.DoesNotContain("proj-123", change, StringComparison.Ordinal);
            Assert.DoesNotContain("scope", change, StringComparison.Ordinal);
            var subject = ws.Git(ws.Master, "log", "-1", "--format=%s", $"change/{change}");
            Assert.StartsWith("docs(PROJ-123): draft intent", subject, StringComparison.Ordinal);
            var committed = ws.Git(intent!.Root, "show", $"HEAD:{intent.Relative}");
            Assert.Matches("\"scope\"\\s*:\\s*\"PROJ-123\"", committed);
            Assert.DoesNotContain("docs(proj-123)", ws.Git(ws.Master, "log", "--all", "--format=%s"), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Row 31, original <c>intent-rejects-a-change</c>, task B15: a typed reject of an accepted intent nothing was built
    /// from commits the rejection on the change's branch, removes the copy, keeps the branch, and accepts nothing more.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "intent-rejects-a-change")]
    public sealed class IntentRejectsAChange
    {
        [Fact]
        public void TypedReject_OfAnAcceptedIntent_CommitsOnTheChangeBranch_RemovesTheCopy_AndAcceptsNothingMore()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            var intent = ws.AuthorAndAccept(po, "intent");
            var copy = intent.Root;
            Assert.False(AcceptanceWorkspace.PathsEqual(copy, ws.Master), "setup: the change has its own copy of the master");
            var change = ws.ChangeName(ws.Master);
            var mainBefore = ws.Git(ws.Master, "rev-parse", "main");

            var told = po.Prompt("reject");
            po.Turn(told);

            var subjects = ws.Subjects(ws.Master, $"main..change/{change}");
            var all = string.Join(" | ", subjects);
            Assert.True(subjects.Length >= 3, po.Transcript($"the branch records the draft, the acceptance and the rejection; got: {all}"));
            Assert.Contains("reject", subjects[^1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("accept intent", subjects[^2], StringComparison.Ordinal);
            Assert.Contains("draft intent", subjects[^3], StringComparison.Ordinal);
            Assert.DoesNotContain(subjects, s => s.Contains("spec", StringComparison.OrdinalIgnoreCase) || s.Contains("plan", StringComparison.OrdinalIgnoreCase));
            Assert.True(subjects.Count(s => s.Contains("accept", StringComparison.Ordinal)) == 1, po.Transcript($"only the fixture's own acceptance of the intent is on the branch; got: {all}"));
            var tip = ws.Git(ws.Master, "show", $"change/{change}:{intent.Relative}");
            Assert.Matches("\"status\"\\s*:\\s*\"rejected\"", tip);
            Assert.False(Directory.Exists(copy), po.Transcript($"a rejected change's copy must be removed, but {copy} still exists"));
            Assert.Equal(mainBefore, ws.Git(ws.Master, "rev-parse", "main"));
            Assert.True(ws.IsClean(ws.Master), po.Transcript("the engineer's checkout must stay clean"));
        }
    }

    /// <summary>
    /// Row 35, original <c>plan-gives-up-after-three</c>, task B10: the engineer insists on a repository the system does
    /// not list, so the plan check can never pass. Stop blocks three times, then gives up; nothing is committed and the
    /// draft stays on disk.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "plan-gives-up-after-three")]
    public sealed class PlanGivesUpAfterThree
    {
        [Fact]
        public void APlanNamingARepositoryTheSystemDoesNotList_IsBlockedThreeTimes_ThenGivenUp_WithNothingCommitted() =>
            AssertGivesUpAfterThree(LifecycleParityHarness.PlanInAnUnlistedRepository(), "legacy-portal");

        /// <summary>The same loop with a refusal the plan check gives today, so the cap and the commit-nothing guarantee are pinned apart from the repository check.</summary>
        [Fact]
        public void APlanThatKeepsFailingTheCheck_IsBlockedThreeTimes_ThenGivenUp_WithNothingCommitted() =>
            AssertGivesUpAfterThree(LifecycleParityHarness.PlanCoveringAnUnknownScenario(), "R9.1");

        private static void AssertGivesUpAfterThree(ChangeScript script, string problemMentions)
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(script));
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAccept(po, "intent");
            var accepted = ws.AuthorAndAccept(po, "spec");
            var copyHead = ws.Head(accepted.Root);
            var mainHead = ws.Git(ws.Master, "rev-parse", "HEAD");

            po.Turn(po.Prompt("plan"));

            var plan = po.LastDocument;
            Assert.True(plan?.FileName == "plan.md", po.Transcript("/hoshin:plan must ask for plan.md to be written"));
            Assert.True(File.Exists(plan!.FullPath), "the draft plan stays on disk");
            var blocks = po.Told.Count(t => t.Contains("cannot be committed yet", StringComparison.Ordinal));
            Assert.True(blocks == 3, po.Transcript($"a plan the check refuses is blocked three times, then given up; it was blocked {blocks} times"));
            Assert.Contains(problemMentions, string.Join("\n", po.Told), StringComparison.Ordinal);
            Assert.Equal(copyHead, ws.Head(accepted.Root));
            Assert.Equal(mainHead, ws.Git(ws.Master, "rev-parse", "HEAD"));
            Assert.False(ws.HeadStatusIs(plan, "draft"), "a refused plan must not be committed");
        }
    }

    /// <summary>
    /// Row 48, original <c>single-repo-done-one-pull-request</c>, task B13 (R21 (6) covers it end to end): in the
    /// single-repository layout a typed <c>done</c> pushes the one change branch, opens one pull request, tells the
    /// engineer its URL and records it in the change's copy in a <c>record pull requests</c> commit.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "single-repo-done-one-pull-request")]
    public sealed class SingleRepoDoneOnePullRequest
    {
        [Fact]
        public void TypedDone_InASingleRepositoryWorkspace_OpensOnePullRequest_AndRecordsItInTheChangeCopy()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.SingleRepoShop(ChangeScripts.TrimTheGreetingName()));
            var dev = ws.Session("dev-session", ws.Master);
            dev.Turn(dev.Prompt("fix", "greet('  Ada ') keeps the padding around the name; it should trim it"));
            var drafted = dev.LastDocument;
            Assert.True(drafted is not null, dev.Transcript("/hoshin:fix must ask for the light-track documents"));
            dev.Turn(dev.Prompt("fix", "--accept"));
            dev.Turn(dev.Prompt("build"));
            var copy = ws.BuildFolder("app");
            ws.AssertTaskCommitted(copy, "T1", dev);
            ws.ReviewUnlessAlreadyCommitted(dev, drafted!.Folder, 1);
            var mainBefore = ws.Git(ws.Master, "ls-remote", "origin", "refs/heads/main");

            var toldBefore = dev.Told.Count;
            dev.Turn(dev.Prompt("done"));

            var prs = ws.PullRequests();
            Assert.True(prs.Count == 1, dev.Transcript($"a single-repository change opens exactly one pull request, got {prs.Count}"));
            var change = ws.ChangeName(ws.Master);
            Assert.Equal($"change/{change}", prs[0].Head);
            Assert.Equal(ws.Head(copy), ws.RemoteHead(ws.Master));
            Assert.Equal(mainBefore, ws.Git(ws.Master, "ls-remote", "origin", "refs/heads/main"));
            var told = string.Join("\n", dev.Told.Skip(toldBefore));
            Assert.True(told.Contains(prs[0].Url, StringComparison.Ordinal), dev.Transcript($"the engineer must be told the pull request's URL {prs[0].Url}:\n{told}"));
            var recorded = ws.Subjects(copy, "main..HEAD").Where(s => s.Contains("record pull requests", StringComparison.Ordinal)).ToList();
            Assert.True(recorded.Count == 1, dev.Transcript($"the pull request is recorded in the change's copy in one `record pull requests` commit, got {recorded.Count}"));
        }
    }

    /// <summary>
    /// Row 49, original <c>spec-finds-a-change-in-its-copy</c>, task B07: the session starts in the engineer's checkout
    /// of the master, on <c>main</c> with no change in its intent folder. The only accepted intent lives in the change's
    /// copy; the spec brief points at the copy, the spec is committed there, and the checkout gains no commit and no file.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "spec-finds-a-change-in-its-copy")]
    public sealed class SpecFindsAChangeInItsCopy
    {
        [Fact]
        public void TheSpecBrief_PointsAtTheChangeCopy_TheSpecIsCommittedThere_AndTheCheckoutGainsNothing()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            var intent = ws.AuthorAndAccept(po, "intent");
            var copy = ws.ChangeCopy();
            Assert.True(copy is not null && AcceptanceWorkspace.PathsEqual(copy, intent.Root), "setup: the accepted intent lives in the change's copy");
            var change = ws.ChangeName(ws.Master);
            Assert.False(Directory.Exists(Path.Combine(ws.Master, "intent", "shop", change)), "setup: the engineer's checkout holds no change folder");
            var mainBefore = ws.Git(ws.Master, "rev-parse", "HEAD");
            var copyBefore = ws.Head(copy!);

            var brief = po.Prompt("spec");

            Assert.True(brief is not null, po.Transcript("/hoshin:spec must find the accepted intent in its copy"));
            Assert.True(LifecycleParityHarness.Mentions(brief, copy!), po.Transcript($"the brief must point at the change's copy {copy}:\n{brief}"));
            po.Turn(brief);
            var spec = po.LastDocument;
            Assert.True(spec?.FileName == "spec.md" && AcceptanceWorkspace.PathsEqual(spec.Root, copy!), po.Transcript($"the spec must be written in the copy, but it was written in {spec?.Root}"));
            ws.AssertCommitted(spec!, po.Transcript("the spec is committed when the turn ends"));
            Assert.NotEqual(copyBefore, ws.Head(copy!));
            Assert.Equal(mainBefore, ws.Git(ws.Master, "rev-parse", "HEAD"));
            Assert.Equal("main", ws.Git(ws.Master, "branch", "--show-current"));
            Assert.True(ws.IsClean(ws.Master), po.Transcript("the engineer's checkout must gain no file"));
            Assert.False(File.Exists(Path.Combine(ws.Master, spec!.Relative)), "the spec must not be written in the engineer's checkout");
        }
    }

    /// <summary>
    /// Row 50, original <c>spec-fixes-then-commits</c>, task B07: Stop checks the spec before it commits anything. A spec
    /// that fails the check is blocked with the problems and nothing is committed; once the model fixes it, Stop commits
    /// the spec and only the spec, although another file sits untracked beside it.
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "spec-fixes-then-commits")]
    public sealed class SpecFixesThenCommits
    {
        private static readonly Regex WriteTo = new(@"Write it to `(?<rel>[^`]+)` in (?<root>.+?)(?: \(|, from|\.\s|\.$)", RegexOptions.Multiline);

        [Fact]
        public void StopChecksTheSpecBeforeCommitting_BlocksABadOne_AndCommitsOnlyTheSpecOnceItIsFixed()
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
            var po = ws.Session("po-session", ws.Master);
            ws.AuthorAndAccept(po, "intent");
            var brief = po.Prompt("spec");
            Assert.True(brief is not null && WriteTo.IsMatch(brief), po.Transcript("/hoshin:spec must ask for spec.md to be written"));
            var target = WriteTo.Match(brief);
            var spec = new DocumentRef(target.Groups["root"].Value.Trim(), target.Groups["rel"].Value.Trim());
            var copyBefore = ws.Head(spec.Root);

            // First the model writes a spec the check refuses (a rule with no requirement tag and no source), and a scratch file beside it.
            ChangeScripts.Write(spec.Folder, "spec.md", """
                ```jsonc
                { "layout": 2, "title": "Shout greeting", "system": "shop", "date": "2026-11-26", "status": "draft" }
                ```

                # Spec: Shout greeting

                ## Requirements

                ```gherkin
                Feature: Shout greeting
                  Rule: Untagged
                    Scenario: Nothing is tagged
                      Given a name
                ```
                """);
            ChangeScripts.Write(spec.Folder, "scratch.md", "notes the model left beside the spec\n");
            var blocked = po.EndTurnOnce();

            Assert.True(blocked is not null, po.Transcript("a spec that fails the check must block the end of the turn"));
            Assert.Contains("spec", blocked, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(copyBefore, ws.Head(spec.Root));
            Assert.True(AcceptanceWorkspace.RunGit(spec.Root, "show", $"HEAD:{spec.Relative.Replace('\\', '/')}").ExitCode != 0, "a spec that fails the check must not be committed");

            // Then the model fixes it, and the turn ends.
            ws.WriteDocument(spec, brief);
            po.Turn(null);

            ws.AssertCommitted(spec, po.Transcript("the fixed spec is committed when the turn ends"));
            var commits = ws.Subjects(spec.Root, $"{copyBefore}..HEAD");
            Assert.True(commits.Length == 1, po.Transcript($"one commit follows the fix, the spec's; got: {string.Join(" | ", commits)}"));
            Assert.Contains("spec", commits[0], StringComparison.Ordinal);
            Assert.Equal(new[] { spec.Relative.Replace('\\', '/') }, ws.FilesOf(spec.Root, "HEAD"));
            Assert.Contains("scratch.md", ws.Git(spec.Root, "status", "--porcelain"), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Row 55 (parity half), original <c>start-routes-a-bug-to-fix</c>, task B05: after start hands a bug to fix, a typed
    /// accept commits the intent, spec and plan acceptances in that order, every pin holds, and no product code is written.
    /// The original's "accept in advance" is a typed accept here ("Advance acceptance", Accepted differences).
    /// </summary>
    [Trait("Category", "Acceptance")]
    [Trait("Original", "start-routes-a-bug-to-fix")]
    public sealed class StartRoutesABugToFix
    {
        private const string Bug = "web: returning visitors see \"Welcome back,Mina\" with no space after the comma";

        [Fact]
        public void AfterStartHandsABugToFix_ATypedAccept_CommitsIntentSpecAndPlanInOrder_WithPinsThatHold() =>
            AssertTypedAcceptCommitsInOrder(viaStart: true);

        /// <summary>The accept half alone, from the fix hand-off on, so it is pinned apart from the start hand-off that B05 adds.</summary>
        [Fact]
        public void AfterTheFixDrafts_ATypedAccept_CommitsIntentSpecAndPlanInOrder_WithPinsThatHold() =>
            AssertTypedAcceptCommitsInOrder(viaStart: false);

        private static void AssertTypedAcceptCommitsInOrder(bool viaStart)
        {
            using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(LifecycleParityHarness.GreetingSpaceBug()));
            var eng = ws.Session("eng-session", ws.Master);
            var webHead = ws.Head(ws.Web);
            var apiHead = ws.Head(ws.Api);

            if (viaStart)
            {
                Assert.True(eng.Prompt("start", Bug) is not null, eng.Transcript("/hoshin:start must reach the plugin"));
            }

            var fix = viaStart ? eng.ModelInvokedSkill("fix", Bug) : eng.Prompt("fix", Bug);
            Assert.True(fix is not null && fix.Contains("intent.md", StringComparison.Ordinal), eng.Transcript("the hand-off from start must give the light-track brief"));
            eng.Turn(fix);
            var drafted = eng.LastDocument;
            Assert.True(drafted is not null, eng.Transcript("the light-track documents must be written"));
            ws.AssertCommitted(drafted!, eng.Transcript("the light-track documents are committed when the turn ends"));
            var copy = ws.ChangeCopy()!;
            var change = ws.ChangeName(ws.Master);
            var draftHead = ws.Head(copy);

            eng.Turn(eng.Prompt("fix", "--accept"));

            var commits = ws.Subjects(copy, $"{draftHead}..HEAD");
            var accepts = commits.Select(c => Regex.Match(c, "accept (intent|spec|plan)")).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
            Assert.True(
                accepts.SequenceEqual(["intent", "spec", "plan"]),
                eng.Transcript($"a typed accept must commit the intent, spec and plan acceptances in that order; the commits after the draft are: {string.Join(" | ", commits)}"));
            foreach (var file in new[] { "intent.md", "spec.md", "plan.md" })
            {
                ws.AssertHeadStatus(new DocumentRef(copy, Path.GetRelativePath(copy, Path.Combine(drafted!.Folder, file)).Replace('\\', '/')), "accepted", eng.Transcript($"{file} is accepted"));
            }

            var state = ws.ChangeState(ws.Master, change);
            var documents = state.GetProperty("documents");
            Assert.Equal("holds", documents.GetProperty("spec").GetProperty("pin").GetString());
            Assert.Equal("holds", documents.GetProperty("plan").GetProperty("pin").GetString());
            Assert.Equal("fix", state.GetProperty("track").GetString());
            Assert.Equal(webHead, ws.Head(ws.Web));
            Assert.Equal(apiHead, ws.Head(ws.Api));
            Assert.Empty(ws.ChangeBranches(ws.Web));
            Assert.Empty(ws.ChangeBranches(ws.Api));
        }
    }
}
