using System.Text.Json;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// R05 (W6): Task completion progress comes from the committed build logs (<c>build/&lt;repo&gt;.md</c>),
/// matching <c>hoshin state</c>, so builds resumed on a fresh clone or another machine never re-issue
/// already-logged tasks and report ledger-vs-log disagreements once.
/// </summary>
public sealed class BuildLogProgressTests
{
    [Fact]
    public void FreshClone_WithT1LoggedInCommittedBuildLogAndEmptyHoshinDir_IssuesT2AndAgreesWithState()
    {
        using var tmp = new TempDir("hoshin-r05-clone-");
        var wsRoot = Path.Combine(tmp.Path, "ws");
        var masterDir = Path.Combine(wsRoot, "ai-sdlc");
        var appDir = Path.Combine(wsRoot, "app");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "systems": {
                    "shop": {
                      "repos": ["app"]
                    }
                  }
                }
                """,
        });
        TestGitFixture.InitRepo(appDir, new Dictionary<string, string>
        {
            ["src/calc.mjs"] = "export const add = (a, b) => a + b;\n",
        });

        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "two-tasks");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        // Seed accepted intent, spec, plan with T1 and T2 in `app`.
        TestGitFixture.Write(
            Path.Combine(changeFolder, "intent.md"),
            """
            ```jsonc
            { "title": "Two tasks", "system": "shop", "author": "Pat", "date": "2026-11-26", "status": "accepted", "scope": "shop" }
            ```

