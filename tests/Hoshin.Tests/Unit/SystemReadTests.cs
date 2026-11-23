using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class SystemReadTests
{
    private static TestGitFixture.WorkspaceFixture CreatePopulatedWorkspace(int finishedCount)
    {
        var ws = TestGitFixture.CreateWorkspace(built: true);
        var files = new Dictionary<string, string>();
        for (var i = 0; i < finishedCount; i++)
        {
            var c = $"2026-08-01-finished-{i:D3}";
            files[$"intent/demo/{c}/intent.md"] = TestGitFixture.IntentText + "\n";
            files[$"intent/demo/{c}/spec.md"] = "```jsonc\n{ \"layout\": 2 }\n```\n# Spec\n";
            files[$"intent/demo/{c}/plan.md"] = "```jsonc\n{ \"layout\": 2 }\n```\n# Plan\n";
            files[$"intent/demo/{c}/build/app.md"] = TestGitFixture.BuildLog(["T1", "T2"]);
            files[$"intent/demo/{c}/build/review.md"] = "```jsonc\n{ \"layout\": 2 }\n```\n# Review\n";
        }

        if (files.Count > 0)
        {
            TestGitFixture.Commit(ws.Master, files);
        }

        return ws;
    }

    [Fact]
    public void HeadBlobsAndHeadText_MatchGitByteForByte()
    {
        using var f = CreatePopulatedWorkspace(2);
        var r = new SystemRead(f.Master, "demo", TestServices.Git);
        var paths = TestGitFixture.Git(f.Master, "ls-tree", "-r", "--name-only", "HEAD", "--", "intent/demo/")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.TrimEnd('\r'))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(paths, r.HeadBlobs.Keys.OrderBy(s => s, StringComparer.Ordinal).ToList());

        r.LoadTexts(paths);
        foreach (var p in paths)
        {
            var raw = TestServices.CommandRunner.Run(new CommandRequest(f.Master, ["git", "show", $"HEAD:{p}"], ExtraEnvironment: TestGitFixture.GitEnv)).Stdout;
            Assert.Equal(raw, r.HeadText(p));
        }
    }

    [Fact]
    public void HeadTextAndObjectOf_HandleMissingAmbiguousAndNewline()
    {
        using var f = CreatePopulatedWorkspace(1);
        var r = new SystemRead(f.Master, "demo", TestServices.Git);
        Assert.Null(r.HeadText("intent/demo/none/intent.md"));
        Assert.Null(r.ObjectOf("0000000^{commit}"));
        Assert.Equal(TestGitFixture.Git(f.Master, "rev-parse", "HEAD"), r.ObjectOf("HEAD^{commit}"));
        Assert.Null(r.ObjectOf("HEAD:intent/demo/none/intent.md"));
        Assert.Null(r.ObjectOf("HEAD\nHEAD"));
    }

    [Fact]
    public void RepoWithoutHead_HasNoBlobsAndStartsNoCatFile()
    {
        using var dir = new TempDir("hoshin-nohead-");
        TestGitFixture.Git(dir.Path, "init", "-q", "-b", "main");
        var counter = new CountingCommandRunner(TestServices.CommandRunner);
        var git = new ProcessGitClient(counter);
        var r = new SystemRead(dir.Path, "demo", git);
        Assert.False(r.HasHead);
        Assert.Empty(r.HeadBlobs);
        var afterCtor = counter.GitCount;
        Assert.Null(r.HeadText("x"));
        r.LoadTexts(["x"]);
        Assert.Equal(afterCtor, counter.GitCount);
    }

    [Fact]
    public void Reported_IsComputedOnFirstUseAndCached()
    {
        using var f = CreatePopulatedWorkspace(1);
        var counter = new CountingCommandRunner(TestServices.CommandRunner);
        var r = new SystemRead(f.Master, "demo", new ProcessGitClient(counter));
        var before = counter.GitCount;
        var first = r.Reported;
        Assert.Equal(before + 1, counter.GitCount);
        Assert.Same(first, r.Reported);
        Assert.Equal(before + 1, counter.GitCount);
    }

    [Fact]
    public void LoadTextsAndResolve_EachStartOneProcessHoweverManyNames()
    {
        using var f = CreatePopulatedWorkspace(5);
        var counter = new CountingCommandRunner(TestServices.CommandRunner);
        var r = new SystemRead(f.Master, "demo", new ProcessGitClient(counter));
        var paths = r.HeadBlobs.Keys.ToList();
        Assert.True(paths.Count >= 20, $"expected >= 20 paths, got {paths.Count}");

        r.LoadTexts(paths);
        r.LoadTexts(paths);
        r.Resolve(paths.Take(20).Select(p => "HEAD:" + p));
        r.Resolve(paths.Take(20).Select(p => "HEAD:" + p));
        foreach (var p in paths)
        {
            Assert.NotNull(r.HeadText(p));
        }

        // Constructor: rev-parse HEAD (1) + ls-tree (2); LoadTexts: cat-file --batch (3); Resolve: cat-file --batch-check (4).
        Assert.Equal(4, counter.GitCount);
    }

    [Fact]
    public void ChangeFolderWithSpace_IsReadByteForByte()
    {
        using var f = CreatePopulatedWorkspace(1);
        TestGitFixture.Commit(f.Master, new() { ["intent/demo/2026-01-01 with space/intent.md"] = "hello\r\nworld\n" });
        var r = new SystemRead(f.Master, "demo", TestServices.Git);
        Assert.Equal("hello\r\nworld\n", r.HeadText("intent/demo/2026-01-01 with space/intent.md"));
        Assert.Equal(["intent/demo/2026-01-01 with space/intent.md"], r.HeadPaths("intent/demo/2026-01-01 with space/"));
    }
}
