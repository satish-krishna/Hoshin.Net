using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class LifecycleEventsAndBuildLogTests
{
    private static readonly string HoshinDll =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "hoshin.dll"));

    private const string Change = "2026-11-01-two-tasks";
    private const string Folder = $"intent/demo/{Change}";

    [Fact]
    public void HookDrivenTwoTaskBuild_WritesBuildLogEntriesAndLogsBuildFinishChecks()
    {
        using var tmp = new TempDir("hoshin-a5-twotask-");
        var repoDir = CreateTwoTaskWorkspace(tmp.Path);
        var repo = TestServices.FindRepo(repoDir, Folder)!;
        var orchestrator = TestServices.HookOrchestrator;
        var session = "claude-a5";

        // 1. Start build via hook -> Implement T1.
        var startEvent = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = session,
            cwd = repoDir,
            command_name = "hoshin:build",
        }));
        var (startOut, _) = orchestrator.HandleStart(startEvent, rolesRoot: null);
        Assert.NotNull(startOut);

        var step1 = repo.ReadStep();
        Assert.NotNull(step1);
        Assert.Equal("T1", step1!.Task);

        // 2. Complete T1.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t1.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { one } from '../src/one.mjs';
            test('one returns 1', () => { assert.equal(one(), 1); });
            """);
        TestGitFixture.Write(Path.Combine(repoDir, "src", "one.mjs"), "export const one = () => 1;\n");

        orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = session,
                cwd = repoDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Implement T1\n{step1.DispatchNonce}" },
                tool_response = new { content = "done" },
            })),
            rolesRoot: null);

        var step2 = repo.ReadStep();
        Assert.NotNull(step2);
        Assert.Equal("T2", step2!.Task);

        // 3. Complete T2 -> triggers task.done + build.finish checks -> ReviewDocument.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "t2.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { two } from '../src/two.mjs';
            test('two returns 2', () => { assert.equal(two(), 2); });
            """);
        TestGitFixture.Write(Path.Combine(repoDir, "src", "two.mjs"), "export const two = () => 2;\n");

        orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = session,
                cwd = repoDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Implement T2\n{step2.DispatchNonce}" },
                tool_response = new { content = "done" },
            })),
            rolesRoot: null);

        // 4. Verify `hoshin read --json build/app.md` reports two entries with red: "observed".
        var buildLogRel = $"{Folder}/build/app.md";
        var readRes = RunHoshinCli(repoDir, "read", "--json", buildLogRel);
        Assert.True(readRes.ExitCode == 0, $"hoshin read failed ({readRes.ExitCode}): {readRes.Stderr}\n{readRes.Stdout}");
        using var readDoc = JsonDocument.Parse(readRes.Stdout);
        Assert.Empty(readDoc.RootElement.GetProperty("problems").EnumerateArray());
        var entries = readDoc.RootElement.GetProperty("document").GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal("T1", entries[0].GetProperty("task").GetString());
        Assert.Equal("observed", entries[0].GetProperty("red").GetString());
        Assert.NotEmpty(entries[0].GetProperty("commits").EnumerateArray());
        Assert.Equal("T2", entries[1].GetProperty("task").GetString());
        Assert.Equal("observed", entries[1].GetProperty("red").GetString());
        Assert.NotEmpty(entries[1].GetProperty("commits").EnumerateArray());

        // 5. Verify `hoshin checks --json` reports the build.finish check events.
        var checksRes = RunHoshinCli(repoDir, "checks", "--json", "--change", Change);
        Assert.True(checksRes.ExitCode == 0, $"hoshin checks failed ({checksRes.ExitCode}): {checksRes.Stderr}\n{checksRes.Stdout}");
        using var checksDoc = JsonDocument.Parse(checksRes.Stdout);
        Assert.Empty(checksDoc.RootElement.GetProperty("problems").EnumerateArray());
        var runs = checksDoc.RootElement.GetProperty("runs").EnumerateArray().ToList();
        Assert.Contains(runs, r =>
            r.GetProperty("event").GetString() == "build.finish"
            && r.GetProperty("repo").GetString() == "app"
            && r.GetProperty("checks").EnumerateArray().Any(c =>
                c.GetProperty("check").GetString() == "unit-check"
                && c.GetProperty("outcome").GetString() == "passed"));

        // 6. Verify ledger runs include build.start, task.done (T1 & T2), and build.finish.
        var ledger = repo.ReadLedger();
        Assert.Contains(ledger.Runs, r => r.Event == "build.start" && r.Outcome == "passed");
        Assert.Contains(ledger.Runs, r => r.Event == "task.done" && r.Subject == "T1" && r.Outcome == "passed");
        Assert.Contains(ledger.Runs, r => r.Event == "task.done" && r.Subject == "T2" && r.Outcome == "passed");
        Assert.Contains(ledger.Runs, r => r.Event == "build.finish" && r.Outcome == "passed");

        // 7. Commit review -> fires review.finish on master ledger; Publish -> fires change.done (pushes + opens PR + updates change.jsonc).
        var fakePr = new FakePullRequestClient();
        var services = new Hoshin.Cli.HoshinServices(
            TestServices.SchemaValidator,
            TestServices.CommandRunner,
            TestServices.Git,
            fakePr,
            TestServices.Time,
            TestServices.FileSystem);
        var stateBeforeReview = services.WorkflowStateReader.Read(repo)!;
        var appHead = stateBeforeReview.ChangeState!.Apps!["app"].Head!;
        var shortHead = WorkflowEngine.Short(appHead);
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "build", "review.md"),
            $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-01", "commits": { "app": "{{shortHead}}" } }
            ```

            No findings.
            """);

        var (reviewProblems, reviewCommit) = services.DocumentCommitter.CommitReview(repo, stateBeforeReview);
        Assert.Empty(reviewProblems);
        Assert.NotNull(reviewCommit);

        var masterLedger = services.Ledger.Read(repo.LedgerFolder, "master");
        Assert.NotNull(masterLedger);
        Assert.Contains(masterLedger!.Runs, r => r.Event == "review.finish" && r.Outcome == "passed");

        var stateBeforePublish = services.WorkflowStateReader.Read(repo)!;
        services.ApprovalRecorder.Answer(repo, stateBeforePublish, approved: true, "ship it");
        var stateApproved = services.WorkflowStateReader.Read(repo)!;
        var publishRes = services.Publisher.Publish(repo, stateApproved, $"refs/heads/change/{Change}");
        Assert.True(publishRes.Ok, publishRes.Error);
        Assert.Equal("https://github.com/org/repo/pull/99", publishRes.Published!.PullRequestUrl);

        var masterAfterPublish = services.Ledger.Read(repo.LedgerFolder, "master")!;
        Assert.Contains(masterAfterPublish.Runs, r => r.Event == "change.done" && r.Outcome == "passed");
        var changeJson = File.ReadAllText(Path.Combine(repoDir, Folder, "change.jsonc"));
        Assert.Contains("https://github.com/org/repo/pull/99", changeJson, StringComparison.Ordinal);
    }

    private sealed class FakePullRequestClient : IPullRequestClient
    {
        public string? HostOf(string remote) => "github.com";
        public string? SignInProblem(string cwd, string? host) => null;
        public PullRequestRef? OpenPullRequest(string cwd, string branch) => null;
        public OpenPullRequestResult OpenNew(string cwd, OpenPullRequestInput pr) =>
            new(new PullRequestRef("https://github.com/org/repo/pull/99", 99), null);
        public string? StateOf(string cwd, string url) => PullRequestStates.Open;
    }

    private static string CreateTwoTaskWorkspace(string root)
    {
        var repoDir = Path.Combine(root, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": { "demo": { "apps": { "app": "." } } },
                  "hooks": {
                    "build.finish": [
                      { "id": "unit-check", "when": "before", "run": ["node", "-e", "process.exit(0)"], "blocking": true }
                    ]
                  }
                }
                """,
            ["src/index.mjs"] = "export const ok = true;\n",
        });

        TestGitFixture.Git(repoDir, "checkout", "-q", "-b", $"change/{Change}");
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "change.jsonc"),
            $$"""
            { "version": 1, "change": "{{Change}}", "type": "feat", "scope": "demo", "base": "main", "branch": "change/{{Change}}" }
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "intent.md"),
            """
            ```jsonc
            { "title": "Two tasks", "system": "demo", "author": "t", "date": "2026-11-01", "status": "accepted", "scope": "demo" }
            ```

            # Intent: Two tasks
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept intent {Change}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(repoDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "spec.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Two tasks

            ```gherkin
            Feature: Two tasks

              @R1 @from:intent
              Rule: Return constants

                @R1.1
                Scenario: Returns one
                  Given nothing
                  When one is called
                  Then the result is 1

                @R1.2
                Scenario: Returns two
                  Given nothing
                  When two is called
                  Then the result is 2
            ```
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept spec {Change}", "-m", "Generated-By: hoshin");
        var specSha = TestGitFixture.Git(repoDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "plan.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Two tasks

            ## Tasks

            ### T1: Add one helper

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1.1"], "supports": [], "test": ["node", "--test", "test/t1.test.mjs"] }
            ```

            #### Change
            Add `src/one.mjs`.

            #### Tests
            Add `test/t1.test.mjs`.

            ### T2: Add two helper

            ```jsonc
            { "id": "T2", "repo": "app", "depends_on": ["T1"], "covers": ["R1.2"], "supports": [], "test": ["node", "--test", "test/t2.test.mjs"] }
            ```

            #### Change
            Add `src/two.mjs`.

            #### Tests
            Add `test/t2.test.mjs`.
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept plan {Change}", "-m", "Generated-By: hoshin");

        return repoDir;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunHoshinCli(string cwd, params string[] args)
    {
        var runner = new ProcessCommandRunner();
        var argv = new List<string>(args.Length + 2) { "dotnet", HoshinDll };
        argv.AddRange(args);
        var res = runner.Run(new CommandRequest(cwd, argv));
        return (res.ExitCode ?? -1, res.Stdout, res.Stderr);
    }
}
