using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R04 (E14, E15): Real-process tests proving the detached Red worktree runs a task's <c>compile</c>
/// command before running the test, rejects non-missing-symbol compile failures as invalid Red,
/// and makes gitignored dependency folders (<c>node_modules</c>, <c>.venv</c>) visible without copying or committing them.
/// </summary>
public sealed class RedCompileAndToolsTests
{
    [Fact]
    public void RedWorktree_RunsCompileWithGitignoredOutput_GivesValidRedAndCommitsTask()
    {
        using var tmp = new TempDir("hoshin-r04-compile-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["package.json"] = "{ \"name\": \"r04-compile\", \"type\": \"module\", \"private\": true }\n",
            [".gitignore"] = "dist/\n",
            ["build.mjs"] = """
                import { cpSync, mkdirSync } from 'node:fs';
                mkdirSync('dist', { recursive: true });
                cpSync('src', 'dist', { recursive: true });
                """,
            ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n",
            ["test/add.test.mjs"] = """
                import test from 'node:test';
                import assert from 'node:assert/strict';
                import { add } from '../dist/calc.mjs';
                test('add', () => { assert.equal(add(2, 3), 5); });
                """,
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "r04-compile", "type": "feat", "scope": "calc", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "calc", "status": "accepted" }
                ```

                # Plan: Compile red check

                ## Tasks

                ### T1: Multiply

                ```jsonc
                {
                  "id": "T1",
                  "repo": "repo",
                  "compile": ["node", "build.mjs"],
                  "test": ["node", "--test", "test/multiply.test.mjs"]
                }
                ```

                #### Change
                Add `multiply(a, b)` to `src/calc.mjs`.

                #### Tests
                Test `multiply(3, 4) === 12` against `../dist/calc.mjs`.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;
        var step = WorkflowEngine.Next(state);
        TestServices.WorkflowRunner.Issue(repo, state, step, "sess-r04");

        // Subagent writes test importing ../dist/calc.mjs and implements multiply in src/calc.mjs.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "multiply.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { multiply } from '../dist/calc.mjs';
            test('multiply', () => { assert.equal(multiply(3, 4), 12); });
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "calc.mjs"),
            "export const add = (a, b) => a + b;\nexport const multiply = (a, b) => a * b;\n");

        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, state, "T1", 1);

        Assert.True(verdict.Passed, $"Expected T1 to pass Red/Green with compile step; reasons: {string.Join("; ", verdict.Reasons)}; red: {verdict.Red}");
        Assert.Contains("observed", verdict.Red, StringComparison.Ordinal);
        var committedFiles = TestGitFixture.Git(repoDir, "ls-tree", "-r", "--name-only", "HEAD").Split('\n');
        Assert.DoesNotContain(committedFiles, f => f.StartsWith("dist/", StringComparison.Ordinal));
    }

    [Fact]
    public void RedWorktree_SeesGitignoredNodeModulesTool_GivesValidRedAndNeverCommitsNodeModules()
    {
        using var tmp = new TempDir("hoshin-r04-tools-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["package.json"] = "{ \"name\": \"r04-tools\", \"type\": \"module\", \"private\": true }\n",
            [".gitignore"] = "node_modules/\n",
            ["src/greet.mjs"] = "export const greet = (name) => `Hello, ${name}`;\n",
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "r04-tools", "type": "feat", "scope": "web", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "web", "status": "accepted" }
                ```

                # Plan: Gitignored tool red check

                ## Tasks

                ### T2: Shout

                ```jsonc
                {
                  "id": "T2",
                  "repo": "repo",
                  "test": ["node", "node_modules/testkit/run.mjs", "test/shout.test.mjs"]
                }
                ```

                #### Change
                Add `shout(name)` to `src/greet.mjs`.

                #### Tests
                Test `shout('Ada') === 'Hello, Ada!'`.
                """,
        });

        // Gitignored installed dev tool in main checkout.
        TestGitFixture.Write(
            Path.Combine(repoDir, "node_modules", "testkit", "run.mjs"),
            """
            import { spawnSync } from 'node:child_process';
            const r = spawnSync(process.execPath, ['--test', ...process.argv.slice(2)], { stdio: 'inherit' });
            process.exit(r.status ?? 1);
            """);

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;
        var step = WorkflowEngine.Next(state);
        TestServices.WorkflowRunner.Issue(repo, state, step, "sess-r04-tools");

        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "shout.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout', () => { assert.equal(shout('Ada'), 'Hello, Ada!'); });
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "greet.mjs"),
            "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name)}!`;\n");

        var verdict = TestServices.WorkflowRunner.EvaluateAndCommitTask(repo, state, "T2", 1);

        Assert.True(verdict.Passed, $"Expected T2 to pass Red/Green using node_modules tool; reasons: {string.Join("; ", verdict.Reasons)}; red: {verdict.Red}");
        Assert.Contains("observed", verdict.Red, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(repoDir, "node_modules", "testkit", "run.mjs")), "Main checkout node_modules must remain intact after Red cleanup.");
        var committedFiles = TestGitFixture.Git(repoDir, "ls-tree", "-r", "--name-only", "HEAD").Split('\n');
        Assert.DoesNotContain(committedFiles, f => f.StartsWith("node_modules/", StringComparison.Ordinal));
    }

    [Fact]
    public void RedWorktree_RejectsNonMissingSymbolCompileFailure()
    {
        using var tmp = new TempDir("hoshin-r04-badcompile-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["build.mjs"] = "console.error('build broken: missing config'); process.exit(1);\n",
            ["src/a.mjs"] = "export const a = 1;\n",
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "r04-badcompile", "type": "feat", "scope": "a", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "a", "status": "accepted" }
                ```

                # Plan: Bad compile

                ## Tasks

                ### T1: Broken compile

                ```jsonc
                {
                  "id": "T1",
                  "repo": "repo",
                  "compile": ["node", "build.mjs"],
                  "test": ["node", "--test", "test/a.test.mjs"]
                }
                ```

                #### Change
                Fix build.

                #### Tests
                Test a.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var red = RedWorktree.ObserveRed(
            repo,
            TestServices.Git,
            TestServices.AllowListedRunner,
            repo.Head(),
            ["node", "--test", "test/a.test.mjs"],
            [],
            ["src/a.mjs"],
            null,
            TimeSpan.FromSeconds(30),
            ["node", "build.mjs"]);

        Assert.False(red.IsValidRed);
    }

    [Fact]
    public void RedWorktree_ConfiguredBuildLinkDirsAndCompileClassificationBranches()
    {
        using var tmp = new TempDir("hoshin-r04-linkdirs-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".gitignore"] = "custom_tools/\n",
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "build": {
                    "linkDirs": ["custom_tools", " ", "../escape"]
                  }
                }
                """,
            ["src/a.mjs"] = "export const a = 1;\n",
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "r04-linkdirs", "type": "feat", "scope": "a", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "a", "status": "accepted" }
                ```

                # Plan: Custom linkDirs

                ## Tasks

                ### T1: Check linkDirs

                ```jsonc
                { "id": "T1", "repo": "repo", "compile": ["node", "build.mjs"], "test": ["node", "--test", "test/a.test.mjs"] }
                ```

                #### Change
                Change.

                #### Tests
                Test.
                """,
        });
        TestGitFixture.Write(Path.Combine(repoDir, "custom_tools", "tool.txt"), "ok");

        var repo = TestServices.FindRepo(repoDir)!;
        var plan = TestServices.ReadPlan(repo)!;
        Assert.NotNull(plan.LinkDirs);
        Assert.Contains("custom_tools", plan.LinkDirs!);

        // Compile failure classified as MissingSymbol is valid Red; syntax error compile failure is invalid Red.
        var missingSymbolRed = RedWorktree.ObserveRed(
            repo,
            TestServices.Git,
            new FixedRunner(new AllowListedCommandResult(1, "FooTests.cs(5,10): error CS0246: The type or namespace name 'Missing' could not be found", false, TimeSpan.Zero)),
            repo.Head(),
            ["node", "--test"],
            [],
            ["src/a.mjs"],
            null,
            TimeSpan.FromSeconds(10),
            ["dotnet", "build"],
            plan.LinkDirs);
        Assert.True(missingSymbolRed.IsValidRed);
        Assert.Equal(RedKind.MissingSymbol, missingSymbolRed.Kind);

        var syntaxRed = RedWorktree.ObserveRed(
            repo,
            TestServices.Git,
            new FixedRunner(new AllowListedCommandResult(1, "FooTests.cs(5,10): error CS1002: ; expected", false, TimeSpan.Zero)),
            repo.Head(),
            ["node", "--test"],
            [],
            ["src/a.mjs"],
            null,
            TimeSpan.FromSeconds(10),
            ["dotnet", "build"],
            plan.LinkDirs);
        Assert.False(syntaxRed.IsValidRed);
        Assert.Equal(RedKind.SyntaxError, syntaxRed.Kind);
    }

    private sealed class FixedRunner(AllowListedCommandResult result) : IAllowListedCommandRunner
    {
        public AllowListedCommandResult Run(string workingDirectory, IReadOnlyList<string> argv, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? additionalEnvironment = null) =>
            result;
    }
}
