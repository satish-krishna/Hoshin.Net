using System.Text.Json;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R09 (S11): The PreToolUse guard covers every shell and write tool Claude Code offers, including
/// <c>PowerShell</c> and <c>mcp__*</c> write/push tools, synced from a single definition in <c>HarnessTools</c>.
/// </summary>
public sealed class GuardShellAndMcpToolTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static (TempDir Temp, string RepoDir) CreateActiveBuildFixture(string sessionId = "s-r09")
    {
        var tmp = new TempDir("hoshin-r09-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/app.mjs"] = "export const x = 1;\n",
            [".hoshin/change.jsonc"] = """{ "version": 1, "change": "r09-guard", "type": "feat", "scope": "demo", "base": "main" }""",
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Guard tools

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
        repo.WriteRunMarker(new RunMarker(now, sessionId, "claude-code", repo.ChangeFolder, repo.ChangeKey));
        repo.WriteStep(new CurrentStep(StepKind.Implement, "T1", 1, repo.Head(), now, SessionId: sessionId, ChangeFolder: repo.ChangeFolder));
        return (tmp, repoDir);
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
        return TestServices.EvaluateGuard(TestServices.HookAdapter.Parse(raw)).Reason;
    }

    [Fact]
    public void Guard_DuringActiveBuild_DeniesGitPushAndMutationsThroughPowerShellTool()
    {
        var (tmp, repoDir) = CreateActiveBuildFixture();
        using var _ = tmp;

        var pushDeny = EvalGuard(repoDir, "s-r09", "PowerShell", new { command = "git push origin HEAD:refs/heads/main" });
        Assert.NotNull(pushDeny);

        var commitDeny = EvalGuard(repoDir, "s-r09", "PowerShell", new { command = "git commit -m \"feat(demo): hand commit\"" });
        Assert.NotNull(commitDeny);
    }

    [Theory]
    [InlineData("mcp__github__push_files", true)]
    [InlineData("mcp__filesystem__write_file", true)]
    [InlineData("mcp__filesystem__edit_file", true)]
    [InlineData("mcp__github__create_or_update_file", true)]
    [InlineData("mcp__github__create_pull_request", true)]
    [InlineData("mcp__filesystem__read_file", false)]
    [InlineData("mcp__github__get_file_contents", false)]
    public void Guard_DuringActiveBuild_DeniesMcpWriteAndPushTools_AndAllowsReadOnlyMcpTools(string toolName, bool expectedDenied)
    {
        var (tmp, repoDir) = CreateActiveBuildFixture();
        using var _ = tmp;

        var reason = EvalGuard(repoDir, "s-r09", toolName, new { path = "src/app.mjs", content = "export const x = 2;\n" });
        if (expectedDenied)
        {
            Assert.NotNull(reason);
        }
        else
        {
            Assert.Null(reason);
        }
    }

    [Fact]
    public void ShippedClaudeHooksJson_PreToolUseMatchers_IncludePowerShellAndMcpWritePushTools()
    {
        var hooksJson = File.ReadAllText(Path.Combine(RepoRoot, "packaging", "claude-code", "hooks", "hooks.json"));
        using var doc = JsonDocument.Parse(hooksJson);
        var matchers = doc.RootElement.GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray()
            .Select(e => e.GetProperty("matcher").GetString() ?? "")
            .ToList();

        var shellMatcher = matchers.Single(m => m.Contains("Bash", StringComparison.Ordinal));
        Assert.Contains("PowerShell", shellMatcher.Split('|'));
        Assert.Contains(matchers, m => m.Contains("mcp__", StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_DuringWriteDocumentStep_DeniesMcpWriteTools()
    {
        var (tmp, repoDir) = CreateActiveBuildFixture();
        using var _ = tmp;

        var repo = TestServices.FindRepo(repoDir)!;
        repo.WriteStep(new CurrentStep(StepKind.WriteDocument, "intent", 0, repo.Head(), DateTimeOffset.UtcNow, SessionId: "s-r09", ChangeFolder: repo.ChangeFolder));

        var reason = EvalGuard(repoDir, "s-r09", "mcp__filesystem__write_file", new { path = "src/app.mjs" });
        Assert.NotNull(reason);
        Assert.Contains("MCP", reason!, StringComparison.Ordinal);
    }
}
