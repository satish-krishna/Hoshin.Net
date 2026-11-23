using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class UnifiedRedRuleTests
{
    private static readonly string HoshinDll =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "hoshin.dll"));

    public static IEnumerable<object[]> SharedRedTable()
    {
        yield return ["assertion-failure", 1, "AssertionError: expected 1 to equal 2", false, true, "failed"];
        yield return ["missing-symbol-cs", 1, "FooTests.cs(5,10): error CS0246: The type or namespace name 'Widget' could not be found", false, true, "failed"];
        yield return ["passed-unexpectedly", 0, "ok 1 - passes", false, false, "passed"];
        yield return ["syntax-error-js", 1, "SyntaxError: Unexpected token '}'", false, false, "could-not-compile"];
        yield return ["syntax-error-cs", 1, "FooTests.cs(5,10): error CS1002: ; expected", false, false, "could-not-compile"];
        yield return ["could-not-run-127", 127, "command not found", false, false, "could-not-run"];
        yield return ["missing-bare-module", 1, "Error: Cannot find module 'nonexistent-third-party-pkg'", false, false, "could-not-run"];
    }

    [Theory]
    [MemberData(nameof(SharedRedTable))]
    public void RedRecordCli_And_TaskEvaluator_AgreeOnEveryRowOfSharedTable(
        string caseName,
        int exitCode,
        string output,
        bool timedOut,
        bool expectedObserved,
        string expectedOutcome)
    {
        // 1. Evaluate via TaskEvaluator (fake git + fake test runner returning the row's output in Red worktree).
        var evaluatorPassed = EvaluateViaTaskEvaluator(caseName, exitCode, output, timedOut);
        Assert.Equal(expectedObserved, evaluatorPassed);

        // 2. Evaluate via black-box `dotnet hoshin.dll red record --json` running a node script emitting the exact output/exitCode.
        var (cliOutcome, cliExitCode) = RunRedRecordCli(exitCode, output);
        Assert.Equal(expectedOutcome, cliOutcome);
        var cliObserved = cliOutcome == "failed";
        Assert.Equal(expectedObserved, cliObserved);
        Assert.Equal(expectedObserved ? 0 : (expectedOutcome == "could-not-run" ? 2 : 1), cliExitCode);
    }

    [Fact]
    public void RedRecordCli_CompileMissingSymbolCountsAsObservedRed_WhileSyntaxErrorDoesNot()
    {
        var (missingOutcome, missingExit) = RunRedRecordCliWithCompile(
            "FooTests.cs(5,10): error CS0246: The type or namespace name 'Widget' could not be found");
        Assert.Equal("failed", missingOutcome);
        Assert.Equal(0, missingExit);

        var (syntaxOutcome, syntaxExit) = RunRedRecordCliWithCompile(
            "FooTests.cs(5,10): error CS1002: ; expected");
        Assert.Equal("could-not-compile", syntaxOutcome);
        Assert.Equal(1, syntaxExit);
    }

    private static (string Outcome, int ExitCode) RunRedRecordCliWithCompile(string compileOutput)
    {
        using var ws = TestGitFixture.CreateWorkspace(built: false);
        var compileScript = $"process.stderr.write({JsonSerializer.Serialize(compileOutput + "\n")}); process.exit(1);";
        var compileArgv = JsonSerializer.Serialize(new[] { "node", "-e", compileScript });
        var testArgv = JsonSerializer.Serialize(new[] { "node", "-e", "process.exit(1)" });
        var planPath = Path.Combine(ws.Master, "intent", "demo", TestGitFixture.ChangeName, "plan.md");
        var planText = $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted" }
            ```

            # Plan: Demo

            ## Tasks

            ### T1: Check

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": [], "supports": [], "compile": {{compileArgv}}, "test": {{testArgv}} }
            ```

            #### Change
            Change app.

            #### Tests
            Run test.
            """;
        TestGitFixture.Write(planPath, planText);
        TestGitFixture.Git(ws.Master, "add", ".");
        TestGitFixture.Git(ws.Master, "commit", "-q", "-m", "docs(demo): accept plan");

        TestGitFixture.Git(ws.App, "checkout", "-q", "-b", $"change/{TestGitFixture.ChangeName}");
        TestGitFixture.Write(Path.Combine(ws.App, "t1.test.mjs"), "// test\n");
        TestGitFixture.Git(ws.App, "add", ".");
        TestGitFixture.Git(ws.App, "commit", "-q", "-m", "test(demo): add test", "-m", "Plan-Task: T1");

        var res = TestServices.CommandRunner.Run(new CommandRequest(
            ws.App,
            ["dotnet", HoshinDll, "red", "record", "--json", "--change", TestGitFixture.ChangeName, "--repo", "app", "--task", "T1"]));

        Assert.True(res.Started, res.StartError);
        var lastLine = res.Stdout.Trim().Split('\n').Last();
        using var doc = JsonDocument.Parse(lastLine);
        return (doc.RootElement.GetProperty("outcome").GetString() ?? "", res.ExitCode ?? -1);
    }

    private static bool EvaluateViaTaskEvaluator(string caseName, int exitCode, string output, bool timedOut)
    {
        using var tmp = new TempDir($"hoshin-a2-eval-{caseName}-");
        var repoRoot = Path.Combine(tmp.Path, "repo");
        var gitDir = Path.Combine(tmp.Path, "git");
        var changeDir = Path.Combine(repoRoot, ".hoshin");
        Directory.CreateDirectory(Path.Combine(repoRoot, "test"));
        Directory.CreateDirectory(Path.Combine(repoRoot, "src"));
        Directory.CreateDirectory(changeDir);
        File.WriteAllText(Path.Combine(repoRoot, "test", "t1.test.mjs"), "// test\n");
        File.WriteAllText(Path.Combine(repoRoot, "src", "app.mjs"), "export const ok = true;\n");

        var fakeGit = new MinimalFakeGit("1111111111111111111111111111111111111111", "?? test/t1.test.mjs\0?? src/app.mjs\0");
        var runner = new TableTestRunner(repoRoot, exitCode, output, timedOut);
        var fs = new PhysicalFileSystem();
        var ledgerStore = new Hoshin.Workflow.Events.LedgerStore(fakeGit, fs);
        var repo = new WorkflowRepo(repoRoot, gitDir, changeDir, fakeGit, fakeGit, fs, ledgerStore);

        var task = new WorkflowTask("T1", "task", "body", ["fake-test", "test/t1.test.mjs"]);
        var plan = new WorkflowPlan("c1", "demo", "main", [task], []);
        var missingDoc = new WorkflowDocState(Path.Combine(changeDir, "intent.md"), false, false, false, null, null);
        var state = new WorkflowState(repoRoot, changeDir, plan, fakeGit.HeadSha, Hoshin.Workflow.Events.LedgerStore.Empty(), null, missingDoc, missingDoc, missingDoc);

        var evaluator = new TaskEvaluator(fakeGit, fakeGit, fakeGit, fakeGit, runner, new BuildLogWriter(fs), new NullWorkflowEventBridge(), new IdentityTaskRepoResolver(), TimeProvider.System);
        var verdict = evaluator.EvaluateAndCommitTask(repo, state, "T1", 1);
        return verdict.Passed;
    }

    private static (string Outcome, int ExitCode) RunRedRecordCli(int exitCode, string output)
    {
        using var ws = TestGitFixture.CreateWorkspace(built: false);
        var script = $"process.stderr.write({JsonSerializer.Serialize(output + "\n")}); process.exit({exitCode});";
        var testArgv = JsonSerializer.Serialize(new[] { "node", "-e", script });
        var planPath = Path.Combine(ws.Master, "intent", "demo", TestGitFixture.ChangeName, "plan.md");
        var planText = $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted" }
            ```

            # Plan: Demo

            ## Tasks

            ### T1: Check

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": [], "supports": [], "test": {{testArgv}} }
            ```

            #### Change
            Change app.

            #### Tests
            Run test.
            """;
        TestGitFixture.Write(planPath, planText);
        TestGitFixture.Git(ws.Master, "add", ".");
        TestGitFixture.Git(ws.Master, "commit", "-q", "-m", "docs(demo): accept plan");

        TestGitFixture.Git(ws.App, "checkout", "-q", "-b", $"change/{TestGitFixture.ChangeName}");
        TestGitFixture.Write(Path.Combine(ws.App, "t1.test.mjs"), "// test\n");
        TestGitFixture.Git(ws.App, "add", ".");
        TestGitFixture.Git(ws.App, "commit", "-q", "-m", "test(demo): add test", "-m", "Plan-Task: T1");

        var res = TestServices.CommandRunner.Run(new CommandRequest(
            ws.App,
            ["dotnet", HoshinDll, "red", "record", "--json", "--change", TestGitFixture.ChangeName, "--repo", "app", "--task", "T1"]));

        Assert.True(res.Started, res.StartError);
        var lastLine = res.Stdout.Trim().Split('\n').Last();
        using var doc = JsonDocument.Parse(lastLine);
        var outcome = doc.RootElement.GetProperty("outcome").GetString() ?? "";
        return (outcome, res.ExitCode ?? -1);
    }

    private sealed class TableTestRunner(string repoRoot, int redExit, string redOutput, bool redTimedOut) : IAllowListedCommandRunner
    {
        public AllowListedCommandResult Run(string workingDirectory, IReadOnlyList<string> argv, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? extraEnv = null)
        {
            var inWorktree = !string.Equals(Path.GetFullPath(workingDirectory), Path.GetFullPath(repoRoot), StringComparison.OrdinalIgnoreCase);
            return inWorktree
                ? new AllowListedCommandResult(redExit, redOutput, redTimedOut, TimeSpan.FromMilliseconds(5))
                : new AllowListedCommandResult(0, "ok", false, TimeSpan.FromMilliseconds(5));
        }
    }

    private sealed class MinimalFakeGit(string initialHead, string porcelainZ)
        : IGitQuery, IGitStatus, IGitWorktrees, IGitCommitter
    {
        private int _seq;
        public string HeadSha { get; private set; } = initialHead;

        public string? CommitOf(string dir, string rev) => HeadSha;
        public string? ShowFile(string dir, string rev, string path) => null;
        public List<GitFileEntry> ListFiles(string dir, string rev, string path) => [];
        public bool ExistsAt(string dir, string rev, string path) => false;
        public int? CountCommits(string dir, string from, string to, IReadOnlyList<string> paths) => 0;
        public List<TrailerCommit> CommitsWithTrailer(string dir, string from, string to, string key, IReadOnlyList<string>? paths = null) => [];
        public string? LastCommitTouching(string dir, string range, IReadOnlyList<string> pathspec) => null;
        public string? CodeHead(string dir, string excludeRelativePath) => HeadSha;
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
            Directory.CreateDirectory(worktreeDir);
            return new GitResult(true, 0, "", "");
        }

        public GitResult CleanDetachedWorktree(string worktreeDir, string rev) => new(true, 0, "", "");
        public GitCommitOutcome CommitPaths(string dir, IReadOnlyList<string> paths, IReadOnlyList<string> paragraphs)
        {
            HeadSha = $"{++_seq:D40}";
            return new GitCommitOutcome(true, HeadSha, null, "");
        }

        public GitResult ResetMixed(string dir, string rev)
        {
            HeadSha = rev;
            return new GitResult(true, 0, "", "");
        }

        public GitResult Push(string dir, string remote, string refSpec) => new(true, 0, "", "");
    }
}
