using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class AppsTests
{
    private readonly IGitClient _git = ProcessGitClient.Default;
    private readonly AppService _apps = AppService.Default;

    private static (TestGitFixture.SingleRepoFixture Fixture, string Base) OnChange(Dictionary<string, string>? apps = null)
    {
        var f = TestGitFixture.CreateSingleRepo(apps);
        var baseSha = f.Git("rev-parse", "HEAD");
        f.Git("checkout", "-q", "-b", "change/x");
        return (f, baseSha);
    }

    [Fact]
    public void IsSingleRepo_ReadsMarkerAndIsFalseWithoutOne()
    {
        using var single = TestGitFixture.CreateSingleRepo();
        Assert.True(_apps.IsSingleRepo(single.Repo));

        using var ws = TestGitFixture.CreateWorkspace();
        Assert.False(_apps.IsSingleRepo(ws.Master));
        Assert.False(_apps.IsSingleRepo(Path.Combine(ws.Master, "nowhere")));
    }

    [Fact]
    public void AppFolders_AndAppPathspec_LeaveOutOwnFiles()
    {
        using var single = TestGitFixture.CreateSingleRepo();
        Assert.Equal(new Dictionary<string, string> { ["api"] = "apps/api", ["web"] = "apps/web" }, _apps.AppFolders(single.Repo, "demo"));
        Assert.Equal(["apps/web", ":(exclude)intent", ":(exclude).hoshin.json"], _apps.AppPathspec("apps/web"));
        Assert.Equal([".", ":(exclude)intent", ":(exclude).hoshin.json"], _apps.AppPathspec("."));
    }

    [Fact]
    public void CommitInAppsWeb_TouchesWebAndMovesItsHeadNotApi()
    {
        var (f, baseSha) = OnChange();
        using var _ = f;
        var sha = TestGitFixture.Commit(f.Repo, new() { ["apps/web/a.txt"] = "a\n" });
        Assert.Equal(["web"], _apps.TouchedApps(f.Repo, "demo", f.Repo, baseSha, "HEAD"));
        Assert.Equal(sha, _apps.AppHead(f.Repo, "HEAD", "apps/web", baseSha));
        Assert.Equal(baseSha, _apps.AppHead(f.Repo, "HEAD", "apps/api", baseSha));
    }

    [Fact]
    public void TouchedApps_ComeBackInMarkerOrder()
    {
        var (f, baseSha) = OnChange();
        using var _ = f;
        TestGitFixture.Commit(f.Repo, new() { ["apps/web/a.txt"] = "a\n" });
        TestGitFixture.Commit(f.Repo, new() { ["apps/api/a.txt"] = "a\n" });
        Assert.Equal(["api", "web"], _apps.TouchedApps(f.Repo, "demo", f.Repo, baseSha, "HEAD"));
    }

    [Fact]
    public void CommitTouchingOnlyIntent_TouchesNoAppAndMovesNoHead()
    {
        var (f, baseSha) = OnChange();
        using var _ = f;
        TestGitFixture.Commit(f.Repo, new() { ["intent/demo/x/plan.md"] = "p\n" });
        Assert.Empty(_apps.TouchedApps(f.Repo, "demo", f.Repo, baseSha, "HEAD"));
        Assert.Equal(baseSha, _apps.AppHead(f.Repo, "HEAD", "apps/web", baseSha));
    }

    [Fact]
    public void LoneAppAtRoot_IsTouchedByReadmeButNotByIntentAlone()
    {
        var (f, baseSha) = OnChange(new() { ["shop"] = "." });
        using var _ = f;
        TestGitFixture.Commit(f.Repo, new() { ["intent/demo/x/plan.md"] = "p\n" });
        Assert.Empty(_apps.TouchedApps(f.Repo, "demo", f.Repo, baseSha, "HEAD"));
        Assert.Equal(baseSha, _apps.AppHead(f.Repo, "HEAD", ".", baseSha));

        var sha = TestGitFixture.Commit(f.Repo, new() { ["README.md"] = "changed\n" });
        Assert.Equal(["shop"], _apps.TouchedApps(f.Repo, "demo", f.Repo, baseSha, "HEAD"));
        Assert.Equal(sha, _apps.AppHead(f.Repo, "HEAD", ".", baseSha));
    }

    [Fact]
    public void Compare_IsAgainstMergeBaseNotBaseTip()
    {
        var (f, baseSha) = OnChange();
        using var _ = f;
        f.Git("checkout", "-q", "main");
        TestGitFixture.Commit(f.Repo, new() { ["apps/api/main-only.txt"] = "m\n" });
        f.Git("checkout", "-q", "change/x");
        var sha = TestGitFixture.Commit(f.Repo, new() { ["apps/web/a.txt"] = "a\n" });
        Assert.Equal(["web"], _apps.TouchedApps(f.Repo, "demo", f.Repo, "main", "HEAD"));
        Assert.Equal(sha, _apps.AppHead(f.Repo, "HEAD", "apps/web", "main"));
        Assert.Equal(baseSha, _apps.AppHead(f.Repo, "HEAD", "apps/api", "main"));
    }

    [Fact]
    public void RepoPathOf_KeepsDefaultLayoutAndMapsSingleRepoAppsIntoCopy()
    {
        using var ws = TestGitFixture.CreateWorkspace();
        Assert.Equal(Path.Combine(ws.Ws, "app"), _apps.RepoPathOf(ws.Master, ws.Ws, "demo", "app"));

        using var s1 = TestGitFixture.CreateSingleRepo();
        Assert.Equal(Path.GetFullPath(Path.Combine(s1.Repo, "apps/web")), _apps.RepoPathOf(s1.Repo, s1.Workspace, "demo", "web"));

        using var s2 = TestGitFixture.CreateSingleRepo();
        Assert.Equal(Path.GetFullPath(Path.Combine(s2.Repo, "apps/web")), _apps.RepoPathOf(s2.Repo, s1.Workspace, "demo", "web"));
    }

    [Fact]
    public void GitHelpers_TakeRangeAndPathspec()
    {
        var (f, baseSha) = OnChange();
        using var _ = f;
        Assert.Null(_git.LastCommitTouching(f.Repo, $"{baseSha}..HEAD", ["apps/web"]));
        var sha = TestGitFixture.Commit(f.Repo, new() { ["apps/web/a.txt"] = "a\n" });
        Assert.Equal(sha, _git.LastCommitTouching(f.Repo, $"{baseSha}..HEAD", ["apps/web"]));
        Assert.Equal(["apps/web/a.txt"], _git.ChangedFilesBetween(f.Repo, baseSha, "HEAD", ["apps/web"]));
        Assert.Empty(_git.ChangedFilesBetween(f.Repo, baseSha, "HEAD", ["apps/api"]));
    }

    [Fact]
    public void BaseBranchOnlyOnOrigin_StillResolvesForHeadAndTouchedApps()
    {
        var (f, baseSha) = OnChange();
        using var _ = f;
        var sha = TestGitFixture.Commit(f.Repo, new() { ["apps/web/a.txt"] = "a\n" });
        f.Git("branch", "-D", "main");
        Assert.Null(_git.CommitOf(f.Repo, "refs/heads/main"));
        Assert.Equal(["web"], _apps.TouchedApps(f.Repo, "demo", f.Repo, "main", "HEAD"));
        Assert.Equal(sha, _apps.AppHead(f.Repo, "HEAD", "apps/web", "main"));
        Assert.Equal(baseSha, _apps.AppHead(f.Repo, "HEAD", "apps/api", "main"));
        Assert.Equal(sha, _apps.CurrentAppHead(f.Repo, f.Repo, "demo", "web"));
    }

    [Fact]
    public void CurrentAppHead_IsNullWhenBaseBranchExistsNowhere()
    {
        var (f, _) = OnChange();
        using var _ = f;
        TestGitFixture.Commit(f.Repo, new() { ["apps/web/a.txt"] = "a\n" });
        f.Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/trunk");
        Assert.Null(_apps.CurrentAppHead(f.Repo, f.Repo, "demo", "web"));
    }

    [Fact]
    public void LastCommitTouching_ThrowsCouldNotRunOnGitFailure()
    {
        var (f, _) = OnChange();
        using var _ = f;
        Assert.Null(_git.LastCommitTouching(f.Repo, "HEAD..HEAD", ["apps/web"]));
        Assert.Throws<CouldNotRunException>(() => _git.LastCommitTouching(f.Repo, "nonexistent-rev..HEAD", ["apps/web"]));
    }
}
