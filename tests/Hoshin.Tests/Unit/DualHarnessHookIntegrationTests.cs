using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// Phase 4 end-to-end scripted hook integration tests across BOTH Claude Code and Google Antigravity payloads:
/// foreground subagents, async background subagents, session-aware guard denials (premature prod edits & manual git commit),
/// retry handoffs with zero dirty commits, RunChecks, accept -> Publish, and skill/packaging hygiene checks.
/// </summary>
public sealed class DualHarnessHookIntegrationTests
{
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");

    private sealed class ToyFixture : IDisposable
    {
        public TempDir Temp { get; } = new("hoshin-hook-e2e-");
        public string RepoDir { get; }
        public string RemoteDir { get; }
        public WorkflowRepo Repo { get; }

        public ToyFixture()
        {
            RepoDir = Path.Combine(Temp.Path, "repo");
            RemoteDir = RepoDir + ".remote.git";
            TestGitFixture.InitRepo(RepoDir, new Dictionary<string, string>
            {
                ["src/greet.mjs"] = "export function greet(name) { return `Hello, ${name}`; }\n",
                ["test/base.test.mjs"] = """
                    import test from 'node:test';
                    import assert from 'node:assert/strict';
                    import { greet } from '../src/greet.mjs';
                    test('base greet', () => { assert.equal(greet('world'), 'Hello, world'); });
                    """,
                [".hoshin/change.jsonc"] = """
                    {
                      "version": 1,
                      "change": "greet-polish",
                      "branch": "hoshin/greet-polish",
                      "type": "feat",
                      "scope": "greet",
                      "base": "main"
                    }
                    """,
                [".hoshin/hooks.jsonc"] = """
                    {
                      "hooks": [
                        { "event": "build.finish", "blocking": true, "run": ["node", "--test"] }
                      ]
                    }
                    """,
                [".hoshin/plan.md"] = """
                    ```jsonc
                    {
                      "layout": 2,
                      "system": "greet",
                      "status": "accepted"
                    }
                    ```

                    # Plan: Polish greeting

                    ## Tasks

                    ### T1: Add exclamation option

                    ```jsonc
                    {
                      "id": "T1",
                      "repo": "repo",
                      "test": ["node", "--test", "test/t1.test.mjs"]
                    }
                    ```

                    #### Change

                    Add `shout(name)` returning `Hello, <name>!`.

                    #### Tests

                    Verify `shout('Ada') === 'Hello, Ada!'`.

                    ### T2: Trim blank names

                    ```jsonc
                    {
                      "id": "T2",
                      "repo": "repo",
                      "depends_on": ["T1"],
                      "test": ["node", "--test", "test/t2.test.mjs"]
                    }
                    ```

                    #### Change

                    Add `cleanGreet(name)` trimming whitespace.

                    #### Tests

                    Verify `cleanGreet('  Ada  ') === 'Hello, Ada'`.
                    """,
            });
            Repo = WorkflowRepo.Find(RepoDir)!;
        }

        public void WriteTaskWork(string task, bool sloppy = false)
        {
            if (task == "T1")
            {
                TestGitFixture.Write(Path.Combine(RepoDir, "test", "t1.test.mjs"), sloppy
                    ? "import test from 'node:test'; import assert from 'node:assert/strict'; test('sloppy', () => assert.equal(1, 1));\n"
                    : "import test from 'node:test'; import assert from 'node:assert/strict'; import { shout } from '../src/greet.mjs'; test('shout', () => assert.equal(shout('Ada'), 'Hello, Ada!'));\n");
                TestGitFixture.Write(Path.Combine(RepoDir, "src", "greet.mjs"),
                    "export function greet(name) { return `Hello, ${name}`; }\nexport function shout(name) { return `Hello, ${name}!`; }\n");
            }
            else if (task == "T2")
            {
                TestGitFixture.Write(Path.Combine(RepoDir, "test", "t2.test.mjs"),
                    "import test from 'node:test'; import assert from 'node:assert/strict'; import { cleanGreet } from '../src/greet.mjs'; test('clean', () => assert.equal(cleanGreet('  Ada  '), 'Hello, Ada'));\n");
                TestGitFixture.Write(Path.Combine(RepoDir, "src", "greet.mjs"),
                    "export function greet(name) { return `Hello, ${name}`; }\nexport function shout(name) { return `Hello, ${name}!`; }\nexport function cleanGreet(name) { return `Hello, ${name.trim()}`; }\n");
            }
        }

