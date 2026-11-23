using System.Text.Json;
using Hoshin.Cli.Commands;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class GuardProtectionTests
{
    private static (TempDir Temp, string RepoDir, WorkflowRepo Repo) CreateActiveBuildFixture(string sessionId = "s1")
    {
        var tmp = new TempDir("hoshin-t03-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/app.mjs"] = "export const x = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t03-sec", "type": "feat", "scope": "sec", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "sec", "status": "accepted" }
                ```

                # Plan: Sec

                ## Tasks

                ### T1: First

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                ```

                #### Change
                Do T1.

                #### Tests
                Test T1.
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var now = DateTimeOffset.UtcNow;
        repo.WriteRunMarker(new RunMarker(now, sessionId, "claude-code", repo.ChangeFolder));
        repo.WriteStep(new CurrentStep("NeedsApproval", null, 0, repo.Head(), now, SessionId: sessionId, ChangeFolder: repo.ChangeFolder));
        return (tmp, repoDir, repo);
    }

    private static string? EvalGuard(string cwd, string sessionId, string toolName, object toolInput)
    {
        var raw = JsonSerializer.Serialize(new
        {
            hook_event_name = "PreToolUse",
            cwd,
            session_id = sessionId,
            tool_name = toolName,
            tool_input = toolInput,
        });
        var ev = TestServices.HookAdapter.Parse(raw);
        return TestServices.EvaluateGuard(ev).Reason;
    }

    [Theory]
    [InlineData("Bash", "echo x > .claude/settings.local.json")]
    [InlineData("Bash", "cd .git && cd hoshin && rm run.json")]
    [InlineData("Bash", "rm -r \"$(git rev-parse --git-common-dir)/hoshin\"")]
    public void Guard_DeniesShellTamperingWithHarnessConfigAndHoshinState(string tool, string command)
    {
        var (tmp, repoDir, _) = CreateActiveBuildFixture();
        using var _ = tmp;

        var reason = EvalGuard(repoDir, "s1", tool, new { command });
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("Write", ".claude/settings.json")]
    [InlineData("Edit", ".claude/settings.local.json")]
    [InlineData("Write", ".claude/hooks/disable.sh")]
    [InlineData("Edit", ".git/hooks/pre-commit")]
    public void Guard_DeniesFileEditsToHarnessConfigAndGitHooks(string tool, string relativePath)
    {
        var (tmp, repoDir, _) = CreateActiveBuildFixture();
        using var _ = tmp;

        var reason = EvalGuard(repoDir, "s1", tool, new { file_path = Path.Combine(repoDir, relativePath) });
        Assert.NotNull(reason);
    }

    [Fact]
    public void Guard_DeniesWriteToAbsoluteGitCommonDirHoshinPath_AndAllowsReadOfMarker()
    {
        var (tmp, repoDir, repo) = CreateActiveBuildFixture();
        using var _ = tmp;

        var target = Path.Combine(repo.HoshinDir, "run.json");
        var denyEdit = EvalGuard(repoDir, "s1", "Write", new { file_path = target });
        Assert.NotNull(denyEdit);

        var allowRead = EvalGuard(repoDir, "s1", "Bash", new { command = "cat .hoshin.json" });
        Assert.Null(allowRead);
    }

    [Fact]
    public void Orchestrator_BlocksStopWithTamperingMessage_WhenRunJsonDeletedMidBuild()
    {
        var (tmp, repoDir, repo) = CreateActiveBuildFixture();
        using var _ = tmp;

        var orchestrator = TestServices.CreateOrchestrator();
        var startPayload = JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = "s1",
            command_name = "hoshin:build",
        });
        orchestrator.HandleStart(TestServices.HookAdapter.Parse(startPayload), rolesRoot: null);
        Assert.True(File.Exists(repo.RunMarkerPath));
        Assert.NotEmpty(repo.ReadLedger().Issued);

        // Tampering: delete run.json mid-build.
        File.Delete(repo.RunMarkerPath);

        var stopPayload = JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            cwd = repoDir,
            session_id = "s1",
        });
        var (output, decision) = orchestrator.HandleStop(TestServices.HookAdapter.Parse(stopPayload), rolesRoot: null);
        Assert.NotNull(output);
        using var doc = JsonDocument.Parse(output!);
        Assert.Equal("block", doc.RootElement.GetProperty("decision").GetString());
        Assert.Contains("run.json", doc.RootElement.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tamper", decision, StringComparison.OrdinalIgnoreCase);
    }
}
