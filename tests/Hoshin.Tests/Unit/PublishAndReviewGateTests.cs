using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class PublishAndReviewGateTests
{
    private static WorkflowDocState AcceptedDoc(string name) =>
        new($"/repo/.hoshin/{name}.md", Exists: true, InHead: true, Uncommitted: false, WorkingStatus: "accepted", HeadStatus: "accepted");

    private static WorkflowDocState MissingDoc(string name) =>
        new($"/repo/.hoshin/{name}.md", Exists: false, InHead: false, Uncommitted: false, WorkingStatus: null, HeadStatus: null);

    private static WorkflowPlan SamplePlan(string? branch = null) => new(
        Change: "my-change",
        Scope: "demo",
        Base: "main",
        Tasks: [new WorkflowTask("T1", "Do thing", "text", ["node", "--test", "test/t1.test.mjs"])],
        Checks: [],
        Branch: branch);

    private static WorkflowState StateAtChecksGreen(WorkflowLedger? ledger = null, string head = "code1111111", string? codeHead = null)
    {
        var l = ledger ?? new WorkflowLedger();
        if (l.Verdicts.Count == 0)
        {
            l.Verdicts.Add(new WorkflowVerdict("T1", 1, codeHead ?? head, true, "observed", [], DateTimeOffset.UtcNow));
        }

        if (l.Checks.Count == 0)
        {
            l.Checks.Add(new WorkflowCheckRun(codeHead ?? head, true, "ok", DateTimeOffset.UtcNow));
        }

        return new WorkflowState(
            "/repo",
            "/repo/.hoshin",
            SamplePlan(),
            head,
            l,
            null,
            MissingDoc("intent"),
            MissingDoc("spec"),
            AcceptedDoc("plan"));
    }

    [Fact]
    public void EffectiveBranch_DefaultsToChangePrefix()
    {
        Assert.Equal("change/my-change", SamplePlan().EffectiveBranch);
        Assert.Equal("custom/branch", SamplePlan("custom/branch").EffectiveBranch);
    }

    [Fact]
    public void Next_AfterGreenChecksWithNoReview_ReturnsReviewDocument()
    {
        var state = StateAtChecksGreen();
        var step = Assert.IsType<ReviewDocument>(WorkflowEngine.Next(state));
        Assert.Equal("code1111111", step.Head);
    }

    [Fact]
    public void Next_WithHighReviewFindings_ReturnsDoneFalse()
    {
        var ledger = new WorkflowLedger();
        ledger.Verdicts.Add(new WorkflowVerdict("T1", 1, "code1111111", true, "observed", [], DateTimeOffset.UtcNow));
        ledger.Checks.Add(new WorkflowCheckRun("code1111111", true, "ok", DateTimeOffset.UtcNow));
        ledger.Reviews.Add(new WorkflowReviewRun("code1111111", 1, 2, 1, DateTimeOffset.UtcNow));

        var done = Assert.IsType<Done>(WorkflowEngine.Next(StateAtChecksGreen(ledger)));
        Assert.False(done.Success);
        Assert.Contains("review 1 has 1 high findings", done.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void Next_WithCleanReview_ReturnsNeedsApproval()
    {
        var ledger = new WorkflowLedger();
        ledger.Verdicts.Add(new WorkflowVerdict("T1", 1, "code1111111", true, "observed", [], DateTimeOffset.UtcNow));
        ledger.Checks.Add(new WorkflowCheckRun("code1111111", true, "ok", DateTimeOffset.UtcNow));
        ledger.Reviews.Add(new WorkflowReviewRun("code1111111", 1, 1, 0, DateTimeOffset.UtcNow));

        var approval = Assert.IsType<NeedsApproval>(WorkflowEngine.Next(StateAtChecksGreen(ledger)));
        Assert.Equal("code1111111", approval.Head);
    }

    [Fact]
    public void CommitReview_DoesNotChangeCodeHeadSoNextTransitionsDirectlyToNeedsApproval()
    {
        using var tmp = new TempDir("hoshin-t31-codehead-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/app.mjs"] = "export const a = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t31-codehead", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Demo

                ## Tasks

                ### T1: Task

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Change
                Update `src/app.mjs`.

                #### Tests
                Test it.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var codeHead = repo.Head();
        repo.WithLedger(l =>
        {
            l.Verdicts.Add(new WorkflowVerdict("T1", 1, codeHead, true, "observed", [], DateTimeOffset.UtcNow));
            l.Checks.Add(new WorkflowCheckRun(codeHead, true, "ok", DateTimeOffset.UtcNow));
            return 0;
        });

        var beforeReview = TestServices.ReadState(repo)!;
        Assert.IsType<ReviewDocument>(WorkflowEngine.Next(beforeReview));

        var shortHead = WorkflowEngine.Short(codeHead);
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "build", "review.md"), $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-22", "commits": { "repo": "{{shortHead}}" } }
            ```

            No findings.
            """);

        var (problems, commit) = TestServices.WorkflowRunner.CommitReview(repo, beforeReview);
        Assert.Empty(problems);
        Assert.NotNull(commit);
        Assert.NotEqual(codeHead, repo.Head());

        // Committing build/review.md moved HEAD, but NOT codeHead: Next must proceed to NeedsApproval without re-running checks or review!
        var afterReview = TestServices.ReadState(repo)!;
        var approval = Assert.IsType<NeedsApproval>(WorkflowEngine.Next(afterReview));
        Assert.Equal(codeHead, approval.Head);
    }

    [Fact]
    public void Publish_OpensNewPullRequestOrReusesExistingAndUsesUpstreamRemote()
    {
        using var tmp = new TempDir("hoshin-t31-pub-");
        var upstreamDir = Path.Combine(tmp.Path, "upstream.git");
        Directory.CreateDirectory(upstreamDir);
        TestGitFixture.Git(upstreamDir, "init", "--bare", "-b", "main");

        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/app.mjs"] = "export const a = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t31-pub", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Demo

                ## Tasks

                ### T1: Task

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Change
                Update `src/app.mjs`.

                #### Tests
                Test it.
                """,
        });

        // Point branch tracking upstream to a non-origin remote ("upstream").
        TestGitFixture.Git(repoDir, "remote", "add", "upstream", upstreamDir);
        TestGitFixture.Git(repoDir, "push", "-u", "upstream", "main");

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;
        var targetRef = $"refs/heads/{state.Plan.EffectiveBranch}";
        Assert.Equal("refs/heads/change/t31-pub", targetRef);

        var fakePr = new FakePullRequestClient();
        var runner = TestServices.CreateWorkflowRunner(pullRequests: fakePr);

        // First publish: opens a new PR with base="main", head="change/t31-pub", and pushes to upstream remote.
        var first = runner.Publish(repo, state, targetRef);
        Assert.True(first.Ok, first.Error);
        Assert.Equal("https://github.com/org/repo/pull/42", first.Published!.PullRequestUrl);
        Assert.Single(fakePr.OpenedInputs);
        Assert.Equal("main", fakePr.OpenedInputs[0].Base);
        Assert.Equal("change/t31-pub", fakePr.OpenedInputs[0].Head);
        Assert.Equal(repo.Head(), TestGitFixture.Git(tmp.Path, "--git-dir", upstreamDir, "rev-parse", targetRef));

        // Second publish with an existing open PR: reuses it without calling OpenNew again.
        fakePr.Existing = new PullRequestRef("https://github.com/org/repo/pull/42", 42);
        var second = runner.Publish(repo, state, targetRef);
        Assert.True(second.Ok, second.Error);
        Assert.Equal("https://github.com/org/repo/pull/42", second.Published!.PullRequestUrl);
        Assert.Single(fakePr.OpenedInputs);
    }

    [Fact]
    public void Publish_WhenPullRequestCreationFails_ReturnsErrorAndDoesNotRecordPublished()
    {
        using var tmp = new TempDir("hoshin-t31-prfail-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/app.mjs"] = "export const a = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t31-prfail", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Demo

                ## Tasks

                ### T1: Task

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Change
                Update `src/app.mjs`.

                #### Tests
                Test it.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;
        var fakePr = new FakePullRequestClient { OpenNewError = "gh auth required" };
        var runner = TestServices.CreateWorkflowRunner(pullRequests: fakePr);

        var result = runner.Publish(repo, state, $"refs/heads/{state.Plan.EffectiveBranch}");
        Assert.False(result.Ok);
        Assert.Contains("gh auth required", result.Error, StringComparison.Ordinal);
        Assert.Empty(repo.ReadLedger().Published);
    }

    private sealed class FakePullRequestClient : IPullRequestClient
    {
        public PullRequestRef? Existing { get; set; }

        public string? OpenNewError { get; set; }

        public List<OpenPullRequestInput> OpenedInputs { get; } = [];

        public string? HostOf(string remote) => "github.com";

        public string? SignInProblem(string cwd, string? host) => null;

        public PullRequestRef? OpenPullRequest(string cwd, string branch) => Existing;

        public OpenPullRequestResult OpenNew(string cwd, OpenPullRequestInput pr)
        {
            OpenedInputs.Add(pr);
            return OpenNewError is not null
                ? new OpenPullRequestResult(null, OpenNewError)
                : new OpenPullRequestResult(new PullRequestRef("https://github.com/org/repo/pull/42", 42), null);
        }

        public string? StateOf(string cwd, string url) => PullRequestStates.Open;
    }
}
