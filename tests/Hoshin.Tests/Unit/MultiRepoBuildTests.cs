using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class MultiRepoBuildTests
{
    private const string Change = "2026-11-01-multi";
    private const string Folder = $"intent/demo/{Change}";

    [Fact]
    public void HookDrivenMultiRepoBuild_CommitsInEachRepoHistoryAndWritesBuildLogPerRepo()
    {
        using var tmp = new TempDir("hoshin-a6-multi-");
        var (masterDir, appDir, apiDir) = CreateTwoRepoWorkspace(tmp.Path);

        var masterRepo = TestServices.FindRepo(masterDir, Folder)!;
        var orchestrator = TestServices.HookOrchestrator;
        var session = "claude-a6";

        // 1. Start build from master -> Implement T1 (in app).
        var (startOut, _) = orchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptSubmit",
                session_id = session,
                cwd = masterDir,
                command_name = "hoshin:build",
            })),
            rolesRoot: null);
        Assert.NotNull(startOut);
        Assert.Contains(appDir, ExtractContext(startOut!), StringComparison.OrdinalIgnoreCase);

        var step1 = masterRepo.ReadStep();
        Assert.NotNull(step1);
        Assert.Equal("T1", step1!.Task);

        // 2. Subagent implements T1 inside app checkout.
        TestGitFixture.Write(
            Path.Combine(appDir, "test", "app.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { greetApp } from '../src/app.mjs';
            test('app greets', () => { assert.equal(greetApp(), 'app-ok'); });
            """);
        TestGitFixture.Write(Path.Combine(appDir, "src", "app.mjs"), "export const greetApp = () => 'app-ok';\n");

        var (res1Out, _) = orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = session,
                cwd = masterDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Implement T1\n{step1.DispatchNonce}" },
                tool_response = new { content = "done" },
            })),
            rolesRoot: null);
        Assert.NotNull(res1Out);
        Assert.Contains(apiDir, ExtractContext(res1Out!), StringComparison.OrdinalIgnoreCase);

        var step2 = masterRepo.ReadStep();
        Assert.NotNull(step2);
        Assert.Equal("T2", step2!.Task);

        // 3. Subagent implements T2 inside api checkout.
        TestGitFixture.Write(
            Path.Combine(apiDir, "test", "api.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { greetApi } from '../src/api.mjs';
            test('api greets', () => { assert.equal(greetApi(), 'api-ok'); });
            """);
        TestGitFixture.Write(Path.Combine(apiDir, "src", "api.mjs"), "export const greetApi = () => 'api-ok';\n");

        orchestrator.HandleResult(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = session,
                cwd = masterDir,
                tool_name = "Agent",
                tool_input = new { prompt = $"Implement T2\n{step2.DispatchNonce}" },
                tool_response = new { content = "done" },
            })),
            rolesRoot: null);

        // 4. Each product repository has its own branch and test+feat commits.
        Assert.Equal($"change/{Change}", TestGitFixture.Git(appDir, "branch", "--show-current"));
        Assert.Equal($"change/{Change}", TestGitFixture.Git(apiDir, "branch", "--show-current"));

        var appLog = TestGitFixture.Git(appDir, "log", "main..HEAD", "--format=%s%n%b");
        Assert.Contains("test(demo): Add app helper", appLog, StringComparison.Ordinal);
        Assert.Contains("feat(demo): Add app helper", appLog, StringComparison.Ordinal);
        Assert.Contains("Plan-Task: T1", appLog, StringComparison.Ordinal);

        var apiLog = TestGitFixture.Git(apiDir, "log", "main..HEAD", "--format=%s%n%b");
        Assert.Contains("test(demo): Add api helper", apiLog, StringComparison.Ordinal);
        Assert.Contains("feat(demo): Add api helper", apiLog, StringComparison.Ordinal);
        Assert.Contains("Plan-Task: T2", apiLog, StringComparison.Ordinal);

        // 5. Master holds one valid build log per repository.
        var appBuildLogPath = Path.Combine(masterDir, Folder, "build", "app.md");
        var apiBuildLogPath = Path.Combine(masterDir, Folder, "build", "api.md");
        Assert.True(File.Exists(appBuildLogPath), $"Expected build log at {appBuildLogPath}");
        Assert.True(File.Exists(apiBuildLogPath), $"Expected build log at {apiBuildLogPath}");

        var appBuildLog = TestServices.BuildLogReader.Read(File.ReadAllText(appBuildLogPath), appBuildLogPath);
        Assert.Empty(appBuildLog.Problems);
        var t1Entry = Assert.Single(appBuildLog.Document.Entries);
        Assert.Equal("T1", t1Entry.Task);
        Assert.Equal("observed", t1Entry.Red);

        var apiBuildLog = TestServices.BuildLogReader.Read(File.ReadAllText(apiBuildLogPath), apiBuildLogPath);
        Assert.Empty(apiBuildLog.Problems);
        var t2Entry = Assert.Single(apiBuildLog.Document.Entries);
        Assert.Equal("T2", t2Entry.Task);
        Assert.Equal("observed", t2Entry.Red);

        // 6. Separate ledgers exist per repository in master/.git/hoshin/demo/<change>/.
        Assert.True(File.Exists(Path.Combine(masterRepo.LedgerFolder, "app.json")));
        Assert.True(File.Exists(Path.Combine(masterRepo.LedgerFolder, "api.json")));

        // 7. After both repos pass build.finish, Next advances to ReviewDocument.
        var finalState = TestServices.ReadState(masterRepo)!;
        Assert.IsType<ReviewDocument>(WorkflowEngine.Next(finalState));
    }

    [Fact]
    public void Next_OrdersMultiRepoTasksByScheduleDependencies()
    {
        var t1Web = new WorkflowTask("T1", "Web consumer", "Use api", ["node", "-e", "process.exit(0)"], Repo: "web", DependsOn: ["T2"]);
        var t2Api = new WorkflowTask("T2", "API provider", "Add endpoint", ["node", "-e", "process.exit(0)"], Repo: "api", DependsOn: []);
        var plan = new WorkflowPlan("c1", "demo", "main", [t1Web, t2Api], []);
        var missingDoc = new WorkflowDocState("/repo/intent.md", false, false, false, null, null);
        var acceptedPlan = new WorkflowDocState("/repo/plan.md", true, true, false, "accepted", "accepted");

        var state = new WorkflowState(
            "/ws/master",
            "/ws/master/intent/demo/c1",
            plan,
            "head1",
            Hoshin.Workflow.Events.LedgerStore.Empty(),
            null,
            missingDoc,
            missingDoc,
            acceptedPlan,
            RepoPaths: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["web"] = "/ws/web",
                ["api"] = "/ws/api",
            });

        // Even though T1 appears first in plan.Tasks, it depends on T2 in api -> Next issues T2 first in /ws/api!
        var first = Assert.IsType<Implement>(WorkflowEngine.Next(state));
        Assert.Equal("T2", first.Agent.Task);
        Assert.Contains("/ws/api", first.Agent.Brief, StringComparison.Ordinal);

        // Once T2 passes in api's ledger, Next issues T1 in /ws/web!
        var apiLedger = Hoshin.Workflow.Events.LedgerStore.Empty("demo", "c1", "api", "/ws/api") with
        {
            Verdicts = [new WorkflowVerdict("T2", 1, "api1234", true, "observed", [], DateTimeOffset.UtcNow)],
        };
        var afterApi = state with
        {
            RepoLedgers = new Dictionary<string, Hoshin.Workflow.Events.RepoLedger>(StringComparer.Ordinal) { ["api"] = apiLedger },
        };
        var second = Assert.IsType<Implement>(WorkflowEngine.Next(afterApi));
        Assert.Equal("T1", second.Agent.Task);
        Assert.Contains("/ws/web", second.Agent.Brief, StringComparison.Ordinal);
    }

    private static (string Master, string App, string Api) CreateTwoRepoWorkspace(string root)
    {
        var masterDir = Path.Combine(root, "master");
        var appDir = Path.Combine(root, "app");
        var apiDir = Path.Combine(root, "api");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": {
                    "demo": {
                      "repos": ["app", "api"]
                    }
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(appDir, new Dictionary<string, string> { ["README.md"] = "# app\n" });
        TestGitFixture.InitRepo(apiDir, new Dictionary<string, string> { ["README.md"] = "# api\n" });

        TestGitFixture.Git(masterDir, "checkout", "-q", "-b", $"change/{Change}");
        TestGitFixture.Write(
            Path.Combine(masterDir, Folder, "change.jsonc"),
            $$"""
            { "version": 1, "change": "{{Change}}", "type": "feat", "scope": "demo", "base": "main", "branch": "change/{{Change}}" }
            """);
        TestGitFixture.Write(
            Path.Combine(masterDir, Folder, "intent.md"),
            """
            ```jsonc
            { "title": "Multi repo", "system": "demo", "author": "t", "date": "2026-11-01", "status": "accepted", "scope": "demo" }
            ```

            # Intent: Multi repo
            """);
        TestGitFixture.Git(masterDir, "add", ".");
        TestGitFixture.Git(masterDir, "commit", "-q", "-m", $"docs(demo): accept intent {Change}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(masterDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(masterDir, Folder, "spec.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Multi repo

            ```gherkin
            Feature: Multi repo

              @R1 @from:intent
              Rule: App and API helpers

                @R1.1
                Scenario: App greets
                  Given app
                  When called
                  Then returns app-ok

                @R1.2
                Scenario: API greets
                  Given api
                  When called
                  Then returns api-ok
            ```
            """);
        TestGitFixture.Git(masterDir, "add", ".");
        TestGitFixture.Git(masterDir, "commit", "-q", "-m", $"docs(demo): accept spec {Change}", "-m", "Generated-By: hoshin");
        var specSha = TestGitFixture.Git(masterDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(masterDir, Folder, "plan.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Multi repo

            ## Tasks

            ### T1: Add app helper

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1.1"], "supports": [], "test": ["node", "--test", "test/app.test.mjs"] }
            ```

            #### Change
            Add `src/app.mjs` in `app`.

            #### Tests
            Add `test/app.test.mjs` in `app`.

            ### T2: Add api helper

            ```jsonc
            { "id": "T2", "repo": "api", "depends_on": ["T1"], "covers": ["R1.2"], "supports": [], "test": ["node", "--test", "test/api.test.mjs"] }
            ```

            #### Change
            Add `src/api.mjs` in `api`.

            #### Tests
            Add `test/api.test.mjs` in `api`.
            """);
        TestGitFixture.Git(masterDir, "add", ".");
        TestGitFixture.Git(masterDir, "commit", "-q", "-m", $"docs(demo): accept plan {Change}", "-m", "Generated-By: hoshin");

        return (masterDir, appDir, apiDir);
    }

    private static string ExtractContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }
}
