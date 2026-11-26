using System.Text.Json;
using Hoshin.Cli.Commands;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R01 (W14): Hook crashes must exit 0, still log to stderr, append a trace line to hooks.jsonl,
/// and emit a visible harness message for start, result, and stop without blocking Stop.
/// </summary>
public sealed class HookCrashReportingTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");

    [Fact]
    public void HookResult_ClaudeCode_WhenOrchestratorThrows_EmitsContextMessageTraceAndExitsZero()
    {
        using var fixture = CreateActiveBuildFixture("sess-claude");
        var cmd = CreateFailingHookCommand(new InvalidOperationException("boom in evaluator"));

        var payload = JsonSerializer.Serialize(new
        {
            session_id = "sess-claude",
            cwd = fixture.RepoDir,
            hook_event_name = "PostToolUse",
            tool_name = "Agent",
            tool_use_id = "toolu_1",
            tool_input = new { subagent_type = "general-purpose", description = "Build T1", prompt = $"Build T1\n{fixture.DispatchNonce}" },
            tool_response = new { status = "completed" },
        });

        string? stdout = null;
        var stderr = new List<string>();
        var exitCode = cmd.Execute(["result", "--roles", SkillsDir], () => payload, s => stdout = s, stderr.Add);

        Assert.Equal(ExitCodes.Pass, exitCode);
        Assert.Contains(stderr, line => line.Contains("hoshin hook:", StringComparison.Ordinal) && line.Contains("boom in evaluator", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(stdout));

        using var doc = JsonDocument.Parse(stdout!);
        var context = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
        Assert.Equal(
            "hoshin hit an internal error in `result`: `InvalidOperationException: boom in evaluator`. Tell the engineer; don't retry.",
            context);

        var trace = File.ReadAllText(fixture.Repo.HooksLogPath);
        Assert.Contains("InvalidOperationException", trace, StringComparison.Ordinal);
        Assert.Contains("boom in evaluator", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void HookResult_Antigravity_WhenOrchestratorThrows_EmitsInjectSystemMessageTraceAndExitsZero()
    {
        using var fixture = CreateActiveBuildFixture("conv-agy");
        var cmd = CreateFailingHookCommand(new InvalidOperationException("boom in agy"));

        var payload = JsonSerializer.Serialize(new
        {
            conversationId = "conv-agy",
            workspacePaths = new[] { fixture.RepoDir },
            hookEventName = "PostToolUse",
            toolCall = new
            {
                id = "call_1",
                name = "invoke_subagent",
                args = new { Prompt = $"Build T1\n{fixture.DispatchNonce}" },
            },
            toolResult = new { conversationId = "sub-1" },
        });

        string? stdout = null;
        var stderr = new List<string>();
        var exitCode = cmd.Execute(["result", "--roles", SkillsDir], () => payload, s => stdout = s, stderr.Add);

        Assert.Equal(ExitCodes.Pass, exitCode);
        Assert.Contains(stderr, line => line.Contains("hoshin hook:", StringComparison.Ordinal) && line.Contains("boom in agy", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(stdout));

        using var doc = JsonDocument.Parse(stdout!);
        var message = doc.RootElement.GetProperty("injectSteps")[0].GetProperty("systemMessage").GetProperty("systemMessage").GetString();
        Assert.Equal(
            "hoshin hit an internal error in `result`: `InvalidOperationException: boom in agy`. Tell the engineer; don't retry.",
            message);

        var trace = File.ReadAllText(fixture.Repo.HooksLogPath);
        Assert.Contains("InvalidOperationException", trace, StringComparison.Ordinal);
        Assert.Contains("boom in agy", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void HookStop_ClaudeCode_WhenOrchestratorThrows_InformsWithoutBlockingAndExitsZero()
    {
        using var fixture = CreateActiveBuildFixture("sess-claude-stop");
        var cmd = CreateFailingHookCommand(new InvalidOperationException("boom in stop"));

        var payload = JsonSerializer.Serialize(new
        {
            session_id = "sess-claude-stop",
            cwd = fixture.RepoDir,
            hook_event_name = "Stop",
            stop_hook_active = false,
            background_tasks = Array.Empty<object>(),
        });

        string? stdout = null;
        var stderr = new List<string>();
        var exitCode = cmd.Execute(["stop", "--roles", SkillsDir], () => payload, s => stdout = s, stderr.Add);

        Assert.Equal(ExitCodes.Pass, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout));

        using var doc = JsonDocument.Parse(stdout!);
        Assert.False(doc.RootElement.TryGetProperty("decision", out _), "Stop crash must not block the session.");
        var message = doc.RootElement.GetProperty("systemMessage").GetString();
        Assert.Equal(
            "hoshin hit an internal error in `stop`: `InvalidOperationException: boom in stop`. Tell the engineer; don't retry.",
            message);

        var trace = File.ReadAllText(fixture.Repo.HooksLogPath);
        Assert.Contains("InvalidOperationException", trace, StringComparison.Ordinal);
        Assert.Contains("boom in stop", trace, StringComparison.Ordinal);
    }

    [Fact]
    public void HookStop_Antigravity_WhenOrchestratorThrows_InformsWithoutContinuingAndExitsZero()
    {
        using var fixture = CreateActiveBuildFixture("conv-agy-stop");
        var cmd = CreateFailingHookCommand(new InvalidOperationException("boom in agy stop"));

        var payload = JsonSerializer.Serialize(new
        {
            conversationId = "conv-agy-stop",
            workspacePaths = new[] { fixture.RepoDir },
            hookEventName = "AfterAgent",
            fullyIdle = true,
            continuationActive = false,
        });

        string? stdout = null;
        var stderr = new List<string>();
        var exitCode = cmd.Execute(["stop", "--roles", SkillsDir], () => payload, s => stdout = s, stderr.Add);

        Assert.Equal(ExitCodes.Pass, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout));

        using var doc = JsonDocument.Parse(stdout!);
        Assert.False(doc.RootElement.TryGetProperty("decision", out _), "Antigravity Stop crash must not continue/trap the session.");
        var message = doc.RootElement.GetProperty("injectSteps")[0].GetProperty("systemMessage").GetProperty("systemMessage").GetString();
        Assert.Equal(
            "hoshin hit an internal error in `stop`: `InvalidOperationException: boom in agy stop`. Tell the engineer; don't retry.",
            message);
    }

    [Fact]
    public void HookStart_ClaudeCode_WhenOrchestratorThrows_EmitsContextMessageAndExitsZero()
    {
        using var fixture = CreateActiveBuildFixture("sess-claude-start");
        var cmd = CreateFailingHookCommand(new InvalidOperationException("boom in start"));

        var payload = JsonSerializer.Serialize(new
        {
            session_id = "sess-claude-start",
            cwd = fixture.RepoDir,
            hook_event_name = "UserPromptExpansion",
            command_name = "hoshin:build",
            command_args = "",
            prompt = "/hoshin:build",
        });

        string? stdout = null;
        var stderr = new List<string>();
        var exitCode = cmd.Execute(["start", "--roles", SkillsDir], () => payload, s => stdout = s, stderr.Add);

        Assert.Equal(ExitCodes.Pass, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout));

        using var doc = JsonDocument.Parse(stdout!);
        var context = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString();
        Assert.Equal(
            "hoshin hit an internal error in `start`: `InvalidOperationException: boom in start`. Tell the engineer; don't retry.",
            context);
    }

    [Fact]
    public void HookCommand_MissingLogUnknownAndSubagentStopCrash_AlwaysExitZero()
    {
        var cmd = new HookCommand(TestServices.HookAdapter, TestServices.HookOrchestrator, TestServices.Default.HookRunResolver);

        string? stdout = null;
        var stderr = new List<string>();
        Assert.Equal(ExitCodes.Pass, cmd.Execute([], () => "{}", s => stdout = s, stderr.Add));
        Assert.Contains(stderr, l => l.Contains("missing subcommand", StringComparison.OrdinalIgnoreCase));

        stdout = "sentinel";
        Assert.Equal(ExitCodes.Pass, cmd.Execute(["log"], () => """{"hook_event_name":"Stop","session_id":"s1"}""", s => stdout = s, stderr.Add));
        Assert.Null(stdout);

        stdout = "sentinel";
        Assert.Equal(ExitCodes.Pass, cmd.Execute(["bogus"], () => """{"hook_event_name":"Stop","session_id":"s1"}""", s => stdout = s, stderr.Add));
        Assert.Null(stdout);

        // Crash in subagent-stop emits no context/block output (BuildCrashOutput returns null), still exits 0 and logs stderr.
        using var fixture = CreateActiveBuildFixture("sess-sub");
        fixture.Repo.UpdateLedger(l => l with { ActiveSubagent = new ActiveSubagent("T1", 1, "agent-1", TestServices.Time.GetUtcNow()) });
        var step = fixture.Repo.ReadStep()!;
        fixture.Repo.WriteStep(step with { AgentId = "agent-1" });
        var failing = CreateFailingHookCommand(new InvalidOperationException("boom in subagent-stop"));
        stdout = null;
        stderr.Clear();
        var subPayload = JsonSerializer.Serialize(new { hook_event_name = "SubagentStop", session_id = "sess-sub", agent_id = "agent-1", cwd = fixture.RepoDir });
        Assert.Equal(ExitCodes.Pass, failing.Execute(["subagent-stop", "--roles", SkillsDir], () => subPayload, s => stdout = s, stderr.Add));
        Assert.Null(stdout);
        Assert.Contains(stderr, l => l.Contains("boom in subagent-stop", StringComparison.Ordinal));
    }

    private static HookCommand CreateFailingHookCommand(Exception toThrow)
    {
        var orchestrator = TestServices.CreateOrchestrator(runner: new ThrowingWorkflowRunner(toThrow));
        return new HookCommand(
            TestServices.HookAdapter,
            orchestrator,
            TestServices.Git,
            TestServices.Git,
            TestServices.Git,
            TestServices.FileSystem,
            TestServices.Ledger);
    }

    private static ActiveBuildFixture CreateActiveBuildFixture(string sessionId)
    {
        var temp = new TempDir("hoshin-r01-");
        var repoDir = Path.Combine(temp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/greet.mjs"] = "export const greet = () => 'hi';\n",
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "crash-test", "type": "feat", "scope": "greet", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "greet", "status": "accepted" }
                ```

                # Plan: Crash test

                ## Tasks

                ### T1: Test task

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                ```

                #### Change

                Update greet.

                #### Tests

                Check greet.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo, SkillsDir)!;
        var step = WorkflowEngine.Next(state);
        repo.WriteRunMarker(new RunMarker(TestServices.Time.GetUtcNow(), sessionId, "claude-code", repo.ChangeFolder, state.Plan.Change));
        TestServices.WorkflowRunner.Issue(repo, state, step, sessionId);
        var nonce = repo.ReadStep()!.DispatchNonce!;
        return new ActiveBuildFixture(temp, repoDir, repo, nonce);
    }

    private sealed record ActiveBuildFixture(TempDir Temp, string RepoDir, WorkflowRepo Repo, string DispatchNonce) : IDisposable
    {
        public void Dispose() => Temp.Dispose();
    }

    private sealed class ThrowingWorkflowRunner(Exception toThrow) : IWorkflowRunner
    {
        public void Issue(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId = null) => throw toThrow;
        public bool TryRegisterActiveSubagent(WorkflowRepo repo, string taskId, int attempt, string agentId, out string? existingAgentId) => throw toThrow;
        public void ClearActiveSubagent(WorkflowRepo repo, string? agentId = null) => throw toThrow;
        public WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt) => throw toThrow;
        public WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state, string? targetRepo = null) => throw toThrow;
        public WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state) => throw toThrow;
        public WorkflowApproval Answer(WorkflowRepo repo, WorkflowState state, bool approved, string note) => throw toThrow;
        public WorkflowPublishResult Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = true) => throw toThrow;
        public (IReadOnlyList<Problem> Problems, string? Commit) CommitDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document) => throw toThrow;
        public (IReadOnlyList<Problem> Problems, string? Commit) CommitReview(WorkflowRepo repo, WorkflowState state) => throw toThrow;
        public (IReadOnlyList<Problem> Problems, string? Commit) CommitFixDocuments(WorkflowRepo repo, WorkflowState state) => throw toThrow;
        public string? AcceptDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document) => throw toThrow;
        public IReadOnlyList<string> AcceptFixDocuments(WorkflowRepo repo, WorkflowState state) => throw toThrow;
        public string? ReopenDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document) => throw toThrow;
        public string? RejectIntent(WorkflowRepo repo, WorkflowState state) => throw toThrow;
    }
}
