using System.Text.Json;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class SingleOwnerAndEnumTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void HookManifests_MatchersEqualHarnessToolsLists()
    {
        var claudePath = Path.Combine(RepoRoot, "packaging", "claude-code", "hooks", "hooks.json");
        using var claudeDoc = JsonDocument.Parse(File.ReadAllText(claudePath));
        var claudeHooks = claudeDoc.RootElement.GetProperty("hooks");

        Assert.Equal(
            HarnessTools.ToMatcher(HarnessTools.ClaudeGuardTools),
            claudeHooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        Assert.Equal(
            HarnessTools.ClaudeMcpWritePushMatcher,
            claudeHooks.GetProperty("PreToolUse")[1].GetProperty("matcher").GetString());
        Assert.Equal(
            HarnessTools.ToMatcher(HarnessTools.ClaudeSkillTools),
            claudeHooks.GetProperty("PostToolUse")[0].GetProperty("matcher").GetString());
        Assert.Equal(
            HarnessTools.ToMatcher(HarnessTools.ClaudeSubagentTools),
            claudeHooks.GetProperty("PostToolUse")[1].GetProperty("matcher").GetString());

        var agyPath = Path.Combine(RepoRoot, "packaging", "antigravity", "hooks.json");
        using var agyDoc = JsonDocument.Parse(File.ReadAllText(agyPath));
        var agyHooks = agyDoc.RootElement.GetProperty("hoshin");

        Assert.Equal(
            HarnessTools.ToMatcher(HarnessTools.AntigravityGuardTools),
            agyHooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString());
        Assert.Equal(
            HarnessTools.ToMatcher(HarnessTools.AntigravitySkillReadTools),
            agyHooks.GetProperty("PostToolUse")[0].GetProperty("matcher").GetString());
        Assert.Equal(
            HarnessTools.ToMatcher(HarnessTools.AntigravityResultTools),
            agyHooks.GetProperty("PostToolUse")[1].GetProperty("matcher").GetString());
    }

    [Fact]
    public void LoadRoleProcedure_LoadsEmbeddedImplementProcedureAndFailsLoudlyWhenMissing()
    {
        var expected = File.ReadAllText(Path.Combine(RepoRoot, "skills", "implement", "procedure.md")).Replace("\r\n", "\n").Trim();
        var actual = WorkflowBriefs.LoadRoleProcedure(skillsRoot: null, "implement");
        Assert.Equal(expected, actual);

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowBriefs.LoadRoleProcedure(skillsRoot: null, "nonexistent-role"));
        Assert.Contains("nonexistent-role", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IntentBrief_UsesIntentReaderRequiredHeadings()
    {
        var doc = new WorkflowDocState(Path.Combine("repo", "intent", "demo", "c1", "intent.md"), false, false, false, null, null);
        var state = new WorkflowState(
            "repo",
            Path.Combine("repo", "intent", "demo", "c1"),
            new WorkflowPlan("c1", "demo", "main", [], [], HasPlan: false),
            "abc1234",
            Hoshin.Workflow.Events.LedgerStore.Empty(),
            null,
            doc,
            doc,
            doc);

        var brief = WorkflowBriefs.Intent(state);
        foreach (var heading in IntentReader.RequiredHeadings)
        {
            Assert.Contains($"`{heading}`", brief, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProtectedPaths_MatchesProtectedDirsMarkerAndChangeFolder()
    {
        var root = Path.Combine("repo");
        var change = Path.Combine(root, "intent", "demo", "c1");
        Assert.True(ProtectedPaths.IsProtectedDuringBuild(".hoshin.json", root, change));
        Assert.True(ProtectedPaths.IsProtectedDuringBuild(".hoshin/plan.md", root, change));
        Assert.True(ProtectedPaths.IsProtectedDuringBuild(".git/HEAD", root, change));
        Assert.True(ProtectedPaths.IsProtectedDuringBuild(".agents/hooks.json", root, change));
        Assert.True(ProtectedPaths.IsProtectedDuringBuild(".claude-plugin/plugin.json", root, change));
        Assert.True(ProtectedPaths.IsProtectedDuringBuild("intent/demo/c1/spec.md", root, change));
        Assert.False(ProtectedPaths.IsProtectedDuringBuild("src/app.cs", root, change));
    }

    [Fact]
    public void CurrentStep_SerializesStepKindEnumAsWireString()
    {
        var step = new CurrentStep(StepKind.Implement, "T1", 1, "abc1234", DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(step, WorkflowJsonContext.Default.CurrentStep);
        Assert.Contains("\"kind\": \"Implement\"", json, StringComparison.Ordinal);
        var roundTrip = JsonSerializer.Deserialize(json, WorkflowJsonContext.Default.CurrentStep);
        Assert.NotNull(roundTrip);
        Assert.Equal(StepKind.Implement, roundTrip!.Kind);
        Assert.Equal("intent", WorkflowDocument.Intent.ToWireName());
        Assert.Equal("spec", WorkflowDocument.Spec.ToWireName());
        Assert.Equal("plan", WorkflowDocument.Plan.ToWireName());
    }
}
