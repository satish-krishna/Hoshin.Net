using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class StopCounterAndQuestionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SkillsDir = Path.Combine(RepoRoot, "skills");

    private static (TempDir Temp, string RepoDir) CreateInvalidIntentDraftFixture()
    {
        var tmp = new TempDir("hoshin-t26-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["src/math.mjs"] = "export const x = 1;\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t26-stops", "type": "feat", "scope": "math", "base": "main" }
                """,
        });

        var orchestrator = new HookOrchestrator();
        orchestrator.HandleStart(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptExpansion",
            cwd = repoDir,
            session_id = "s1",
            command_name = "hoshin:intent",
            prompt = "/hoshin:intent Add subtract",
        })), SkillsDir);

        // Write an invalid partial draft (missing required headings).
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "intent.md"), """
            ```jsonc
            { "title": "Add subtract", "system": "math", "author": "PO", "date": "2026-11-22", "status": "draft", "scope": "math" }
            ```

            # Intent: Add subtract

            ## Problem

            Missing subtract.
            """);

        return (tmp, repoDir);
    }

    [Fact]
    public void StopWhileWriting_ConsecutiveUserTurnsWithStopHookActiveFalse_KeepReturningDiagnostics()
    {
        var (tmp, repoDir) = CreateInvalidIntentDraftFixture();
        using var cleanup = tmp;

        var orchestrator = new HookOrchestrator();
        for (var turn = 1; turn <= 4; turn++)
        {
            var (output, _) = orchestrator.HandleStop(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                cwd = repoDir,
                session_id = "s1",
                stop_hook_active = false,
                last_assistant_message = "I updated the draft intent.",
            })), SkillsDir);

            Assert.NotNull(output);
            var doc = JsonDocument.Parse(output!).RootElement;
            Assert.True(
                doc.TryGetProperty("decision", out var dec) && dec.GetString() == "block",
                $"Turn {turn} should block with diagnostics, got: {output}");
            Assert.Contains("## Open questions", doc.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public void StopWhileWriting_FourChainedStopsWithStopHookActiveTrue_GivesUpAfterThree()
    {
        var (tmp, repoDir) = CreateInvalidIntentDraftFixture();
        using var cleanup = tmp;

        var orchestrator = new HookOrchestrator();
        for (var i = 1; i <= 3; i++)
        {
            var (output, _) = orchestrator.HandleStop(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "Stop",
                cwd = repoDir,
                session_id = "s1",
                stop_hook_active = true,
                last_assistant_message = "Retrying draft.",
            })), SkillsDir);

            Assert.NotNull(output);
            Assert.Equal("block", JsonDocument.Parse(output!).RootElement.GetProperty("decision").GetString());
        }

        // 4th chained stop gives up with a system message instead of blocking.
        var (fourthOut, fourthDecision) = orchestrator.HandleStop(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            cwd = repoDir,
            session_id = "s1",
            stop_hook_active = true,
            last_assistant_message = "Still retrying.",
        })), SkillsDir);

        Assert.Contains("gave up", fourthDecision, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(fourthOut);
        var fourthDoc = JsonDocument.Parse(fourthOut!).RootElement;
        Assert.False(fourthDoc.TryGetProperty("decision", out _));
        Assert.Contains("still has problems after 3 tries", fourthDoc.GetProperty("systemMessage").GetString());
    }

    [Fact]
    public void StopWhileWriting_InvalidDraftWhenAssistantEndsWithQuestion_AllowsEngineerToAnswer()
    {
        var (tmp, repoDir) = CreateInvalidIntentDraftFixture();
        using var cleanup = tmp;

        var orchestrator = new HookOrchestrator();
        var (output, decision) = orchestrator.HandleStop(HarnessAdapter.Default.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "Stop",
            cwd = repoDir,
            session_id = "s1",
            stop_hook_active = false,
            last_assistant_message = "I started drafting intent.md. Should negative results return an error or wrap around?",
        })), SkillsDir);

        Assert.Null(output);
        Assert.Contains("question", decision, StringComparison.OrdinalIgnoreCase);
    }
}