            # Intent: Two tasks
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept intent {changeName}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(changeFolder, "spec.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Two tasks

            ```gherkin
            Feature: Two tasks

              @R1 @from:intent
              Rule: Multiply and divide
                @R1.1
                Scenario: Multiply
                  Given 3 and 4
                  When multiplied
                  Then 12

                @R1.2
                Scenario: Subtract
                  Given 5 and 2
                  When subtracted
                  Then 3
            ```
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept spec {changeName}", "-m", "Generated-By: hoshin");
        var specSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(
            Path.Combine(changeFolder, "plan.md"),
            $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Two tasks

            ## Tasks

            ### T1: Multiply

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1.1"], "test": ["node", "--test", "test/mul.test.mjs"] }
            ```

            #### Change
            Add multiply.

            #### Tests
            Test multiply.

            ### T2: Subtract

            ```jsonc
            { "id": "T2", "repo": "app", "depends_on": ["T1"], "covers": ["R1.2"], "test": ["node", "--test", "test/sub.test.mjs"] }
            ```

            #### Change
            Add subtract.

            #### Tests
            Test subtract.
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept plan {changeName}", "-m", "Generated-By: hoshin");

        // Commit T1 work in `app` and commit `build/app.md` logging T1 in the master copy.
        TestGitFixture.Git(appDir, "checkout", "-q", "-b", $"change/{changeName}");
        TestGitFixture.Write(Path.Combine(appDir, "src", "calc.mjs"), "export const add = (a, b) => a + b;\nexport const mul = (a, b) => a * b;\n");
        TestGitFixture.Git(appDir, "add", ".");
        TestGitFixture.Git(appDir, "commit", "-q", "-m", "feat(shop): Multiply", "-m", "Plan-Task: T1\nGenerated-By: hoshin");
        var t1Sha = TestGitFixture.Git(appDir, "rev-parse", "HEAD");

        TestServices.Default.BuildLogWriter.AppendEntry(changeFolder, "app", "T1", [t1Sha], "observed");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): log T1 {changeName}", "-m", "Generated-By: hoshin");

        // Simulate a fresh clone: wipe .git/hoshin completely in master and app!
        var masterHoshin = Path.Combine(masterDir, ".git", "hoshin");
        if (Directory.Exists(masterHoshin))
        {
            Directory.Delete(masterHoshin, recursive: true);
        }

        var appHoshin = Path.Combine(appDir, ".git", "hoshin");
        if (Directory.Exists(appHoshin))
        {
            Directory.Delete(appHoshin, recursive: true);
        }

        // System A `hoshin state` says T1 is logged and next ready task for `app` is T2.
        var changeState = TestServices.Default.ChangeState.Compute(appDir, new StateOptions());
        var item = Assert.Single(changeState.Changes, c => c.Change == changeName);
        Assert.Contains("T1", item.Tasks!["app"].Logged);
        Assert.Equal("T2", item.Schedule!.Repos.Single(r => r.Repo == "app").Task);

        // Hook `/hoshin:build` from `app` must also issue T2, NOT T1!
        var (startOut, _) = TestServices.HookOrchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = "fresh-clone-r05",
                cwd = appDir,
                command_name = "hoshin:build",
                command_args = "",
                prompt = "/hoshin:build",
            })),
            rolesRoot: null);

        Assert.NotNull(startOut);
        using var doc = JsonDocument.Parse(startOut!);
        var ctx = doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("# Task T2:", ctx, StringComparison.Ordinal);
        Assert.DoesNotContain("# Task T1:", ctx, StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerAndBuildLogDisagreement_LogsWinAndDisagreementIsReportedOnce()
    {
        using var tmp = new TempDir("hoshin-r05-disagree-");
        var wsRoot = Path.Combine(tmp.Path, "ws");
        var masterDir = Path.Combine(wsRoot, "ai-sdlc");
        var appDir = Path.Combine(wsRoot, "app");

        TestGitFixture.InitRepo(masterDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """{ "version": 1, "systems": { "shop": { "repos": ["app"] } } }""",
        });
        TestGitFixture.InitRepo(appDir, new Dictionary<string, string> { ["src/a.mjs"] = "export const a = 1;\n" });

        var started = TestServices.Default.ChangeStarter.StartFromHook(masterDir, "intent", "disagree");
        Assert.True(started.Ok, started.Error);
        var copyRoot = started.CopyRoot!;
        var changeFolder = started.ChangeFolder!;
        var changeName = started.ChangeName!;

        TestGitFixture.Write(Path.Combine(changeFolder, "intent.md"), "```jsonc\n{ \"title\": \"Disagree\", \"system\": \"shop\", \"author\": \"Pat\", \"date\": \"2026-11-26\", \"status\": \"accepted\", \"scope\": \"shop\" }\n```\n\n# Intent: Disagree\n");
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept intent {changeName}", "-m", "Generated-By: hoshin");
        var intentSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(Path.Combine(changeFolder, "spec.md"), $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_intent_commit": "{{intentSha}}" }
            ```

            # Spec: Disagree

            ```gherkin
            Feature: Disagree
              @R1 @from:intent
              Rule: One
                @R1.1
                Scenario: S1
                  Given x
                  When y
                  Then z
            ```
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept spec {changeName}", "-m", "Generated-By: hoshin");
        var specSha = TestGitFixture.Git(copyRoot, "rev-parse", "HEAD");

        TestGitFixture.Write(Path.Combine(changeFolder, "plan.md"), $$"""
            ```jsonc
            { "layout": 2, "system": "shop", "status": "accepted", "source_spec_commit": "{{specSha}}" }
            ```

            # Plan: Disagree

            ## Tasks

            ### T1: First

            ```jsonc
            { "id": "T1", "repo": "app", "depends_on": [], "covers": ["R1.1"], "test": ["node", "--test", "test/t1.test.mjs"] }
            ```

            #### Change
            Do T1.

            #### Tests
            Test T1.
            """);
        TestGitFixture.Git(copyRoot, "add", ".");
        TestGitFixture.Git(copyRoot, "commit", "-q", "-m", $"docs(shop): accept plan {changeName}", "-m", "Generated-By: hoshin");

        // Seed a stale passing verdict for T1 in the local ledger WITHOUT any committed build/app.md!
        var masterRepo = TestServices.Hooks.HookRunResolver.ResolveAt(copyRoot, changeFolder)!;
        var appRepo = TestServices.Default.TaskRepoResolver.ResolveByName(masterRepo, "app");
        appRepo.UpdateLedger(l => l with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "deadbeef", true, "observed", [], TestServices.Time.GetUtcNow())],
        });

        // Logs win: T1 is NOT logged, so /hoshin:build issues T1 AND reports the disagreement once.
        var (firstOut, _) = TestServices.HookOrchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = "disagree-s1",
                cwd = appDir,
                command_name = "hoshin:build",
                command_args = "",
                prompt = "/hoshin:build",
            })),
            rolesRoot: null);

        Assert.NotNull(firstOut);
        using var doc1 = JsonDocument.Parse(firstOut!);
        var ctx1 = doc1.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("# Task T1:", ctx1, StringComparison.Ordinal);
        Assert.Contains("local ledger and committed build logs disagree", ctx1, StringComparison.OrdinalIgnoreCase);

        // Second start with the same disagreement must NOT repeat the disagreement notice.
        var (secondOut, _) = TestServices.HookOrchestrator.HandleStart(
            TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
            {
                hook_event_name = "UserPromptExpansion",
                session_id = "disagree-s1",
                cwd = appDir,
                command_name = "hoshin:build",
                command_args = "",
                prompt = "/hoshin:build",
            })),
            rolesRoot: null);

        Assert.NotNull(secondOut);
        using var doc2 = JsonDocument.Parse(secondOut!);
        var ctx2 = doc2.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
        Assert.Contains("# Task T1:", ctx2, StringComparison.Ordinal);
        Assert.DoesNotContain("local ledger and committed build logs disagree", ctx2, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkflowProgress_DescribeDisagreements_ReportsLoggedTaskWithFailedLedgerVerdict()
    {
        var doc = new WorkflowDocState("plan.md", true, true, false, "accepted", "accepted");
        var plan = new WorkflowPlan("c1", "shop", "main", [new WorkflowTask("T1", "Task 1", "Do T1", ["node", "--test"], Repo: "app")], []);
        var emptyDocs = new DocumentsState(
            new DocumentState(true, true, "accepted", "accepted", false),
            new PinnedDocumentState(true, true, "accepted", "accepted", false, "holds"),
            new PinnedDocumentState(true, true, "accepted", "accepted", false, "holds"));
        var ready = new ReadinessMap(
            new SkillReadiness(false, null, []),
            new SkillReadiness(false, null, []),
            new SkillReadiness(false, null, []),
            new SkillReadiness(true, null, []),
            new SkillReadiness(false, null, []),
            new SkillReadiness(false, null, []));
        var tasks = new Dictionary<string, TaskWorkState>(StringComparer.Ordinal)
        {
            ["app"] = new(["T1"], ["T1"], [], null, null, null),
        };
        var item = new ChangeStateItem("c1", "master", "folder", false, "Title", "intent", emptyDocs, 1, tasks, null, new(), new(0, "none"), null, null, ready, null);
        var ledger = Hoshin.Workflow.Events.LedgerStore.Empty() with
        {
            Verdicts = [new WorkflowVerdict("T1", 1, "abc", false, "not observed", ["failed"], DateTimeOffset.UtcNow)],
        };
        var state = new WorkflowState("master", "folder", plan, "abc", ledger, null, doc, doc, doc, ChangeState: item);

        var disagreements = WorkflowProgress.DescribeDisagreements(state);
        var msg = Assert.Single(disagreements);
        Assert.Contains("T1 is logged in committed build logs", msg, StringComparison.Ordinal);
    }
}
