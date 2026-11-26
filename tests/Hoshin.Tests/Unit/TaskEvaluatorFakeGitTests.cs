using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class TaskEvaluatorFakeGitTests
{
    [Fact]
    public void EvaluateAndCommitTask_WithFakeGitAndFakeRunner_CommitsTestThenProductionWithZeroProcesses()
    {
        using var tmp = new TempDir("hoshin-a1-fakegit-");
        var repoRoot = Path.Combine(tmp.Path, "repo");
        var gitDir = Path.Combine(tmp.Path, "git");
        var changeDir = Path.Combine(repoRoot, ".hoshin");
        Directory.CreateDirectory(Path.Combine(repoRoot, "test"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src"));
        Directory.CreateDirectory(changeDir);
        File.WriteAllText(Path.Combine(repoRoot, "test", "t1.test.mjs"), "throw new Error('fail');\n");
        File.WriteAllText(Path.Combine(repoRoot, "src", "app.mjs"), "export const ok = true;\n");

        var fakeGit = new RecordingFakeGit("1111111111111111111111111111111111111111", "?? test/t1.test.mjs\0?? src/app.mjs\0");
        var fakeRunner = new FakeTestRunner(repoRoot);
        var fs = new PhysicalFileSystem();
        var ledgerStore = new Hoshin.Workflow.Events.LedgerStore(fakeGit, fs);
        var repo = new WorkflowRepo(repoRoot, gitDir, changeDir, fakeGit, fakeGit, fs, ledgerStore);

        var task = new WorkflowTask("T1", "add greeting", "Implement greet", ["fake-test", "test/t1.test.mjs"]);
        var plan = new WorkflowPlan("c1", "demo", "main", [task], []);
        var missingDoc = new WorkflowDocState(Path.Combine(changeDir, "intent.md"), false, false, false, null, null);
        var state = new WorkflowState(repoRoot, changeDir, plan, fakeGit.HeadSha, Hoshin.Workflow.Events.LedgerStore.Empty(), null, missingDoc, missingDoc, missingDoc);

        var evaluator = new TaskEvaluator(fakeGit, fakeGit, fakeGit, fakeGit, fakeRunner, new BuildLogWriter(fs), new NullWorkflowEventBridge(), new IdentityTaskRepoResolver(), TimeProvider.System);
        var verdict = evaluator.EvaluateAndCommitTask(repo, state, "T1", 1);

        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
        Assert.Equal(2, fakeGit.Commits.Count);
        Assert.Equal(["test/t1.test.mjs"], fakeGit.Commits[0].Paths);
        Assert.StartsWith("test(demo): add greeting", fakeGit.Commits[0].Paragraphs[0], StringComparison.Ordinal);
        Assert.Equal(["src/app.mjs"], fakeGit.Commits[1].Paths);
        Assert.StartsWith("feat(demo): add greeting", fakeGit.Commits[1].Paragraphs[0], StringComparison.Ordinal);
        Assert.Equal(verdict.TestCommit, fakeGit.Commits[0].Sha);
        Assert.Equal(verdict.Head, fakeGit.Commits[1].Sha);
        Assert.Equal(1, fakeGit.EnsureWorktreeCalls);
        Assert.Equal(1, fakeGit.CleanWorktreeCalls);
        Assert.Equal(3, fakeRunner.Invocations.Count); // pre-green, red in worktree, post-green
    }

    [Fact]
    public void ProcessGitClient_StripsModelSecretsFromGitEnvironmentAndRestrictsPushEnvToPush()
    {
        var prevSecret = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var prevSsh = Environment.GetEnvironmentVariable("GIT_SSH_COMMAND");
        try
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-secret-test-key");
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", "ssh -i /tmp/fake-key");

            var capture = new CapturingCommandRunner();
            var git = new ProcessGitClient(capture);

            git.Run(".", ["status"]);
            Assert.NotNull(capture.LastRequest);
            Assert.True(capture.LastRequest!.ClearEnvironment);
            Assert.NotNull(capture.LastRequest.ExtraEnvironment);
            Assert.False(capture.LastRequest.ExtraEnvironment!.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.False(capture.LastRequest.ExtraEnvironment.ContainsKey("GIT_SSH_COMMAND"));
            Assert.Equal("0", capture.LastRequest.ExtraEnvironment["GIT_TERMINAL_PROMPT"]);

            git.Push(".", "origin", "HEAD:refs/heads/change/demo");
            Assert.NotNull(capture.LastRequest);
            Assert.True(capture.LastRequest!.ClearEnvironment);
            Assert.False(capture.LastRequest.ExtraEnvironment!.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.Equal("ssh -i /tmp/fake-key", capture.LastRequest.ExtraEnvironment["GIT_SSH_COMMAND"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prevSecret);
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", prevSsh);
        }
    }

    private sealed record RecordedCommit(string Dir, List<string> Paths, List<string> Paragraphs, string Sha);

    private sealed class RecordingFakeGit(string initialHead, string porcelainZ)
        : IGitQuery, IGitStatus, IGitWorktrees, IGitCommitter
    {
        private int _commitSeq;
        public string HeadSha { get; private set; } = initialHead;
        public List<RecordedCommit> Commits { get; } = [];
        public int EnsureWorktreeCalls { get; private set; }
        public int CleanWorktreeCalls { get; private set; }

        public string? CommitOf(string dir, string rev) => HeadSha;
        public string? ShowFile(string dir, string rev, string path) => null;
        public List<GitFileEntry> ListFiles(string dir, string rev, string path) => [];
        public bool ExistsAt(string dir, string rev, string path) => false;
        public int? CountCommits(string dir, string from, string to, IReadOnlyList<string> paths) => 0;
        public List<TrailerCommit> CommitsWithTrailer(string dir, string from, string to, string key, IReadOnlyList<string>? paths = null) => [];
        public string? LastCommitTouching(string dir, string range, IReadOnlyList<string> pathspec) => null;
        public string? CodeHead(string dir, IReadOnlyList<string> excludeRelativePaths) => HeadSha;
        public string? LastCommitForPath(string dir, string relativePath) => HeadSha;
        public string? RemoteUrl(string dir, string remote) => null;

        public List<string> DirtyPaths(string dir, IReadOnlyList<string>? exclude = null) => [];
        public List<string> ChangedPaths(string dir, IReadOnlyList<string> paths) => [];
        public List<string> TrackedChanges(string dir) => [];
        public List<string> ChangedFilesBetween(string dir, string from, string to, IReadOnlyList<string> pathspec) => [];
        public HashSet<string> ReportedPaths(string top, IReadOnlyList<string> paths) => [];
        public bool DiffersFromHead(string top, string path) => false;
        public bool Differs(string top, IReadOnlyList<string> revs, string path) => false;
        public string PorcelainStatusZ(string dir) => porcelainZ;

        public string? TopLevel(string dir) => dir;
        public string GitPath(string dir, string path, bool common = false) => Path.Combine(dir, path);
        public GitDirsInfo? GitDirs(string dir) => new(dir, dir);
        public string MainCheckout(string dir) => dir;
        public List<GitWorktree> Worktrees(string dir) => [];
        public string? CheckedOutIn(string dir, string branch) => null;
        public GitResult EnsureDetachedWorktree(string repoRoot, string worktreeDir, string rev)
        {
            EnsureWorktreeCalls++;
            Directory.CreateDirectory(worktreeDir);
            return new GitResult(true, 0, "", "");
        }

        public GitResult CleanDetachedWorktree(string worktreeDir, string rev)
        {
            CleanWorktreeCalls++;
            return new GitResult(true, 0, "", "");
        }

        public GitCommitOutcome CommitPaths(string dir, IReadOnlyList<string> paths, IReadOnlyList<string> paragraphs)
        {
            _commitSeq++;
            HeadSha = $"{_commitSeq:D40}";
            Commits.Add(new RecordedCommit(dir, [.. paths], [.. paragraphs], HeadSha));
            return new GitCommitOutcome(true, HeadSha, null, "");
        }

        public GitResult ResetMixed(string dir, string rev)
        {
            HeadSha = rev;
            return new GitResult(true, 0, "", "");
        }

        public GitResult Push(string dir, string remote, string refSpec) => new(true, 0, "", "");
    }

    private sealed class FakeTestRunner(string repoRoot) : IAllowListedCommandRunner
    {
        public List<(string Cwd, string[] Argv)> Invocations { get; } = [];

        public AllowListedCommandResult Run(string workingDirectory, IReadOnlyList<string> argv, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? extraEnv = null)
        {
            Invocations.Add((workingDirectory, [.. argv]));
            var inWorktree = !string.Equals(Path.GetFullPath(workingDirectory), Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase);
            return inWorktree
                ? new AllowListedCommandResult(1, "AssertionError: expected 'hi' to equal 'hello'", false, TimeSpan.FromMilliseconds(5))
                : new AllowListedCommandResult(0, "ok 1 - greet", false, TimeSpan.FromMilliseconds(5));
        }
    }

    private sealed class CapturingCommandRunner : ICommandRunner
    {
        public CommandRequest? LastRequest { get; private set; }

        public CommandOutcome Run(CommandRequest request)
        {
            LastRequest = request;
            return new CommandOutcome(true, false, 0, "", "", null, null);
        }

        public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Run(request));
    }
}