        public void Dispose() => Temp.Dispose();
    }

    private static (int ExitCode, string Stdout, string Stderr) RunHook(string cwd, string verb, object payload)
    {
        var args = new List<string> { HoshinDll };
        args.AddRange(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (verb.StartsWith("hook ", StringComparison.Ordinal))
        {
            args.Add("--roles");
            args.Add(SkillsDir);
        }

        var env = new Dictionary<string, string>(TestGitFixture.GitEnv) { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };
        var res = ProcessCommandRunner.Default.Run(new CommandRequest(
            WorkingDirectory: cwd,
            Argv: ["dotnet", .. args],
            StandardInput: JsonSerializer.Serialize(payload),
            ExtraEnvironment: env));

        Assert.Equal(0, res.ExitCode); // Every hook MUST exit 0.
        return (res.ExitCode ?? -1, res.Stdout, res.Stderr);
    }

    private static string ClaudeContext(string stdout) =>
        JsonDocument.Parse(stdout).RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;

    private static string AgyInjectedMessage(string stdout) =>
        JsonDocument.Parse(stdout).RootElement.GetProperty("injectSteps")[0].GetProperty("systemMessage").GetProperty("systemMessage").GetString()!;

    // =========================================================================
    // 1. Claude Code Foreground End-to-End (Guard, Retry, Checks, Accept -> Publish)
    // =========================================================================

    [Fact]
    public void ClaudeCode_ForegroundBuildRetryGuardChecksAndAcceptPublish_EndToEnd()
    {
        using var toy = new ToyFixture();
        var startHead = toy.Repo.Head();

        // 1. /hoshin:build starts the run and injects T1 brief.
        var (_, startOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            command_name = "hoshin:build",
            prompt = "/hoshin:build",
        });
        var startCtx = ClaudeContext(startOut);
        Assert.Contains("build task T1, attempt 1", startCtx);
        Assert.Contains("# Implement one task, test first", startCtx);

        // 2. Guard denies premature prod edit before test file exists.
        var (_, denyProd, _) = RunHook(toy.RepoDir, "guard", new
        {
            hook_event_name = "PreToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Edit",
            tool_input = new { file_path = Path.Combine(toy.RepoDir, "src", "greet.mjs") },
        });
        Assert.Equal("deny", JsonDocument.Parse(denyProd).RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());

        // 3. Guard denies manual `git commit` during active build.
        var (_, denyCommit, _) = RunHook(toy.RepoDir, "guard", new
        {
            hook_event_name = "PreToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Bash",
            tool_input = new { command = "git commit -m 'test(greet): manual'" },
        });
        Assert.Equal("deny", JsonDocument.Parse(denyCommit).RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());

        // 4. Guard allows writing the test file.
        var (_, allowTest, _) = RunHook(toy.RepoDir, "guard", new
        {
            hook_event_name = "PreToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Write",
            tool_input = new { file_path = Path.Combine(toy.RepoDir, "test", "t1.test.mjs") },
        });
        Assert.Equal("", allowTest);

        // 5. Sloppy attempt 1 (test passes without prod): commits NOTHING and hands back Retry.
        toy.WriteTaskWork("T1", sloppy: true);
        var (_, retryOut, _) = RunHook(toy.RepoDir, "hook result", new
        {
            hook_event_name = "PostToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Agent",
        });
        var retryCtx = ClaudeContext(retryOut);
        Assert.Contains("T1 attempt 1 is not accepted", retryCtx);
        Assert.Contains("build task T1, attempt 2", retryCtx);
        Assert.Equal(startHead, toy.Repo.Head()); // Zero commits on failed attempt!

        // 6. Attempt 2 fixes T1 test -> accepted, split into test + prod commits, advances to T2.
        toy.WriteTaskWork("T1", sloppy: false);
        var (_, t1OkOut, _) = RunHook(toy.RepoDir, "hook result", new
        {
            hook_event_name = "PostToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Agent",
        });
        var t1OkCtx = ClaudeContext(t1OkOut);
        Assert.Contains("T1 attempt 2 is accepted", t1OkCtx);
        Assert.Contains("build task T2, attempt 1", t1OkCtx);

        // 7. Complete T2 -> automatically runs build.finish checks and transitions to NeedsApproval.
        toy.WriteTaskWork("T2");
        var (_, t2OkOut, _) = RunHook(toy.RepoDir, "hook result", new
        {
            hook_event_name = "PostToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Agent",
        });
        var t2OkCtx = ClaudeContext(t2OkOut);
        Assert.Contains("T2 attempt 1 is accepted", t2OkCtx);
        Assert.Contains("ready for approval", t2OkCtx);
        Assert.Contains("/hoshin:accept", t2OkCtx);

        // 8. Stop at NeedsApproval lets session end cleanly.
        var (_, stopAtApproval, _) = RunHook(toy.RepoDir, "hook stop", new
        {
            hook_event_name = "Stop",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
        });
        Assert.Equal("", stopAtApproval);
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(WorkflowState.Read(toy.Repo)!));

