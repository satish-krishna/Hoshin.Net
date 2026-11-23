using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class UnifiedLedgerTests
{
    private static readonly string HoshinDll =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "hoshin.dll"));

    private const string Change = "2026-11-01-greeting";

    [Fact]
    public void HookDrivenBuild_ReportsTaskVerdictInRedShowAndStatusJson()
    {
        using var tmp = new TempDir("hoshin-a3-unified-");
        var repoDir = CreateSingleRepoWorkspace(tmp.Path);

        var repo = TestServices.FindRepo(repoDir, Path.Combine("intent", "demo", Change))!;
        var orchestrator = TestServices.HookOrchestrator;
        var session = "claude-a3";

        // 1. Start build via hook.
        var startEvent = TestServices.HookAdapter.Parse(
            JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptSubmit",
                session_id = session,
                cwd = repoDir,
                command_name = "hoshin:build",
            }));
        var (startOut, _) = orchestrator.HandleStart(startEvent, rolesRoot: null);
        Assert.NotNull(startOut);

        var step = repo.ReadStep();
        Assert.NotNull(step);
        Assert.Equal(StepKind.Implement, step!.Kind);
        Assert.Equal("T1", step.Task);
        Assert.False(string.IsNullOrEmpty(step.DispatchNonce));

        // 2. Subagent adds failing test + passing implementation.
        TestGitFixture.Write(
            Path.Combine(repoDir, "test", "greet.test.mjs"),
            """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { greet } from '../src/greet.mjs';
            test('greets by name', () => { assert.equal(greet('world'), 'hello world'); });
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "src", "greet.mjs"),
            "export const greet = (name) => `hello ${name}`;\n");

        // 3. Foreground subagent completes -> hook evaluates Red/Green and commits T1.
        var resultEvent = TestServices.HookAdapter.Parse(
            JsonSerializer.Serialize(new
            {
                hook_event_name = "PostToolUse",
                session_id = session,
                cwd = repoDir,
                tool_name = "Agent",
                tool_input = new
                {
                    prompt = $"Implement T1\n{step.DispatchNonce}",
                },
                tool_response = new { content = "done" },
            }));
        orchestrator.HandleResult(resultEvent, rolesRoot: null);

        // 4. Unified ledger file lives at .git/hoshin/demo/<change>/app.json.
        var unifiedPath = Path.Combine(repo.HoshinDir, "demo", Change, "app.json");
        Assert.True(File.Exists(unifiedPath), $"Expected unified RepoLedger at {unifiedPath}");

        // 5. System A `hoshin red show --json` reports T1's observed Red verdict.
        var redShow = RunHoshinCli(repoDir, "red", "show", "--json", "--change", Change, "--repo", "app", "--task", "T1");
        Assert.True(redShow.ExitCode == 0, $"red show failed ({redShow.ExitCode}): {redShow.Stderr}\n{redShow.Stdout}");
        using var redDoc = JsonDocument.Parse(redShow.Stdout);
        Assert.True(redDoc.RootElement.GetProperty("observed").GetBoolean(), $"red show output: {redShow.Stdout}");
        var redRecord = redDoc.RootElement.GetProperty("record");
        Assert.Equal("T1", redRecord.GetProperty("task").GetString());
        Assert.Equal("failed", redRecord.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrEmpty(redRecord.GetProperty("commit").GetString()));

        // 6. System A `hoshin status --json` reports T1's task verdict run.
        var status = RunHoshinCli(repoDir, "status", "--json", "--change", Change);
        Assert.True(status.ExitCode == 0, $"status failed ({status.ExitCode}): {status.Stderr}\n{status.Stdout}");
        using var statusDoc = JsonDocument.Parse(status.Stdout);
        var repos = statusDoc.RootElement.GetProperty("repos").EnumerateArray().ToList();
        var appRepo = Assert.Single(repos, r => r.GetProperty("repo").GetString() == "app");
        var runs = appRepo.GetProperty("runs").EnumerateArray().ToList();
        Assert.Contains(runs, r =>
            r.GetProperty("event").GetString() == "task.done"
            && r.GetProperty("subject").GetString() == "T1"
            && r.GetProperty("outcome").GetString() == "passed");
    }

    [Fact]
    public void LegacyPerChangeLedger_MigratesOnceIntoUnifiedRepoLedgerOnFirstRead()
    {
        using var tmp = new TempDir("hoshin-a3-mig-");
        var repoDir = CreateSingleRepoWorkspace(tmp.Path);
        var repo = TestServices.FindRepo(repoDir, Path.Combine("intent", "demo", Change))!;
        var head = repo.Head();

        // Seed a T07 per-change workflow-ledger.json.
        var legacyPerChange = Path.Combine(repo.HoshinDir, "changes", Change, "workflow-ledger.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPerChange)!);
        File.WriteAllText(
            legacyPerChange,
            JsonSerializer.Serialize(new
            {
                issued = Array.Empty<object>(),
                verdicts = new[]
                {
                    new
                    {
                        task = "T1",
                        attempt = 1,
                        head,
                        passed = true,
                        red = "observed",
                        reasons = Array.Empty<string>(),
                        at = DateTimeOffset.UtcNow,
                    },
                },
                baselines = Array.Empty<object>(),
                checks = Array.Empty<object>(),
                reviews = Array.Empty<object>(),
                approvals = Array.Empty<object>(),
                published = Array.Empty<object>(),
            }));

        var ledger = repo.ReadLedger();
        Assert.Single(ledger.Verdicts ?? []);
        Assert.Equal("T1", (ledger.Verdicts ?? [])[0].Task);
        Assert.False(File.Exists(legacyPerChange), "T07 legacy per-change ledger should be migrated once.");
        Assert.True(File.Exists(Path.Combine(repo.HoshinDir, "demo", Change, "app.json")));
    }

    private static string CreateSingleRepoWorkspace(string root)
    {
        var repoDir = Path.Combine(root, "repo");
        var folder = $"intent/demo/{Change}";
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": {
                    "demo": {
                      "apps": { "app": "." }
                    }
                  }
                }
                """,
            ["src/index.mjs"] = "export const ok = true;\n",
        });

        TestGitFixture.Git(repoDir, "checkout", "-q", "-b", $"change/{Change}");
        TestGitFixture.Write(
            Path.Combine(repoDir, folder, "change.jsonc"),
            $$"""
            { "version": 1, "change": "{{Change}}", "type": "feat", "scope": "demo", "base": "main", "branch": "change/{{Change}}" }
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, folder, "intent.md"),
            """
            ```jsonc
            { "title": "Greeting", "system": "demo", "author": "t", "date": "2026-11-01", "status": "accepted", "scope": "demo" }
            ```

            # Intent: Greeting
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept intent {Change}");
        var intentSha = TestGitFixture.Git(repoDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(repoDir, folder, "spec.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Greeting

            ```gherkin
            Feature: Greeting
              @req:R1 @from:intent
              Scenario: Greets by name
                Given a name "world"
                When greeted
                Then the result is "hello world"
            ```
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept spec {Change}");
        var specSha = TestGitFixture.Git(repoDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(repoDir, folder, "plan.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Greeting

            ## Tasks

            ### T1: Add greeting helper

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1"], "supports": [], "test": ["node", "--test", "test/greet.test.mjs"] }
            ```

            #### Change
            Add `src/greet.mjs`.

            #### Tests
            Add `test/greet.test.mjs`.
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept plan {Change}");

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
