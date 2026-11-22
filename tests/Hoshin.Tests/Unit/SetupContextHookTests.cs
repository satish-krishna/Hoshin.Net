using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class SetupContextHookTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");

    [Fact]
    public void HandleStart_ContextDuringActiveBuild_LeavesRunAndStepUnchangedAndStopSilent()
    {
        using var tmp = new TempDir("hoshin-t23-ctx-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export const x = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t23-ctx", "type": "feat", "scope": "math", "base": "main" }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "math", "status": "accepted" }
                ```

                # Plan: Context

                ## Tasks

                ### T1: Add sub

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "--test", "test/t1.test.mjs"] }
                ```

                #### Tests
                Verify.
                """,
        });

        var repo = WorkflowRepo.Find(repoDir)!;
        var orchestrator = new HookOrchestrator();

        // Session A starts an Implement run.
        orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = "session-a",
            command_name = "hoshin:build",
            prompt = "/hoshin:build",
        })), SkillsDir);

        var runBefore = File.ReadAllText(repo.RunMarkerPath);
        var stepBefore = File.ReadAllText(repo.StepPath);

        // Session B starts context -> empty output, run.json and step.json untouched.
        var (ctxOut, _) = orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = "session-b",
            command_name = "hoshin:context",
            prompt = "/hoshin:context",
        })), SkillsDir);
        Assert.Null(ctxOut);
        Assert.Equal(runBefore, File.ReadAllText(repo.RunMarkerPath));
        Assert.Equal(stepBefore, File.ReadAllText(repo.StepPath));

        // Session B's next Stop returns empty output.
        var (stopOut, _) = orchestrator.HandleStop(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            cwd = repoDir,
            session_id = "session-b",
        })), SkillsDir);
        Assert.Null(stopOut);
    }

    [Fact]
    public void HandleStart_SetupInRepoWithNoChangePlan_ReturnsEmptyOutputNotNoPlanMessage()
    {
        using var tmp = new TempDir("hoshin-t23-setup-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["README.md"] = "# plain repo without change.jsonc\n",
        });

        var orchestrator = new HookOrchestrator();

        var (claudeOut, claudeDecision) = orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = "s-setup",
            command_name = "hoshin:setup",
            prompt = "/hoshin:setup",
        })), SkillsDir);
        Assert.Null(claudeOut);
        Assert.Contains("allowed", claudeDecision, StringComparison.OrdinalIgnoreCase);

        var (agyOut, agyDecision) = orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hookEventName = "PreInvocation",
            conversationId = "agy-setup",
            cwd = repoDir,
            workspacePaths = new[] { repoDir },
            invocationNum = 0,
            lastUserInput = "/setup",
        })), SkillsDir);
        Assert.Equal("{}", agyOut);
        Assert.Contains("allowed", agyDecision, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(repoDir, ".git", "hoshin")));
    }
}
