using System.Text.Json;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class GuardScopeTests
{
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");

    private static (int ExitCode, string Stdout, string Stderr) RunCli(string cwd, string[] subArgs, object payload)
    {
        var env = new Dictionary<string, string>(TestGitFixture.GitEnv) { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };
        var res = TestServices.CommandRunner.Run(new CommandRequest(
            WorkingDirectory: cwd,
            Argv: ["dotnet", HoshinDll, .. subArgs],
            StandardInput: JsonSerializer.Serialize(payload),
            ExtraEnvironment: env));
        Assert.Equal(0, res.ExitCode);
        return (res.ExitCode ?? -1, res.Stdout, res.Stderr);
    }

    [Fact]
    public void HookAndGuard_DoNotCreateHoshinDirInPlainRepo()
    {
        using var tmp = new TempDir("hoshin-plain-scope-");
        var repoDir = Path.Combine(tmp.Path, "plain");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string> { ["README.md"] = "# plain\n" });
        var hoshinDir = Path.Combine(repoDir, ".git", "hoshin");

        var (_, guardOut, _) = RunCli(repoDir, ["guard"], new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command = "git push origin main" },
        });
        Assert.Equal("", guardOut);
        Assert.False(Directory.Exists(hoshinDir));

        var (_, stopOut, _) = RunCli(repoDir, ["hook", "stop"], new
        {
            hook_event_name = "Stop",
            cwd = repoDir,
            session_id = "s1",
        });
        Assert.Equal("", stopOut);
        Assert.False(Directory.Exists(hoshinDir));
    }

    [Fact]
    public void Guard_AllowsStaleRunOlderThan24Hours()
    {
        using var tmp = new TempDir("hoshin-stale-scope-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string> { ["README.md"] = "# repo\n" });
        var repo = TestServices.FindRepo(repoDir)!;
        var staleTime = DateTimeOffset.UtcNow.AddHours(-25);
        repo.WriteRunMarker(new RunMarker(staleTime, "s1", "claude-code", repo.ChangeFolder));
        repo.WriteStep(new CurrentStep(StepKind.NeedsApproval, null, 0, null, staleTime, SessionId: "s1", ChangeFolder: repo.ChangeFolder));

        var (_, guardOut, _) = RunCli(repoDir, ["guard"], new
        {
            hook_event_name = "PreToolUse",
            cwd = repoDir,
            session_id = "s1",
            tool_name = "Bash",
            tool_input = new { command = "git push origin main" },
        });
        Assert.Equal("", guardOut);
    }
}
