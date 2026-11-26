using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R07 (W1): After a successful workspace publish (<c>change.done</c> committing <c>change.jsonc</c>),
/// <see cref="WorkflowEngine.Next"/> reaches <see cref="Done"/> (<c>Success = true</c>) and stays there,
/// every subsequent Stop in any session is silent, and <c>/hoshin:build</c> dispatches nothing and opens no new PR.
/// </summary>
public sealed class PublishDoneStaysDoneTests
{
    private sealed class RecordingPrClient : IPullRequestClient
    {
        public int OpenNewCalls { get; private set; }
        private readonly Dictionary<string, PullRequestRef> _opened = new(StringComparer.Ordinal);

        public string? HostOf(string remoteUrl) => "github";

        public string? SignInProblem(string cwd, string? host) => null;

        public PullRequestRef? OpenPullRequest(string cwd, string branch) =>
            _opened.TryGetValue($"{Path.GetFileName(cwd)}:{branch}", out var existing) ? existing : null;

        public OpenPullRequestResult OpenNew(string cwd, OpenPullRequestInput pr)
        {
            OpenNewCalls++;
            var key = $"{Path.GetFileName(cwd)}:{pr.Head}";
            var created = new PullRequestRef($"https://example.test/{Path.GetFileName(cwd)}/pull/{OpenNewCalls}", OpenNewCalls);
            _opened[key] = created;
            return new OpenPullRequestResult(created, null);
        }

        public string? StateOf(string cwd, string url) => PullRequestStates.Open;
    }

