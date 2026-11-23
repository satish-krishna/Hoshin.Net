using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class TaskPathIsolationTests
{
    private static (TempDir Temp, string RepoDir, WorkflowRepo Repo) CreateFixture(string testTargetPath = "src/math.mjs")
    {
        var tmp = new TempDir("hoshin-t08-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [testTargetPath] = "export function add(a, b) { return a + b; }\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t08-paths", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Math

                ## Tasks

                ### T1: Add sub

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                ```

                #### Change
                Add `sub(a, b)`.

                #### Tests
                Verify `sub(5, 2) === 3`.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        return (tmp, repoDir, repo);
    }

    [Fact]
    public void EvaluateAndCommitTask_ExcludesPreExistingUntrackedFilesFromCommits()
    {
        var (tmp, repoDir, repo) = CreateFixture();
        using var _ = tmp;

        // Pre-existing untracked notes.txt before issuing T1.
        TestGitFixture.Write(Path.Combine(repoDir, "notes.txt"), "local scratch notes\n");

        var runner = TestServices.WorkflowRunner;
        var state = TestServices.ReadState(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        runner.Issue(repo, state, step, "s1");

        // Agent performs valid T1 work.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "math.mjs"),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

        var verdict = runner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));

        var testCommitFiles = TestGitFixture.Git(repoDir, "show", "--name-only", "--format=", verdict.TestCommit!);
        var prodCommitFiles = TestGitFixture.Git(repoDir, "show", "--name-only", "--format=", verdict.Head);

        Assert.DoesNotContain("notes.txt", testCommitFiles, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.txt", prodCommitFiles, StringComparison.Ordinal);
        Assert.Contains("test/t1.test.mjs", testCommitFiles, StringComparison.Ordinal);
        Assert.Contains("src/math.mjs", prodCommitFiles, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateAndCommitTask_RefusesWhenIndexHadStagedChangesBeforeIssue()
    {
        var (tmp, repoDir, repo) = CreateFixture();
        using var _ = tmp;

        // Pre-existing untracked notes.txt AND staged unrelated docs/x.md before issuing T1.
        TestGitFixture.Write(Path.Combine(repoDir, "notes.txt"), "scratch\n");
        TestGitFixture.Write(Path.Combine(repoDir, "docs", "x.md"), "# unrelated\n");
        TestGitFixture.Git(repoDir, "add", "docs/x.md");

        var startHead = repo.Head();
        var runner = TestServices.WorkflowRunner;
        var state = TestServices.ReadState(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        runner.Issue(repo, state, step, "s1");

        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "math.mjs"),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

        var verdict = runner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);
        Assert.False(verdict.Passed);
        Assert.Contains("git: the index had staged changes before the attempt", verdict.Reasons);
        Assert.Equal(startHead, repo.Head());
    }

    [Fact]
    public void EvaluateAndCommitTask_CommitsPathWithSpacesAndQuotesCorrectly()
    {
        // Windows NTFS disallows literal double quotes in filenames; use single quote on Windows and double quote on Unix.
        var prodRel = OperatingSystem.IsWindows() ? "src/a 'b'.mjs" : "src/a \"b\".mjs";
        var importSpec = OperatingSystem.IsWindows() ? "../src/a 'b'.mjs" : "../src/a \"b\".mjs";

        var (tmp, repoDir, repo) = CreateFixture(prodRel);
        using var _ = tmp;

        var runner = TestServices.WorkflowRunner;
        var state = TestServices.ReadState(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        runner.Issue(repo, state, step, "s1");

        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            $"import test from 'node:test'; import assert from 'node:assert/strict'; import {{ sub }} from {System.Text.Json.JsonSerializer.Serialize(importSpec)}; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, prodRel),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

        var verdict = runner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));

        var prodFiles = TestGitFixture.Git(repoDir, "-c", "core.quotepath=off", "show", "--name-only", "--format=", verdict.Head);
        Assert.Equal(prodRel, prodFiles.Trim());
    }

    [Fact]
    public void ParsePorcelainZ_HandlesSpacesDoubleQuotesAndRenames()
    {
        var raw = " M src/a \"b\".mjs\0R  src/new name.mjs\0src/old name.mjs\0?? notes.txt\0A  docs/x.md\0\r\n";
        var parsed = WorkingTreeSnapshot.ParsePorcelainZ(raw);

        Assert.Collection(
            parsed,
            c =>
            {
                Assert.Equal("src/a \"b\".mjs", c.Path);
                Assert.False(c.Deleted);
                Assert.False(c.Staged);
            },
            c =>
            {
                Assert.Equal("src/old name.mjs", c.Path);
                Assert.True(c.Deleted);
                Assert.True(c.Staged);
            },
            c =>
            {
                Assert.Equal("src/new name.mjs", c.Path);
                Assert.False(c.Deleted);
                Assert.True(c.Staged);
            },
            c =>
            {
                Assert.Equal("notes.txt", c.Path);
                Assert.False(c.Deleted);
                Assert.False(c.Staged);
            },
            c =>
            {
                Assert.Equal("docs/x.md", c.Path);
                Assert.False(c.Deleted);
                Assert.True(c.Staged);
            });
    }

    [Fact]
    public void EvaluateAndCommitTask_ExcludesUnchangedPreExistingDirtyTrackedFile()
    {
        var (tmp, repoDir, repo) = CreateFixture();
        using var _ = tmp;

        TestGitFixture.Write(Path.Combine(repoDir, "README.md"), "# initial\n");
        TestGitFixture.Git(repoDir, "add", "README.md");
        TestGitFixture.Git(repoDir, "commit", "-m", "docs: add readme");

        // Dirty tracked README.md before issuing T1 (unstaged).
        TestGitFixture.Write(Path.Combine(repoDir, "README.md"), "# local tweak\n");

        var runner = TestServices.WorkflowRunner;
        var state = TestServices.ReadState(repo)!;
        var step = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        runner.Issue(repo, state, step, "s1");

        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            "import test from 'node:test'; import assert from 'node:assert/strict'; import { sub } from '../src/math.mjs'; test('sub', () => assert.equal(sub(5, 2), 3));\n");
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "math.mjs"),
            "export function add(a, b) { return a + b; }\nexport function sub(a, b) { return a - b; }\n");

        var verdict = runner.EvaluateAndCommitTask(repo, TestServices.ReadState(repo)!, "T1", 1);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));

        var prodFiles = TestGitFixture.Git(repoDir, "show", "--name-only", "--format=", verdict.Head);
        Assert.DoesNotContain("README.md", prodFiles, StringComparison.Ordinal);
        Assert.Contains("src/math.mjs", prodFiles, StringComparison.Ordinal);
    }
}
