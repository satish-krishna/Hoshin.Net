using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class EventsSingleRepoTests
{
    private const string Name = "2026-11-02-wave";
    private readonly EventContextFactory _factory = EventContextFactory.Default;
    private readonly AppService _apps = AppService.Default;
    private readonly LedgerStore _ledger = LedgerStore.Default;

    private static (TestGitFixture.SingleRepoFixture Fixture, string Copy) Started()
    {
        var s = TestGitFixture.CreateSingleRepo();
        var (code, stdout, stderr, json) = TestGitFixture.RunCli(
            s.Repo,
            "change", "start", "--json", "--system", "demo", "--change", Name, "--type", "feat", "--scope", "PROJ-1", "--title", "Wave");
        Assert.True(code == 0 && json is not null, stdout + stderr);
        Assert.Equal("passed", json!.Value.GetProperty("outcome").GetString());
        var copy = json.Value.GetProperty("details").GetProperty("copy").GetString()!;
        return (s, copy);
    }

    private EventContext ContextOf(string cwd, string? repo = null) =>
        _factory.Build(cwd, new EventContextArgs("task.done", Name, null, repo, new ResolveOptions(), new() { ["task"] = "T1" }));

    private RepoLedger LedgerOf(TestGitFixture.SingleRepoFixture s, string app) =>
        _ledger.Read(_ledger.LedgerFolder(s.Repo, "demo", Name), app)!;

    [Fact]
    public void PerRepositoryEvent_RunsInAppFolderInsideCopyWithAppHead()
    {
        var (s, copy) = Started();
        using var _ = s;
        var ctx = ContextOf(s.Repo, "api");
        Assert.Equal("api", ctx.Repo);
        Assert.True(PathHelpers.PathsEqual(Path.Combine(copy, "apps", "api"), ctx.RepoPath));
        Assert.True(PathHelpers.PathsEqual(copy, ctx.Root));
        Assert.Equal(_apps.AppHead(copy, "HEAD", "apps/api", ctx.Base!), ctx.Head);
        Assert.Equal(TestGitFixture.Git(copy, "rev-parse", "main"), ctx.Head);
        Assert.Equal(["api", "web"], ctx.Repos);
        Assert.Equal($"change/{Name}", ContextOf(s.Repo, "api").Branch);
    }

    [Fact]
    public void AppHead_IgnoresCommitsToOtherAppsAndDocuments()
    {
        var (s, copy) = Started();
        using var _ = s;
        var before = ContextOf(s.Repo, "api").Head;
        TestGitFixture.Commit(copy, new() { ["apps/web/new.txt"] = "x" }, "feat(PROJ-1): web");
        TestGitFixture.Commit(copy, new() { [$"intent/demo/{Name}/build/api.md"] = "# log\n" }, "docs(PROJ-1): log");
        Assert.Equal(before, ContextOf(s.Repo, "api").Head);

        var own = TestGitFixture.Commit(copy, new() { ["apps/api/new.txt"] = "x" }, "feat(PROJ-1): api");
        var ctx = ContextOf(s.Repo, "api");
        Assert.Equal(own, ctx.Head);
        Assert.Equal(own, _factory.HeadOf(ctx));
        Assert.NotEqual(own, ContextOf(s.Repo, "web").Head);
    }

    [Fact]
    public void NonPerRepositoryEvent_KeepsMaster()
    {
        var (s, copy) = Started();
        using var _ = s;
        var ctx = _factory.Build(s.Repo, new EventContextArgs("change.done", Name, null, null, new ResolveOptions(), new()));
        Assert.Equal("master", ctx.Repo);
        Assert.True(PathHelpers.PathsEqual(copy, ctx.RepoPath));
        Assert.True(PathHelpers.PathsEqual(copy, ctx.Root));
    }

    [Fact]
    public void DefaultLayout_RootIsRepoPathAndHeadIsHead()
    {
        using var w = TestGitFixture.CreateWorkspace();
        var ctx = _factory.Build(w.Master, new EventContextArgs("build.start", TestGitFixture.ChangeName, null, "app", new ResolveOptions(), new()));
        Assert.True(PathHelpers.PathsEqual(ctx.RepoPath, ctx.Root));
        Assert.True(PathHelpers.PathsEqual(w.App, ctx.Root));
        Assert.Equal(TestGitFixture.Git(w.App, "rev-parse", "HEAD"), _factory.HeadOf(ctx));
    }

    [Fact]
    public void UnknownApp_IsRefused()
    {
        var (s, _) = Started();
        using var _ = s;
        var (code, stdout, stderr, _) = TestGitFixture.RunCli(s.Repo, "run", "task.done", "--check", "--json", "--change", Name, "--repo", "nope", "--task", "T1");
        Assert.NotEqual(0, code);
        Assert.Contains("nope is not an app of system demo (api, web)", stdout + stderr);
    }

    [Fact]
    public void TaskDoneForApp_IsKeyedByAppHeadSoOtherCommitsLeaveItCurrent()
    {
        var (s, copy) = Started();
        using var _ = s;
        string[] args = ["run", "task.done", "--json", "--change", Name, "--repo", "api", "--task", "T1"];
        var own = TestGitFixture.Commit(copy, new() { ["apps/api/new.txt"] = "x" }, "feat(PROJ-1): api");
        var first = TestGitFixture.RunCli(s.Repo, args);
        Assert.Equal("passed", first.Json!.Value.GetProperty("outcome").GetString());
        Assert.Equal(own, first.Json.Value.GetProperty("key").GetString());
        Assert.Equal("api", first.Json.Value.GetProperty("repo").GetString());
        Assert.Equal(own, LedgerOf(s, "api").Runs[^1].Key);
        Assert.True(PathHelpers.PathsEqual(Path.Combine(copy, "apps", "api"), LedgerOf(s, "api").Path));

        TestGitFixture.Commit(copy, new() { ["apps/web/new.txt"] = "x" }, "feat(PROJ-1): web");
        TestGitFixture.Commit(copy, new() { [$"intent/demo/{Name}/build/api.md"] = "# log\n" }, "docs(PROJ-1): log");
        var again = TestGitFixture.RunCli(s.Repo, args);
        Assert.True(again.Json!.Value.GetProperty("reused").GetBoolean());
        Assert.Equal(own, again.Json.Value.GetProperty("key").GetString());
        Assert.Single(LedgerOf(s, "api").Runs);
        Assert.False(File.Exists(Path.Combine(_ledger.LedgerFolder(s.Repo, "demo", Name), "web.json")));
    }

    [Fact]
    public void WithoutRepo_SessionInsideAppFolderOfCopyRunsForThatApp()
    {
        var (s, copy) = Started();
        using var _ = s;
        var inWeb = ContextOf(Path.Combine(copy, "apps", "web"));
        Assert.Equal("web", inWeb.Repo);
        Assert.True(PathHelpers.PathsEqual(Path.Combine(copy, "apps", "web"), inWeb.RepoPath));
        Assert.Equal("api", ContextOf(Path.Combine(copy, "apps", "api")).Repo);
        var ex = Assert.Throws<CouldNotRunException>(() => ContextOf(copy));
        Assert.Contains("runs in one repository: pass --repo", ex.Message);
    }

    [Fact]
    public void BaseBranchOnlyOnRemote_StillLetsPerAppEventRun()
    {
        var (s, copy) = Started();
        using var _ = s;
        TestGitFixture.Commit(copy, new() { ["apps/api/a.txt"] = "a" }, "feat(PROJ-1): api");
        s.Git("checkout", "-q", "-b", "parked");
        s.Git("branch", "-D", "main");
        var r = TestGitFixture.RunCli(s.Repo, "run", "task.done", "--check", "--json", "--change", Name, "--repo", "api", "--task", "T1");
        Assert.DoesNotContain("no common commit", r.Stdout + r.Stderr);
        Assert.NotNull(r.Json);
        Assert.NotEqual("could-not-run", r.Json!.Value.GetProperty("outcome").GetString());
    }

    [Fact]
    public void DeletedAppFolder_DoesNotNullBranchAndBase()
    {
        var (s, copy) = Started();
        using var _ = s;
        TestGitFixture.Git(copy, "rm", "-rq", "apps/api");
        TestGitFixture.Git(copy, "commit", "-q", "-m", "feat(PROJ-1): drop api", "-m", "Generated-By: hoshin");
        Assert.False(Directory.Exists(Path.Combine(copy, "apps", "api")));
        var ctx = ContextOf(s.Repo, "api");
        Assert.Equal($"change/{Name}", ctx.Branch);
        Assert.Equal("main", ctx.Base);
    }
}