        // 9. Second session invokes /hoshin:accept -> records approval & publishes to origin!
        var (_, acceptOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = toy.RepoDir,
            session_id = "claude-s2",
            command_name = "hoshin:accept",
            prompt = "/hoshin:accept",
        });
        Assert.Contains("The change is done: published", ClaudeContext(acceptOut));
        var remoteSha = TestGitFixture.Git(toy.Temp.Path, "--git-dir", toy.RemoteDir, "rev-parse", "refs/heads/hoshin/greet-polish");
        Assert.Equal(toy.Repo.Head(), remoteSha);
    }

    // =========================================================================
    // 2. Claude Code Async Background Subagent Flow
    // =========================================================================

    [Fact]
    public void ClaudeCode_AsyncBackgroundSubagent_RefusesDuplicateWaitsOnStopAndJudgesOnSubagentStop()
    {
        using var toy = new ToyFixture();
        RunHook(toy.RepoDir, "hook start", new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = toy.RepoDir,
            session_id = "claude-async",
            command_name = "hoshin:build",
        });

        // Async launch records agentId in ledger and tells orchestrator to wait.
        var (_, launchedOut, _) = RunHook(toy.RepoDir, "hook result", new
        {
            hook_event_name = "PostToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-async",
            tool_name = "Agent",
            tool_response = new { isAsync = true, status = "async_launched", agentId = "agent-1" },
        });
        Assert.Contains("is running in the background (agent-1)", ClaudeContext(launchedOut));
        Assert.Equal("agent-1", toy.Repo.ReadLedger().ActiveSubagent?.AgentId);

        // Conflicting second launch is refused.
        var (_, dupOut, _) = RunHook(toy.RepoDir, "hook result", new
        {
            hook_event_name = "PostToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-async",
            tool_name = "Agent",
            tool_response = new { isAsync = true, status = "async_launched", agentId = "agent-2" },
        });
        Assert.Contains("already building T1", ClaudeContext(dupOut));

        // Stop while background task is still running allows parent to yield.
        var (_, stopRunning, _) = RunHook(toy.RepoDir, "hook stop", new
        {
            hook_event_name = "Stop",
            cwd = toy.RepoDir,
            session_id = "claude-async",
            background_tasks = new[] { new { id = "agent-1", status = "running" } },
        });
        Assert.Equal("", stopRunning);

        // Background subagent completes work -> SubagentStop judges and commits T1.
        toy.WriteTaskWork("T1");
        RunHook(toy.RepoDir, "hook subagent-stop", new
        {
            hook_event_name = "SubagentStop",
            cwd = toy.RepoDir,
            session_id = "claude-async",
            agent_id = "agent-1",
        });
        Assert.True(toy.Repo.ReadLedger().Verdicts.Single().Passed);
        Assert.Null(toy.Repo.ReadLedger().ActiveSubagent);

        // Next parent Stop blocks with T2 brief.
        var (_, nextStop, _) = RunHook(toy.RepoDir, "hook stop", new
        {
            hook_event_name = "Stop",
            cwd = toy.RepoDir,
            session_id = "claude-async",
            background_tasks = Array.Empty<object>(),
        });
        var stopDoc = JsonDocument.Parse(nextStop).RootElement;
        Assert.Equal("block", stopDoc.GetProperty("decision").GetString());
        Assert.Contains("build task T2", stopDoc.GetProperty("reason").GetString());
    }

    // =========================================================================
    // 3. Antigravity Full End-to-End (PreInvocation, Async invoke_subagent, Guard, fullyIdle, Accept -> Publish)
    // =========================================================================

    [Fact]
    public void Antigravity_AsyncBuildGuardSessionIsolationAndAcceptPublish_EndToEnd()
    {
        using var toy = new ToyFixture();

        // 1. PreInvocation with /build injects T1 brief targeting invoke_subagent.
        var (_, startOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-parent-1",
            cwd = toy.RepoDir,
            workspacePaths = new[] { toy.RepoDir },
            invocationNum = 0,
            lastUserInput = "/build",
        });
        var startMsg = AgyInjectedMessage(startOut);
        Assert.Contains("build task T1, attempt 1", startMsg);
        Assert.Contains("invoke_subagent", startMsg);

        // 2. Duplicate PreInvocation in same turn returns {}.
        var (_, dupStart, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-parent-1",
            cwd = toy.RepoDir,
            workspacePaths = new[] { toy.RepoDir },
            invocationNum = 1,
            lastUserInput = "/build",
        });
        Assert.Equal("{}", dupStart);

        // 3. Session isolation: an unrelated Antigravity conversation (no active subagent launched yet) is NOT locked out!
        var (_, unrelatedAllow, _) = RunHook(toy.RepoDir, "guard", new
        {
            hookEventName = "PreToolUse",
            conversationId = "agy-unrelated-session",
            cwd = toy.RepoDir,
            toolCall = new { id = "u1", name = "edit_file", args = new { Path = Path.Combine(toy.RepoDir, "src", "greet.mjs") } },
        });
        Assert.Equal("allow", JsonDocument.Parse(unrelatedAllow).RootElement.GetProperty("decision").GetString());

        // 4. Execute T1 and T2 through async invoke_subagent + guard + Stop(fullyIdle).
        foreach (var task in new[] { "T1", "T2" })
        {
            var (_, launched, _) = RunHook(toy.RepoDir, "hook result", new
            {
                hookEventName = "PostToolUse",
                conversationId = "agy-parent-1",
                cwd = toy.RepoDir,
                toolCall = new { id = $"call-{task}", name = "invoke_subagent", args = new { TypeName = "self", Role = $"Build {task}", Prompt = "..." } },
            });
            Assert.Equal("{}", launched);

            // Parent Stop while subagent runs (fullyIdle=false) yields cleanly.
            var (_, yieldOut, _) = RunHook(toy.RepoDir, "hook stop", new
            {
                hookEventName = "Stop",
                conversationId = "agy-parent-1",
                cwd = toy.RepoDir,
                fullyIdle = false,
            });
            Assert.Equal("{}", yieldOut);

            // Subagent PreToolUse guard denies premature prod edit and manual git commit.
            var (_, subDenyProd, _) = RunHook(toy.RepoDir, "guard", new
            {
                hookEventName = "PreToolUse",
                conversationId = $"agy-sub-{task}",
                cwd = toy.RepoDir,
                toolCall = new { id = $"e-{task}", name = "replace_file_content", args = new { TargetFile = Path.Combine(toy.RepoDir, "src", "greet.mjs") } },
            });
            Assert.Equal("deny", JsonDocument.Parse(subDenyProd).RootElement.GetProperty("decision").GetString());

            var (_, subDenyCommit, _) = RunHook(toy.RepoDir, "guard", new
            {
                hookEventName = "PreToolUse",
                conversationId = $"agy-sub-{task}",
                cwd = toy.RepoDir,
                toolCall = new { id = $"c-{task}", name = "run_command", args = new { CommandLine = "git commit -m 'manual'" } },
            });
            Assert.Equal("deny", JsonDocument.Parse(subDenyCommit).RootElement.GetProperty("decision").GetString());

            // Subagent writes test file first (allowed), then prod file.
            var (_, subAllowTest, _) = RunHook(toy.RepoDir, "guard", new
            {
                hookEventName = "PreToolUse",
                conversationId = $"agy-sub-{task}",
                cwd = toy.RepoDir,
                toolCall = new { id = $"w-{task}", name = "write_file", args = new { Path = Path.Combine(toy.RepoDir, "test", $"{task.ToLowerInvariant()}.test.mjs") } },
            });
            Assert.Equal("allow", JsonDocument.Parse(subAllowTest).RootElement.GetProperty("decision").GetString());

            toy.WriteTaskWork(task);

            // Subagent Stop (different conversationId) is ignored ({}).
            var (_, subStop, _) = RunHook(toy.RepoDir, "hook stop", new
            {
                hookEventName = "Stop",
                conversationId = $"agy-sub-{task}",
                cwd = toy.RepoDir,
                fullyIdle = true,
            });
            Assert.Equal("{}", subStop);

            // Parent Stop (fullyIdle=true) evaluates, commits test+prod, advances checks, returns decision=continue.
            var (_, parentStop, _) = RunHook(toy.RepoDir, "hook stop", new
            {
                hookEventName = "Stop",
                conversationId = "agy-parent-1",
                cwd = toy.RepoDir,
                fullyIdle = true,
            });
            var stopDoc = JsonDocument.Parse(parentStop).RootElement;
            Assert.Equal("continue", stopDoc.GetProperty("decision").GetString());
            var reason = stopDoc.GetProperty("reason").GetString()!;
            Assert.Contains($"{task} attempt 1 is accepted", reason);
            if (task == "T2")
            {
                Assert.Contains("ready for approval", reason);
                Assert.Contains("/accept", reason);
            }
        }

        // 5. Final Stop at NeedsApproval returns {}.
        var (_, doneStop, _) = RunHook(toy.RepoDir, "hook stop", new
        {
            hookEventName = "Stop",
            conversationId = "agy-parent-1",
            cwd = toy.RepoDir,
            fullyIdle = true,
        });
        Assert.Equal("{}", doneStop);

        // 6. Session 2: /accept approves and publishes branch to origin.
        var (_, acceptOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-parent-2",
            cwd = toy.RepoDir,
            workspacePaths = new[] { toy.RepoDir },
            invocationNum = 0,
            lastUserInput = "/accept",
        });
        Assert.Contains("The change is done: published", AgyInjectedMessage(acceptOut));
        var remoteSha = TestGitFixture.Git(toy.Temp.Path, "--git-dir", toy.RemoteDir, "rev-parse", "refs/heads/hoshin/greet-polish");
        Assert.Equal(toy.Repo.Head(), remoteSha);
    }

    [Fact]
    public void Antigravity_ModelInvokedSkillRead_QueuesAndDrainsOnNextPreInvocation()
    {
        using var toy = new ToyFixture();
        var (_, postOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PostToolUse",
            conversationId = "agy-skill-1",
            cwd = toy.RepoDir,
            toolCall = new
            {
                id = "r1",
                name = "read_file",
                args = new { Path = Path.Combine(toy.RepoDir, ".agents", "skills", "build", "SKILL.md") },
            },
        });
        Assert.Equal("{}", postOut);

        var (_, preOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-skill-1",
            cwd = toy.RepoDir,
            invocationNum = 1,
            lastUserInput = "Please build the plan",
        });
        Assert.Contains("build task T1, attempt 1", AgyInjectedMessage(preOut));
    }

    // =========================================================================
    // 4. Harness-Neutral Skills Hygiene Check
    // =========================================================================

    [Fact]
    public void SkillProcedures_ContainNoYamlCliCommandsOrHarnessToolNames()
    {
        var files = Directory.GetFiles(SkillsDir, "*.md", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var forbidden = new Regex(@"\b(hoshin\.dll|hoshin\.ts|hoshin\s+(check|read|commit|run|state|guard|hook)|invoke_subagent|run_command|write_to_file|replace_file_content|Bash|PreToolUse|PostToolUse|SubagentStop|UserPromptExpansion)\b|`Agent`");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.False(text.TrimStart().StartsWith("---", StringComparison.Ordinal), $"{file} must not contain YAML front matter");
            Assert.False(forbidden.IsMatch(text), $"{file} matched forbidden CLI/harness token: {forbidden.Match(text).Value}");
        }
    }
}
