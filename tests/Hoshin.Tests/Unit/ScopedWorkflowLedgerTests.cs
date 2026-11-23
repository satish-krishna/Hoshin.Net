using System.Text.Json;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class ScopedWorkflowLedgerTests
{
    private static string PlanText(string title) => $$"""
        ```jsonc
        { "layout": 2, "system": "demo", "status": "accepted" }
        ```

        # Plan: {{title}}

        ## Tasks

        ### T1: First task

        ```jsonc
        { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
        ```

        #### Change
        Do T1.

        #### Tests
        Test T1.
        """;

    [Fact]
    public void Ledger_ScopedPerChange_SecondChangeWithSameTaskIdT1StartsAtImplementT1()
    {
        using var tmp = new TempDir("hoshin-t07-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["intent/demo/change-a/change.jsonc"] = """
                { "version": 1, "change": "change-a", "type": "feat", "scope": "demo", "base": "main" }
                """,
            ["intent/demo/change-a/plan.md"] = PlanText("Change A"),
            ["intent/demo/change-b/change.jsonc"] = """
                { "version": 1, "change": "change-b", "type": "feat", "scope": "demo", "base": "main" }
                """,
            ["intent/demo/change-b/plan.md"] = PlanText("Change B"),
        });

        var repoA = TestServices.FindRepo(repoDir, "intent/demo/change-a")!;
        repoA.UpdateLedger(l => l with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, repoA.Head(), true, "observed", [], DateTimeOffset.UtcNow)],
        });

        Assert.IsType<RunChecks>(WorkflowEngine.Next(TestServices.ReadState(repoA)!));
        Assert.True(File.Exists(Path.Combine(repoA.HoshinDir, "demo", "change-a", "master.json")));

        // Switch run to change B (which also has task T1).
        var repoB = TestServices.FindRepo(repoDir, "intent/demo/change-b")!;
        repoB.WriteRunMarker(new RunMarker(DateTimeOffset.UtcNow, "s1", "claude-code", repoB.ChangeFolder));

        var nextB = Assert.IsType<Implement>(WorkflowEngine.Next(TestServices.ReadState(repoB)!));
        Assert.Equal("T1", nextB.Agent.Task);
    }

    [Fact]
    public void Ledger_MigratesLegacyFileOnce_WhenRunJsonNamesChange()
    {
        using var tmp = new TempDir("hoshin-t07-mig-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["intent/demo/change-a/change.jsonc"] = """
                { "version": 1, "change": "change-a", "type": "feat", "scope": "demo", "base": "main" }
                """,
            ["intent/demo/change-a/plan.md"] = PlanText("Change A"),
        });

        var repoA = TestServices.FindRepo(repoDir, "intent/demo/change-a")!;
        repoA.WriteRunMarker(new RunMarker(DateTimeOffset.UtcNow, "s1", "claude-code", repoA.ChangeFolder));

        var legacyPath = Path.Combine(repoA.HoshinDir, "workflow-ledger.json");
        var legacyJson = JsonSerializer.Serialize(new
        {
            verdicts = new[]
            {
                new WorkflowVerdict("T1", 1, repoA.Head(), true, "observed", [], DateTimeOffset.UtcNow),
            },
        });
        File.WriteAllText(legacyPath, legacyJson);

        var read = repoA.ReadLedger();
        Assert.Single(read.WorkflowVerdicts);
        Assert.False(File.Exists(legacyPath), "Legacy ledger should be migrated once.");
        Assert.True(File.Exists(Path.Combine(repoA.HoshinDir, "demo", "change-a", "master.json")));
    }
}