    [Fact]
    public void SingleRepoWorkspace_AfterChangeDoneCommitsChangeJsonc_NextIsDoneTrueAndStaysQuiet()
    {
        using var tmp = new TempDir("hoshin-r07-single-");
        var wsRoot = Path.Combine(tmp.Path, "ws");
        var repoDir = Path.Combine(wsRoot, "mono");

        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": {
                    "shop": {
                      "apps": { "app": "apps/app" }
                    }
                  }
                }
                """,
            ["apps/app/src/index.mjs"] = "export const ok = true;\n",
        });

        var prClient = new RecordingPrClient();
        var services = new Cli.HoshinServices(
            TestServices.SchemaValidator,
            TestServices.CommandRunner,
            TestServices.Git,
            prClient,
            TestServices.Time,
            TestServices.FileSystem);

        var started = services.ChangeStarter.StartFromHook(repoDir, "intent", "single-pub");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        SeedAcceptedPlan(copyRoot, changeFolder, changeName, "app");

        // Commit T1 work in apps/app and log it in build/app.md.
        TestGitFixture.Write(Path.Combine(copyRoot, "apps", "app", "src", "index.mjs"), "export const ok = true;\nexport const shout = () => 'hi!';\n");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", "feat(shop): Implement T1", "-m", "Plan-Task: T1\nGenerated-By: hoshin");
        var codeSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        services.BuildLogWriter.AppendEntry(changeFolder, "app", "T1", [codeSha], "observed");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): log T1 {changeName}", "-m", "Generated-By: hoshin");

        // Record green check and clean review at current code head.
        var wfRepo = services.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        var stateBeforeReview = services.WorkflowStateReader.Read(wfRepo)!;
        var prePublishCodeHead = stateBeforeReview.EffectiveCodeHead;
        wfRepo.UpdateLedger(l => l with
        {
            Checks = [new WorkflowCheckRun(prePublishCodeHead, true, "ok", TestServices.Time.GetUtcNow())],
        });

        var appHead = services.Apps.AppHead(copyRoot, "HEAD", "apps/app", "main");
        TestGitFixture.Write(Path.Combine(changeFolder, "build", "review.md"), $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-26", "commits": { "app": "{{appHead[..7]}}" } }
            ```

            No findings.
            """);
        var (reviewProblems, reviewCommit) = services.DocumentCommitter.CommitReview(wfRepo, services.WorkflowStateReader.Read(wfRepo)!);
        Assert.Empty(reviewProblems);
        Assert.NotNull(reviewCommit);

        // Engineer approves via /hoshin:accept -> publishes via change.done (which commits change.jsonc!).
        var headBeforePublish = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");
        var (acceptOut, _) = services.HookOrchestrator.HandleStart(
            services.HookAdapter.Parse(PromptPayload("po-r07", repoDir, "accept", "")),
            rolesRoot: null);
        Assert.NotNull(acceptOut);
        Assert.Contains("published", acceptOut!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, prClient.OpenNewCalls);

        // change.done committed change.jsonc, moving HEAD.
        var headAfterPublish = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");
        Assert.NotEqual(headBeforePublish, headAfterPublish);

        // Next MUST be Done(true), NOT RunChecks!
        var stateAfterPublish = services.WorkflowStateReader.Read(wfRepo)!;
        var next = Assert.IsType<Done>(WorkflowEngine.Next(stateAfterPublish));
        Assert.True(next.Success, $"Expected Done(true) after publish, got Done(false, {next.Outcome})");

        // Stop in master session must be completely silent.
        var (stopOut, _) = services.HookOrchestrator.HandleStop(
            services.HookAdapter.Parse(StopPayload("po-r07", repoDir)),
            rolesRoot: null);
        Assert.Null(stopOut);

        // Later /hoshin:build dispatches nothing and opens no new PR.
        var (buildOut, _) = services.HookOrchestrator.HandleStart(
            services.HookAdapter.Parse(PromptPayload("dev-r07", repoDir, "build", "")),
            rolesRoot: null);
        Assert.DoesNotContain(HookInstructions.BeginMarker, buildOut ?? "", StringComparison.Ordinal);
        Assert.Equal(1, prClient.OpenNewCalls);
        var (devStopOut, _) = services.HookOrchestrator.HandleStop(
            services.HookAdapter.Parse(StopPayload("dev-r07", repoDir)),
            rolesRoot: null);
        Assert.Null(devStopOut);
    }

    [Fact]
    public void MultiRepoWorkspace_AfterPublish_StopIsSilentAndProductRepoBuildDispatchesNothing()
    {
        using var tmp = new TempDir("hoshin-r07-multi-");
        var wsRoot = Path.Combine(tmp.Path, "ws");
        var masterDir = Path.Combine(wsRoot, "ai-sdlc");
        var appDir = Path.Combine(wsRoot, "app");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """{ "version": 1, "systems": { "shop": { "repos": ["app"] } } }""",
        });

        TestGitFixture.InitRepo(appDir, new Dictionary<string, string>
        {
            ["src/index.mjs"] = "export const ok = true;\n",
        });

        var prClient = new RecordingPrClient();
        var services = new Cli.HoshinServices(
            TestServices.SchemaValidator,
            TestServices.CommandRunner,
            TestServices.Git,
            prClient,
            TestServices.Time,
            TestServices.FileSystem);

        var started = services.ChangeStarter.StartFromHook(masterDir, "intent", "multi-pub");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        SeedAcceptedPlan(copyRoot, changeFolder, changeName, "app");

        TestGitFixture.Git(appDir, "checkout", "-q", "-b", $"change/{changeName}");
        TestGitFixture.Write(Path.Combine(appDir, "src", "index.mjs"), "export const ok = true;\nexport const shout = () => 'hi!';\n");
        TestGitFixture.Git(appDir, "add", ".");
        TestGitFixture.Git(appDir, "commit", "-q", "-m", "feat(shop): Implement T1", "-m", "Plan-Task: T1\nGenerated-By: hoshin");
        var appSha = TestGitFixture.Git(appDir, "rev-parse", "HEAD");

        services.BuildLogWriter.AppendEntry(changeFolder, "app", "T1", [appSha], "observed");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): log T1 {changeName}", "-m", "Generated-By: hoshin");

        var wfRepo = services.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        var preHead = services.WorkflowStateReader.Read(wfRepo)!.EffectiveCodeHead;
        wfRepo.UpdateLedger(l => l with
        {
            Checks = [new WorkflowCheckRun(preHead, true, "ok", TestServices.Time.GetUtcNow())],
        });

        TestGitFixture.Write(Path.Combine(changeFolder, "build", "review.md"), $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-26", "commits": { "app": "{{appSha[..7]}}" } }
            ```

            No findings.
            """);
        var (reviewProblems, reviewCommit) = services.DocumentCommitter.CommitReview(wfRepo, services.WorkflowStateReader.Read(wfRepo)!);
        Assert.Empty(reviewProblems);
        Assert.NotNull(reviewCommit);

        // Publish via /hoshin:accept.
        var (acceptOut, _) = services.HookOrchestrator.HandleStart(
            services.HookAdapter.Parse(PromptPayload("po-multi", masterDir, "accept", "")),
            rolesRoot: null);
        Assert.NotNull(acceptOut);
        Assert.Contains("published", acceptOut!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, prClient.OpenNewCalls);

        var next = Assert.IsType<Done>(WorkflowEngine.Next(services.WorkflowStateReader.Read(wfRepo)!));
        Assert.True(next.Success);

        // Stop in master and app sessions are both silent.
        var (masterStop, _) = services.HookOrchestrator.HandleStop(
            services.HookAdapter.Parse(StopPayload("po-multi", masterDir)),
            rolesRoot: null);
        Assert.Null(masterStop);
        var (appStop, _) = services.HookOrchestrator.HandleStop(
            services.HookAdapter.Parse(StopPayload("app-multi", appDir)),
            rolesRoot: null);
        Assert.Null(appStop);

        // /hoshin:build from app dispatches nothing and opens no new PR.
        var (buildOut, _) = services.HookOrchestrator.HandleStart(
            services.HookAdapter.Parse(PromptPayload("app-multi", appDir, "build", "")),
            rolesRoot: null);
        Assert.DoesNotContain(HookInstructions.BeginMarker, buildOut ?? "", StringComparison.Ordinal);
        Assert.Equal(2, prClient.OpenNewCalls);
        var (appStopAfterBuild, _) = services.HookOrchestrator.HandleStop(
            services.HookAdapter.Parse(StopPayload("app-multi", appDir)),
            rolesRoot: null);
        Assert.Null(appStopAfterBuild);
    }

    private static void SeedAcceptedPlan(string copyRoot, string changeFolder, string changeName, string repoName)
    {
        TestGitFixture.Write(Path.Combine(changeFolder, "intent.md"), "```jsonc\n{ \"title\": \"Publish test\", \"system\": \"shop\", \"author\": \"Pat\", \"date\": \"2026-11-26\", \"status\": \"accepted\", \"scope\": \"shop\" }\n```\n\n# Intent: Publish test\n");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept intent {changeName}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(Path.Combine(changeFolder, "spec.md"), $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Publish test

            ```gherkin
            Feature: Publish test
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

            # Plan: Publish test

            ## Tasks

            ### T1: Implement behavior

            ```jsonc
            { "id": "T1", "repo": "{{repoName}}", "depends_on": [], "covers": ["R1.1"], "test": null }
            ```

            #### Change
            Implement behavior.

            #### Tests
            Verified by build.finish checks.
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept plan {changeName}", "-m", "Generated-By: hoshin");
    }

    private static string PromptPayload(string sessionId, string cwd, string skill, string args) => JsonSerializer.Serialize(new
    {
        hook_event_name = "UserPromptExpansion",
        session_id = sessionId,
        cwd,
        command_name = $"hoshin:{skill}",
        command_args = args,
        prompt = args.Length == 0 ? $"/hoshin:{skill}" : $"/hoshin:{skill} {args}",
    });

    private static string StopPayload(string sessionId, string cwd) => JsonSerializer.Serialize(new
    {
        hook_event_name = "Stop",
        session_id = sessionId,
        cwd,
        stop_hook_active = false,
    });
}
