using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class ChangeStateGatingTests
{
    private const string Change = "2026-11-01-greeting";
    private const string Folder = $"intent/demo/{Change}";

    [Fact]
    public void Gate_RefusesBuild_WhenPlanSourceSpecCommitIsStale_WithSameReasonAsHoshinState()
    {
        using var tmp = new TempDir("hoshin-a4-stale-pin-");
        var repoDir = CreateAcceptedWorkspace(tmp.Path, out var intentSha, out _);

        // Update spec.md after plan.md was pinned to the earlier spec commit.
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "spec.md"),
            SpecText("accepted", intentSha, extraScenario: true));
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): update spec {Change}");

        var stateResult = TestServices.Default.ChangeState.Compute(
            repoDir,
            new StateOptions(Change: Change, System: "demo"));
        var expectedStop = Assert.Single(stateResult.Changes).Ready.Build.Stop;
        Assert.Equal("This plan was not written from the spec that is accepted now. Rerun the plan skill.", expectedStop);

        var repo = TestServices.FindRepo(repoDir, Folder)!;
        var state = TestServices.ReadState(repo)!;
        var actualGate = WorkflowEngine.Gate(state, "build");

        Assert.Equal(expectedStop, actualGate);
    }

    [Fact]
    public void Gate_RefusesBuildAndAllowsPlan_WhenPlanIsSuperseded_WithSameReasonAsHoshinState()
    {
        using var tmp = new TempDir("hoshin-a4-superseded-");
        var repoDir = CreateAcceptedWorkspace(tmp.Path, out _, out var specSha);

        // Mark plan.md as superseded and commit.
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "plan.md"),
            PlanText("superseded", specSha));
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): supersede plan {Change}");

        var stateResult = TestServices.Default.ChangeState.Compute(
            repoDir,
            new StateOptions(Change: Change, System: "demo"));
        var changeItem = Assert.Single(stateResult.Changes);
        var expectedBuildStop = changeItem.Ready.Build.Stop;
        Assert.Equal("This plan is `superseded`. The engineer accepts it by changing its status to `accepted` and committing.", expectedBuildStop);
        Assert.Null(changeItem.Ready.Plan.Stop);
        Assert.True(changeItem.Ready.Plan.Candidate);

        var repo = TestServices.FindRepo(repoDir, Folder)!;
        var state = TestServices.ReadState(repo)!;

        Assert.Equal(expectedBuildStop, WorkflowEngine.Gate(state, "build"));
        Assert.Null(WorkflowEngine.Gate(state, "plan"));
    }

    [Fact]
    public void Gate_RefusesSpecAndPlan_OnLightTrackChange_WithSameReasonAsHoshinState()
    {
        using var tmp = new TempDir("hoshin-a4-light-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": { "demo": { "apps": { "app": "." } } }
                }
                """,
            [$"{Folder}/change.jsonc"] = $$"""
                { "version": 1, "change": "{{Change}}", "type": "fix", "scope": "demo", "base": "main", "branch": "change/{{Change}}" }
                """,
            [$"{Folder}/intent.md"] = """
                ```jsonc
                { "title": "Quick fix", "system": "demo", "author": "t", "date": "2026-11-01", "status": "accepted", "scope": "demo", "track": "fix" }
                ```

                # Intent: Quick fix
                """,
        });

        var stateResult = TestServices.Default.ChangeState.Compute(
            repoDir,
            new StateOptions(Change: Change, System: "demo"));
        var changeItem = Assert.Single(stateResult.Changes);
        var expectedStop = $"`{Change}` is on the light track; `fix --accept {Change}` finishes or revises its documents.";
        Assert.Equal(expectedStop, changeItem.Ready.Spec.Stop);
        Assert.Equal(expectedStop, changeItem.Ready.Plan.Stop);

        var repo = TestServices.FindRepo(repoDir, Folder)!;
        var state = TestServices.ReadState(repo)!;

        Assert.Equal(expectedStop, WorkflowEngine.Gate(state, "spec"));
        Assert.Equal(expectedStop, WorkflowEngine.Gate(state, "plan"));
    }

    [Fact]
    public void Gate_RefusesBuild_WhenPlanAcceptanceIsUncommitted_WithSameReasonAsHoshinState()
    {
        using var tmp = new TempDir("hoshin-a4-uncommitted-");
        var repoDir = CreateAcceptedWorkspace(tmp.Path, out _, out var specSha);

        // Edit plan.md in working tree without committing.
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "plan.md"),
            PlanText("accepted", specSha) + "\n<!-- edited -->\n");

        var stateResult = TestServices.Default.ChangeState.Compute(
            repoDir,
            new StateOptions(Change: Change, System: "demo"));
        var expectedStop = Assert.Single(stateResult.Changes).Ready.Build.Stop;
        Assert.Equal("`plan.md` has uncommitted changes. Commit the acceptance first, so history shows what was accepted.", expectedStop);

        var repo = TestServices.FindRepo(repoDir, Folder)!;
        var state = TestServices.ReadState(repo)!;

        Assert.Equal(expectedStop, WorkflowEngine.Gate(state, "build"));
    }

    private static string CreateAcceptedWorkspace(string root, out string intentSha, out string specSha)
    {
        var repoDir = Path.Combine(root, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": { "demo": { "apps": { "app": "." } } }
                }
                """,
            ["src/index.mjs"] = "export const ok = true;\n",
        });

        TestGitFixture.Git(repoDir, "checkout", "-q", "-b", $"change/{Change}");
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "change.jsonc"),
            $$"""
            { "version": 1, "change": "{{Change}}", "type": "feat", "scope": "demo", "base": "main", "branch": "change/{{Change}}" }
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "intent.md"),
            """
            ```jsonc
            { "title": "Greeting", "system": "demo", "author": "t", "date": "2026-11-01", "status": "accepted", "scope": "demo" }
            ```

            # Intent: Greeting
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept intent {Change}");
        intentSha = TestGitFixture.Git(repoDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "spec.md"),
            SpecText("accepted", intentSha, extraScenario: false));
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept spec {Change}");
        specSha = TestGitFixture.Git(repoDir, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(repoDir, Folder, "plan.md"),
            PlanText("accepted", specSha));
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", $"docs(demo): accept plan {Change}");

        return repoDir;
    }

    private static string SpecText(string status, string intentSha, bool extraScenario)
    {
        var second = extraScenario
            ? """

                @R1.2
                Scenario: Greets empty name
                  Given an empty name
                  When greeted
                  Then the result is "hello"
              """
            : "";
        return $$"""
            ```jsonc
            { "layout": 2, "system": "demo", "status": "{{status}}", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Greeting

            ```gherkin
            Feature: Greeting

              @R1 @from:intent
              Rule: Greet callers by name

                @R1.1
                Scenario: Greets by name
                  Given a name "world"
                  When greeted
                  Then the result is "hello world"{{second}}
            ```
            """;
    }

    private static string PlanText(string status, string specSha) => $$"""
        ```jsonc
        { "layout": 2, "system": "demo", "status": "{{status}}", "source_spec_commit": "{{specSha}}" }
        ```

        # Plan: Greeting

        ## Tasks

        ### T1: Add greeting helper

        ```jsonc
        { "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1"], "supports": [], "test": ["node", "--test", "test/greet.test.mjs"] }
        ```

        #### Change
        Add `src/greet.mjs`.

        #### Tests
        Add `test/greet.test.mjs`.
        """;
}
