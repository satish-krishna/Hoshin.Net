using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class BaseBranchChecksTests
{
    [Fact]
    public void WorkflowStateRead_ReadsBuildFinishChecksFromBaseBranchNotWorkingTree()
    {
        using var tmp = new TempDir("hoshin-t05-base-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "t05-checks", "type": "feat", "scope": "demo", "base": "main" }
                """,
            [".hoshin/hooks.jsonc"] = """
                {
                  "hooks": [
                    { "event": "build.finish", "run": ["node", "-e", "process.exit(1)"] }
                  ]
                }
                """,
            [".hoshin/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: Base checks

                ## Tasks

                ### T1: First

                ```jsonc
                { "id": "T1", "repo": "repo", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Change
                Do T1.

                #### Tests
                Test T1.
                """,
        });

        // Tamper with working tree hooks.jsonc to drop all checks.
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "hooks.jsonc"), "{ \"hooks\": [] }\n");

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;

        Assert.Single(state.Plan.Checks);
        Assert.Equal(["node", "-e", "process.exit(1)"], state.Plan.Checks[0]);
    }

    [Fact]
    public void WorkflowStateRead_ReadsChecksFromBaseBranchMarkerAndSystemHooksJson()
    {
        using var tmp = new TempDir("hoshin-t05-sys-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": { "demo": { "apps": { "app": "." } } },
                  "hooks": {
                    "build.finish": [
                      { "id": "global-check", "run": ["node", "-e", "process.exit(0)"] }
                    ]
                  }
                }
                """,
            ["intent/demo/hooks.json"] = """
                {
                  "version": 1,
                  "hooks": {
                    "build.finish": [
                      { "id": "sys-check", "run": ["node", "-e", "process.exit(1)"] },
                      { "id": "non-blocking", "blocking": false, "run": ["node", "-e", "process.exit(1)"] }
                    ]
                  }
                }
                """,
            ["intent/demo/c1/change.jsonc"] = """
                { "version": 1, "change": "c1", "type": "feat", "scope": "demo", "base": "main" }
                """,
            ["intent/demo/c1/plan.md"] = """
                ```jsonc
                { "layout": 2, "system": "demo", "status": "accepted" }
                ```

                # Plan: System hooks

                ## Tasks

                ### T1: First

                ```jsonc
                { "id": "T1", "repo": "app", "test": ["node", "-e", "process.exit(0)"] }
                ```

                #### Change
                Do T1.

                #### Tests
                Test T1.
                """,
        });

        // Tamper with working tree .hoshin.json and intent/demo/hooks.json to drop checks.
        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin.json"), "{ \"version\": 1, \"systems\": { \"demo\": { \"apps\": { \"app\": \".\" } } } }\n");
        TestGitFixture.Write(Path.Combine(repoDir, "intent", "demo", "hooks.json"), "{ \"version\": 1, \"hooks\": {} }\n");

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;

        Assert.Equal(2, state.Plan.Checks.Count);
        Assert.Contains(state.Plan.Checks, c => c.SequenceEqual(["node", "-e", "process.exit(0)"]));
        Assert.Contains(state.Plan.Checks, c => c.SequenceEqual(["node", "-e", "process.exit(1)"]));
    }
}
