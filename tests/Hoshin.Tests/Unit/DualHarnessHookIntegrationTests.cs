using System.Text.Json;
using System.Text.Json.Nodes;
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
            Repo = TestServices.FindRepo(RepoDir)!;
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

    private static (int ExitCode, string Stdout, string Stderr) RunHook(string cwd, string verb, object payload) =>
        RunHookRaw(cwd, verb, JsonSerializer.Serialize(payload));

    private static (int ExitCode, string Stdout, string Stderr) RunFixture(
        string cwd,
        string verb,
        string harness,
        string fixture,
        Action<JsonObject> patch) =>
        RunHookRaw(cwd, verb, HookFixtureLoader.LoadAndPatch(harness, fixture, patch));

    private static (int ExitCode, string Stdout, string Stderr) RunHookRaw(string cwd, string verb, string rawJson)
    {
        var args = new List<string> { HoshinDll };
        args.AddRange(verb.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (verb.StartsWith("hook ", StringComparison.Ordinal))
        {
            args.Add("--roles");
            args.Add(SkillsDir);
        }

        var env = new Dictionary<string, string>(TestGitFixture.GitEnv) { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };
        var res = TestServices.CommandRunner.Run(new CommandRequest(
            WorkingDirectory: cwd,
            Argv: ["dotnet", .. args],
            StandardInput: rawJson,
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
        var (_, startOut, _) = RunFixture(toy.RepoDir, "hook start", "claude-code", "UserPromptExpansion", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["command_name"] = "hoshin:build";
            o["prompt"] = "/hoshin:build";
        });
        var startCtx = ClaudeContext(startOut);
        Assert.Contains("build task T1, attempt 1", startCtx);
        Assert.Contains("# Implement one task, test first", startCtx);

        // 2. Guard denies premature prod edit before test file exists.
        var (_, denyProd, _) = RunFixture(toy.RepoDir, "guard", "claude-code", "PreToolUse-Edit", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["tool_input"]!["file_path"] = Path.Combine(toy.RepoDir, "src", "greet.mjs");
        });
        Assert.Equal("deny", JsonDocument.Parse(denyProd).RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());

        // 3. Guard denies manual `git commit` during active build.
        var (_, denyCommit, _) = RunFixture(toy.RepoDir, "guard", "claude-code", "PreToolUse-Bash", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["tool_input"]!["command"] = "git commit -m 'test(greet): manual'";
        });
        Assert.Equal("deny", JsonDocument.Parse(denyCommit).RootElement.GetProperty("hookSpecificOutput").GetProperty("permissionDecision").GetString());

        // 4. Guard allows writing the test file.
        var (_, allowTest, _) = RunFixture(toy.RepoDir, "guard", "claude-code", "PreToolUse-Write", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["tool_input"]!["file_path"] = Path.Combine(toy.RepoDir, "test", "t1.test.mjs");
        });
        Assert.Equal("", allowTest);

        // 5. Sloppy attempt 1 (test passes without prod): commits NOTHING and hands back Retry.
        toy.WriteTaskWork("T1", sloppy: true);
        var (_, retryOut, _) = RunFixture(toy.RepoDir, "hook result", "claude-code", "PostToolUse-Agent", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["tool_input"]!["prompt"] = toy.Repo.ReadStep()!.DispatchNonce;
        });
        var retryCtx = ClaudeContext(retryOut);
        Assert.Contains("T1 attempt 1 is not accepted", retryCtx);
        Assert.Contains("build task T1, attempt 2", retryCtx);
        Assert.Equal(startHead, toy.Repo.Head()); // Zero commits on failed attempt!

        // 6. Attempt 2 fixes T1 test -> accepted, split into test + prod commits, advances to T2.
        toy.WriteTaskWork("T1", sloppy: false);
        var (_, t1OkOut, _) = RunFixture(toy.RepoDir, "hook result", "claude-code", "PostToolUse-Agent", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["tool_input"]!["prompt"] = toy.Repo.ReadStep()!.DispatchNonce;
        });
        var t1OkCtx = ClaudeContext(t1OkOut);
        Assert.Contains("T1 attempt 2 is accepted", t1OkCtx);
        Assert.Contains("build task T2, attempt 1", t1OkCtx);

        // 7. Complete T2 -> automatically runs build.finish checks and transitions to ReviewDocument.
        toy.WriteTaskWork("T2");
        var (_, t2OkOut, _) = RunFixture(toy.RepoDir, "hook result", "claude-code", "PostToolUse-Agent", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["tool_input"]!["prompt"] = toy.Repo.ReadStep()!.DispatchNonce;
        });
        var t2OkCtx = ClaudeContext(t2OkOut);
        Assert.Contains("T2 attempt 1 is accepted", t2OkCtx);
        Assert.Contains("build/review.md", t2OkCtx);

        // 7b. Write clean review -> Stop commits review and advances to NeedsApproval.
        WriteCleanReview(toy);
        var (_, reviewStopOut, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
        });
        Assert.Contains("review is committed", JsonDocument.Parse(reviewStopOut).RootElement.GetProperty("reason").GetString()!);

        // 8. Stop at NeedsApproval lets session end cleanly.
        var (_, stopAtApproval, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
        });
        Assert.Equal("", stopAtApproval);
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(toy.Repo)!));

        // 9. Second session (claude-s2) invokes /hoshin:accept -> told claude-s1 owns the run, no state change!
        var (_, otherAcceptOut, _) = RunFixture(toy.RepoDir, "hook start", "claude-code", "UserPromptExpansion", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s2";
            o["command_name"] = "hoshin:accept";
            o["prompt"] = "/hoshin:accept";
        });
        Assert.Contains("claude-s1", ClaudeContext(otherAcceptOut));
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(toy.Repo)!));

        // 10. Original owning session (claude-s1) invokes /hoshin:accept -> records approval & publishes to origin!
        var (_, acceptOut, _) = RunFixture(toy.RepoDir, "hook start", "claude-code", "UserPromptExpansion", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-s1";
            o["command_name"] = "hoshin:accept";
            o["prompt"] = "/hoshin:accept";
        });
        Assert.Contains("The change is done: published", ClaudeContext(acceptOut));
        var remoteSha = TestGitFixture.Git(toy.Temp.Path, "--git-dir", toy.RemoteDir, "rev-parse", "refs/heads/change/greet-polish");
        Assert.Equal(toy.Repo.Head(), remoteSha);
    }

    // =========================================================================
    // 2. Claude Code Async Background Subagent Flow
    // =========================================================================

    [Fact]
    public void ClaudeCode_AsyncBackgroundSubagent_RefusesDuplicateWaitsOnStopAndJudgesOnSubagentStop()
    {
        using var toy = new ToyFixture();
        RunFixture(toy.RepoDir, "hook start", "claude-code", "UserPromptExpansion", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["command_name"] = "hoshin:build";
        });

        // Async launch records agentId in ledger and tells orchestrator to wait.
        var t1Nonce = toy.Repo.ReadStep()!.DispatchNonce;
        var (_, launchedOut, _) = RunFixture(toy.RepoDir, "hook result", "claude-code", "PostToolUse-Agent-async", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["tool_input"]!["prompt"] = t1Nonce;
            o["tool_response"]!["agentId"] = "agent-1";
        });
        Assert.Contains("is running in the background (agent-1)", ClaudeContext(launchedOut));
        Assert.Equal("agent-1", toy.Repo.ReadLedger().ActiveSubagent?.AgentId);

        // Conflicting second launch is refused.
        var (_, dupOut, _) = RunFixture(toy.RepoDir, "hook result", "claude-code", "PostToolUse-Agent-async", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["tool_input"]!["prompt"] = t1Nonce;
            o["tool_response"]!["agentId"] = "agent-2";
        });
        Assert.Contains("already building T1", ClaudeContext(dupOut));

        // Stop while background task is still running allows parent to yield.
        var (_, stopRunning, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["background_tasks"] = new JsonArray(new JsonObject { ["id"] = "agent-1", ["status"] = "running" });
        });
        Assert.Equal("", stopRunning);

        // Background subagent completes work -> SubagentStop judges and commits T1.
        toy.WriteTaskWork("T1");
        RunFixture(toy.RepoDir, "hook subagent-stop", "claude-code", "SubagentStop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["agent_id"] = "agent-1";
        });
        Assert.True(toy.Repo.ReadLedger().WorkflowVerdicts.Single().Passed);
        Assert.Null(toy.Repo.ReadLedger().ActiveSubagent);

        // Next parent Stop blocks with T1 verdict report AND T2 brief.
        var (_, nextStop, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
        });
        var stopDoc = JsonDocument.Parse(nextStop).RootElement;
        Assert.Equal("block", stopDoc.GetProperty("decision").GetString());
        var t1Reason = stopDoc.GetProperty("reason").GetString()!;
        Assert.Contains("T1 attempt 1 is accepted", t1Reason);
        Assert.Contains("build task T2", t1Reason);

        // Run T2 async through SubagentStop -> next parent Stop blocks once with T2 verdict + "ready for approval".
        var t2Nonce = toy.Repo.ReadStep()!.DispatchNonce;
        RunFixture(toy.RepoDir, "hook result", "claude-code", "PostToolUse-Agent-async", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["tool_input"]!["prompt"] = t2Nonce;
            o["tool_response"]!["agentId"] = "agent-t2";
        });
        toy.WriteTaskWork("T2");
        RunFixture(toy.RepoDir, "hook subagent-stop", "claude-code", "SubagentStop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
            o["agent_id"] = "agent-t2";
        });

        var (_, reviewStop, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
        });
        Assert.False(string.IsNullOrWhiteSpace(reviewStop), "Expected parent Stop to report T2 verdict and Review once");
        var reviewDoc = JsonDocument.Parse(reviewStop).RootElement;
        Assert.Equal("block", reviewDoc.GetProperty("decision").GetString());
        var reviewReason = reviewDoc.GetProperty("reason").GetString()!;
        Assert.Contains("T2 attempt 1 is accepted", reviewReason);
        Assert.Contains("build/review.md", reviewReason);

        // Commit clean review -> advances to NeedsApproval, and following Stop allows cleanly.
        WriteCleanReview(toy);
        var (_, committedReviewStop, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
        });
        Assert.Contains("review is committed", JsonDocument.Parse(committedReviewStop).RootElement.GetProperty("reason").GetString()!);

        var (_, secondApprovalStop, _) = RunFixture(toy.RepoDir, "hook stop", "claude-code", "Stop", o =>
        {
            o["cwd"] = toy.RepoDir;
            o["session_id"] = "claude-async";
        });
        Assert.Equal("", secondApprovalStop);
    }

    // =========================================================================
    // 3. Antigravity Full End-to-End (PreInvocation, Async invoke_subagent, Guard, fullyIdle, Accept -> Publish)
    // =========================================================================

    [Fact]
    public void Antigravity_AsyncBuildGuardSessionIsolationAndAcceptPublish_EndToEnd()
    {
        using var toy = new ToyFixture();

        // 1. PreInvocation with /build injects T1 brief targeting invoke_subagent.
        var (_, startOut, _) = RunFixture(toy.RepoDir, "hook start", "antigravity", "PreInvocation", o =>
        {
            o["conversationId"] = "agy-parent-1";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["invocationNum"] = 0;
            o["lastUserInput"] = "/build";
        });
        var startMsg = AgyInjectedMessage(startOut);
        Assert.Contains("build task T1, attempt 1", startMsg);
        Assert.Contains("invoke_subagent", startMsg);

        // 2. Duplicate PreInvocation in same turn returns {}.
        var (_, dupStart, _) = RunFixture(toy.RepoDir, "hook start", "antigravity", "PreInvocation", o =>
        {
            o["conversationId"] = "agy-parent-1";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["invocationNum"] = 1;
            o["lastUserInput"] = "/build";
        });
        Assert.Equal("{}", dupStart);

        // 3. Session isolation: an unrelated Antigravity conversation (no active subagent launched yet) is NOT locked out!
        var (_, unrelatedAllow, _) = RunFixture(toy.RepoDir, "guard", "antigravity", "PreToolUse-edit_file", o =>
        {
            o["conversationId"] = "agy-unrelated-session";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["toolCall"]!["args"]!["Path"] = Path.Combine(toy.RepoDir, "src", "greet.mjs");
        });
        Assert.Equal("allow", JsonDocument.Parse(unrelatedAllow).RootElement.GetProperty("decision").GetString());

        // 4. Execute T1 and T2 through async invoke_subagent + guard + Stop(fullyIdle).
        foreach (var task in new[] { "T1", "T2" })
        {
            var (_, launched, _) = RunFixture(toy.RepoDir, "hook result", "antigravity", "PostToolUse-invoke_subagent", o =>
            {
                o["conversationId"] = "agy-parent-1";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["toolCall"]!["id"] = $"call-{task}";
                o["toolCall"]!["args"]!["Role"] = $"Build {task}";
                o["toolCall"]!["args"]!["Prompt"] = toy.Repo.ReadStep()!.DispatchNonce;
                o["toolResponse"]!["conversationId"] = $"agy-sub-{task}";
            });
            Assert.Equal("{}", launched);

            // Parent Stop while subagent runs (fullyIdle=false) yields cleanly.
            var (_, yieldOut, _) = RunFixture(toy.RepoDir, "hook stop", "antigravity", "Stop", o =>
            {
                o["conversationId"] = "agy-parent-1";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["fullyIdle"] = false;
            });
            Assert.Equal("{}", yieldOut);

            // Subagent PreToolUse guard denies premature prod edit and manual git commit.
            var (_, subDenyProd, _) = RunFixture(toy.RepoDir, "guard", "antigravity", "PreToolUse-replace_file_content", o =>
            {
                o["conversationId"] = $"agy-sub-{task}";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["toolCall"]!["id"] = $"e-{task}";
                o["toolCall"]!["args"]!["TargetFile"] = Path.Combine(toy.RepoDir, "src", "greet.mjs");
            });
            Assert.Equal("deny", JsonDocument.Parse(subDenyProd).RootElement.GetProperty("decision").GetString());

            var (_, subDenyCommit, _) = RunFixture(toy.RepoDir, "guard", "antigravity", "PreToolUse-run_command", o =>
            {
                o["conversationId"] = $"agy-sub-{task}";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["toolCall"]!["id"] = $"c-{task}";
                o["toolCall"]!["args"]!["CommandLine"] = "git commit -m 'manual'";
            });
            Assert.Equal("deny", JsonDocument.Parse(subDenyCommit).RootElement.GetProperty("decision").GetString());

            // Subagent writes test file first (allowed), then prod file.
            var (_, subAllowTest, _) = RunFixture(toy.RepoDir, "guard", "antigravity", "PreToolUse-write_file", o =>
            {
                o["conversationId"] = $"agy-sub-{task}";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["toolCall"]!["id"] = $"w-{task}";
                o["toolCall"]!["args"]!["Path"] = Path.Combine(toy.RepoDir, "test", $"{task.ToLowerInvariant()}.test.mjs");
            });
            Assert.Equal("allow", JsonDocument.Parse(subAllowTest).RootElement.GetProperty("decision").GetString());

            toy.WriteTaskWork(task);

            // Subagent Stop (different conversationId) is ignored ({}).
            var (_, subStop, _) = RunFixture(toy.RepoDir, "hook stop", "antigravity", "Stop", o =>
            {
                o["conversationId"] = $"agy-sub-{task}";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["fullyIdle"] = true;
            });
            Assert.Equal("{}", subStop);

            // Parent Stop (fullyIdle=true) evaluates, commits test+prod, advances checks, returns decision=continue.
            var (_, parentStop, _) = RunFixture(toy.RepoDir, "hook stop", "antigravity", "Stop", o =>
            {
                o["conversationId"] = "agy-parent-1";
                o["cwd"] = toy.RepoDir;
                o["workspacePaths"] = new JsonArray(toy.RepoDir);
                o["fullyIdle"] = true;
            });
            var stopDoc = JsonDocument.Parse(parentStop).RootElement;
            Assert.Equal("continue", stopDoc.GetProperty("decision").GetString());
            var reason = stopDoc.GetProperty("reason").GetString()!;
            Assert.Contains($"{task} attempt 1 is accepted", reason);
            if (task == "T2")
            {
                Assert.Contains("build/review.md", reason);
            }
        }

        // 4b. Write clean review -> parent Stop commits review and advances to NeedsApproval.
        WriteCleanReview(toy);
        var (_, reviewStopAgy, _) = RunFixture(toy.RepoDir, "hook stop", "antigravity", "Stop", o =>
        {
            o["conversationId"] = "agy-parent-1";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["fullyIdle"] = true;
        });
        Assert.Contains("review is committed", JsonDocument.Parse(reviewStopAgy).RootElement.GetProperty("reason").GetString()!);

        // 5. Final Stop at NeedsApproval returns {}.
        var (_, doneStop, _) = RunFixture(toy.RepoDir, "hook stop", "antigravity", "Stop", o =>
        {
            o["conversationId"] = "agy-parent-1";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["fullyIdle"] = true;
        });
        Assert.Equal("{}", doneStop);

        // 6. Unrelated session 2 is told session 1 owns the run with no state change; owning session 1 /accept approves and publishes to origin.
        var (_, otherAcceptOut, _) = RunFixture(toy.RepoDir, "hook start", "antigravity", "PreInvocation", o =>
        {
            o["conversationId"] = "agy-parent-2";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["invocationNum"] = 0;
            o["lastUserInput"] = "/accept";
        });
        Assert.Contains("agy-parent-1", AgyInjectedMessage(otherAcceptOut));
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(toy.Repo)!));

        var (_, acceptOut, _) = RunFixture(toy.RepoDir, "hook start", "antigravity", "PreInvocation", o =>
        {
            o["conversationId"] = "agy-parent-1";
            o["cwd"] = toy.RepoDir;
            o["workspacePaths"] = new JsonArray(toy.RepoDir);
            o["invocationNum"] = 0;
            o["lastUserInput"] = "/accept";
        });
        Assert.Contains("The change is done: published", AgyInjectedMessage(acceptOut));
        var remoteSha = TestGitFixture.Git(toy.Temp.Path, "--git-dir", toy.RemoteDir, "rev-parse", "refs/heads/change/greet-polish");
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
    // 4. Security: Approval and Acceptance Must Be Human-Only (T02 / S1)
    // =========================================================================

    private static void WriteCleanReview(ToyFixture toy)
    {
        var shortHead = WorkflowEngine.Short(toy.Repo.CodeHead());
        TestGitFixture.Write(Path.Combine(toy.RepoDir, ".hoshin", "build", "review.md"), $$"""
            ```jsonc
            { "layout": 2 }
            ```

            # Review findings

            ## Review 1

            ```jsonc
            { "review": 1, "reviewed_at": "2026-11-22", "commits": { "repo": "{{shortHead}}" } }
            ```

            No findings.
            """);
    }

    private static void AdvanceToyToNeedsApproval(ToyFixture toy)
    {
        var head = toy.Repo.CodeHead();
        toy.Repo.UpdateLedger(l => l with
        {
            Verdicts =
            [
                new WorkflowVerdict("T1", 1, head, true, "observed", [], DateTimeOffset.UtcNow),
                new WorkflowVerdict("T2", 1, head, true, "observed", [], DateTimeOffset.UtcNow),
            ],
            Checks = [new WorkflowCheckRun(head, true, "ok", DateTimeOffset.UtcNow)],
            Reviews = [new WorkflowReviewRun(head, 1, 0, 0, DateTimeOffset.UtcNow)],
        });
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(toy.Repo)!));
    }

    [Fact]
    public void ClaudeCode_ModelInvokedAcceptAtNeedsApproval_DoesNotApproveOrPublish()
    {
        using var toy = new ToyFixture();
        AdvanceToyToNeedsApproval(toy);

        var (_, stdout, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hook_event_name = "PostToolUse",
            cwd = toy.RepoDir,
            session_id = "claude-s1",
            tool_name = "Skill",
            tool_input = new { skill = "hoshin:accept" },
        });

        Assert.Empty(toy.Repo.ReadLedger().WorkflowApprovalRecords);
        Assert.Empty(toy.Repo.ReadLedger().WorkflowPublishedRecords);
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(toy.Repo)!));
        Assert.Contains("/hoshin:accept", ClaudeContext(stdout), StringComparison.Ordinal);

        var remoteCheck = TestServices.CommandRunner.Run(new CommandRequest(
            toy.Temp.Path,
            ["git", "--git-dir", toy.RemoteDir, "rev-parse", "--verify", "refs/heads/change/greet-polish"]));
        Assert.False(remoteCheck.Ok, "Remote branch must not be pushed by model-invoked accept.");
    }

    [Fact]
    public void Antigravity_ModelReadAcceptSkillAtNeedsApproval_DoesNotApproveOrPublish()
    {
        using var toy = new ToyFixture();
        AdvanceToyToNeedsApproval(toy);

        var (_, postOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PostToolUse",
            conversationId = "agy-s1",
            cwd = toy.RepoDir,
            toolCall = new
            {
                id = "v1",
                name = "view_file",
                args = new { Path = Path.Combine(toy.RepoDir, ".agents", "skills", "accept", "SKILL.md") },
            },
        });
        Assert.Equal("{}", postOut);

        Assert.Empty(toy.Repo.ReadLedger().WorkflowApprovalRecords);
        Assert.Empty(toy.Repo.ReadLedger().WorkflowPublishedRecords);
        Assert.IsType<NeedsApproval>(WorkflowEngine.Next(TestServices.ReadState(toy.Repo)!));

        var (_, preOut, _) = RunHook(toy.RepoDir, "hook start", new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-s1",
            cwd = toy.RepoDir,
            invocationNum = 1,
            lastUserInput = "continue",
        });
        Assert.Contains("/accept", AgyInjectedMessage(preOut), StringComparison.Ordinal);
    }

    [Fact]
    public void ModelInvokedAcceptAtAwaitAcceptIntent_DoesNotAcceptIntent()
    {
        using var tmp = new TempDir("hoshin-await-s1-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "demo-s1", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/intent.md"] = """
                ```jsonc
                { "title": "Demo", "system": "demo", "author": "t", "date": "2026-11-22", "status": "draft", "scope": "demo" }
                ```

                # Intent: Demo

                ## Problem
                p
                ## Proposed outcome
                o
                ## Affected users and systems
                u
                ## Constraints
                c
                ## Decisions
                d
                ## Open questions
                q
                """,
        });
        var repo = TestServices.FindRepo(repoDir)!;
        var headBefore = repo.Head();
        Assert.IsType<AwaitAccept>(WorkflowEngine.Next(TestServices.ReadState(repo)!));

        // Claude model-invoked Skill("hoshin:accept") must NOT accept the intent.
        var (_, claudeOut, _) = RunHook(repoDir, "hook start", new
        {
            hook_event_name = "PostToolUse",
            cwd = repoDir,
            session_id = "c1",
            tool_name = "Skill",
            tool_input = new { skill = "hoshin:accept" },
        });
        Assert.Equal(headBefore, repo.Head());
        Assert.IsType<AwaitAccept>(WorkflowEngine.Next(TestServices.ReadState(repo)!));
        Assert.Contains("/hoshin:accept", ClaudeContext(claudeOut), StringComparison.Ordinal);

        // Antigravity model-invoked view_file on accept/SKILL.md must NOT accept the intent.
        RunHook(repoDir, "hook start", new
        {
            hookEventName = "PostToolUse",
            conversationId = "a1",
            cwd = repoDir,
            toolCall = new
            {
                id = "v2",
                name = "view_file",
                args = new { Path = Path.Combine(repoDir, ".agents", "skills", "accept", "SKILL.md") },
            },
        });
        Assert.Equal(headBefore, repo.Head());
        Assert.IsType<AwaitAccept>(WorkflowEngine.Next(TestServices.ReadState(repo)!));
    }

    // =========================================================================
    // 5. Harness-Neutral Skills Hygiene Check
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
