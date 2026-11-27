using System.Text.Json;
using Hoshin.Tests.Acceptance.Harness;
using Xunit;

namespace Hoshin.Tests.Acceptance.Parity;

/// <summary>
/// Steps and workspaces the lifecycle parity tests share, built over the public harness API. Every state is reached
/// through the shipped hooks or the CLI (<c>commit --kind work</c>, <c>state</c>, <c>red show</c>); nothing here writes
/// git history the engine would write.
/// </summary>
internal static class LifecycleParityHarness
{
    /// <summary>T1 built in api and T2 in web, each by the repository's own session, as the engineer does.</summary>
    public static (Session Api, Session Web) BuildApiAndWeb(this AcceptanceWorkspace ws)
    {
        var apiDev = ws.Session("api-session", ws.Api);
        apiDev.Turn(apiDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Api, "T1", apiDev);
        var webDev = ws.Session("web-session", ws.Web);
        webDev.Turn(webDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Web, "T2", webDev);
        return (apiDev, webDev);
    }

    /// <summary>The master session writes review <paramref name="number"/> unless it is already committed.</summary>
    public static void ReviewUnlessAlreadyCommitted(this AcceptanceWorkspace ws, Session master, string changeFolder, int number)
    {
        if (!ws.ReviewCommitted(changeFolder, number))
        {
            master.Turn(master.Prompt("review"));
        }

        Assert.True(ws.ReviewCommitted(changeFolder, number), master.Transcript($"review {number} must be written and committed"));
    }

    /// <summary>The subjects of the commits in <paramref name="range"/> of <paramref name="repo"/>, oldest first.</summary>
    public static string[] Subjects(this AcceptanceWorkspace ws, string repo, string range) =>
        ws.Git(repo, "log", "--reverse", "--format=%s", range).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The paths a commit changed, in git's order.</summary>
    public static string[] FilesOf(this AcceptanceWorkspace ws, string repo, string revision) =>
        ws.Git(repo, "show", "--name-only", "--format=", revision).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Mentions(string text, string path) =>
        text.Contains(path, StringComparison.OrdinalIgnoreCase)
        || text.Contains(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Commits work the way the engine's own task commits are made: <c>hoshin commit --kind work</c> from inside the
    /// repository, with the task's <c>Plan-Task</c> trailer, naming only <paramref name="paths"/>.
    /// </summary>
    public static string CommitTaskWork(this AcceptanceWorkspace ws, string repoName, string change, string task, string type, string subject, params string[] paths)
    {
        var args = new List<string> { "commit", "--json", "--kind", "work", "--change", change, "--repo", repoName, "--task", task, "--type", type, "--subject", subject, "--" };
        args.AddRange(paths);
        var result = ws.RunCli(ws.Repo(repoName), [.. args]);
        Assert.True(result.ExitCode == 0, $"`hoshin commit --kind work` for {task} in {repoName} must succeed:\n{result.Stdout}\n{result.Stderr}");
        return ws.Head(ws.Repo(repoName));
    }

    /// <summary>One change as <c>hoshin state --json</c> reports it, run from <paramref name="cwd"/>.</summary>
    public static JsonElement ChangeState(this AcceptanceWorkspace ws, string cwd, string change)
    {
        var result = ws.RunCli(cwd, "state", "--json", "--change", change);
        Assert.True(result.ExitCode == 0, $"`hoshin state --json --change {change}` must work from {cwd}:\n{result.Stdout}\n{result.Stderr}");
        using var doc = JsonDocument.Parse(result.Stdout);
        return doc.RootElement.GetProperty("changes")[0].Clone();
    }

    /// <summary>Whether the working tree of <paramref name="repo"/> has any uncommitted or untracked path.</summary>
    public static bool IsClean(this AcceptanceWorkspace ws, string repo) => ws.Git(repo, "status", "--porcelain").Length == 0;

    // ---------------------------------------------------------------- scripts and workspaces

    private static ChangeScript Script(string slug, string title, string requirements, params PlanTask[] tasks) => new()
    {
        Slug = slug,
        Title = title,
        Problem = "Callers cannot do what the change adds.",
        Outcome = "The change adds it.",
        Users = "Callers.",
        Constraints = "Existing behavior is unchanged.",
        Decisions = "D1: the change adds it.",
        Requirements = requirements,
        Tasks = tasks,
    };

    private static string Requirement(int n) => $$"""
          @R{{n}} @from:D1
          Rule: Behavior {{n}} works
            @R{{n}}.1
            Scenario: Behavior {{n}} at work
              Given the input {{n}}
              When behavior {{n}} runs
              Then the result is {{n}}
        """;

    private static PlanTask Waiting(string id, string repo, int scenario, params string[] dependsOn) =>
        new(id, repo, $"Behavior {scenario} in {repo}", $"Add behavior {scenario} to {repo}.", $"`test/b{scenario}.test.mjs` checks behavior {scenario}.", ["node", "--test", $"test/b{scenario}.test.mjs"], _ => { })
        {
            DependsOn = dependsOn,
            Covers = [$"R{scenario}.1"],
        };

    /// <summary>
    /// The original's schedule workspace: T1 in <c>core</c> (the session's repository), T2 in api, T3 then T4 in web
    /// (T3 needs T1, T4 needs T2) and T5 in docs (needs T2). Once T1 is logged, the schedule says api and web are
    /// ready and docs waits for T2; a reading of every <c>depends_on</c> would make web wait for T2 as well.
    /// </summary>
    public static WorkspaceSpec ScheduleWorkspace()
    {
        var requirements = "Feature: Schedule\n" + string.Join("\n\n", Enumerable.Range(1, 5).Select(Requirement));
        var script = Script(
            "schedule-check",
            "Schedule check",
            requirements,
            new PlanTask(
                "T1",
                "core",
                "Shout in core",
                "Add `shout(name)` to `src/greet.mjs`.",
                "`test/shout.test.mjs` checks `shout('Ada') === 'Hello, Ada!'`.",
                ["node", "node_modules/testkit/run.mjs", "test/shout.test.mjs"],
                ChangeScripts.ShoutInWeb)
            {
                Covers = ["R1.1"],
            },
            Waiting("T2", "api", 2),
            Waiting("T3", "web", 3, "T1"),
            Waiting("T4", "web", 4, "T2"),
            Waiting("T5", "docs", 5, "T2"));
        var core = new RepoSpec("core", ShopRepos.Web.Files, (_, path) => ShopRepos.InstallTestkit(path));
        return new WorkspaceSpec(script, [core, Plain("api"), Plain("web"), Plain("docs")]);
    }

    private static RepoSpec Plain(string name) =>
        new(name, new Dictionary<string, string> { ["package.json"] = $"{{ \"name\": \"{name}\", \"type\": \"module\", \"private\": true }}\n" });

    /// <summary>A change whose every task names <c>legacy-portal</c>, a repository the system does not list, so the plan check can never pass.</summary>
    public static ChangeScript PlanInAnUnlistedRepository() => Script(
        "legacy-portal-greeting",
        "Greeting in the legacy portal",
        "Feature: Legacy portal\n" + Requirement(1),
        Waiting("T1", "legacy-portal", 1));

    /// <summary>A change whose only task covers a scenario the spec does not have, so the plan check, which exists today, can never pass.</summary>
    public static ChangeScript PlanCoveringAnUnknownScenario() => Script(
        "unknown-scenario",
        "Plan covering an unknown scenario",
        "Feature: Unknown scenario\n" + Requirement(1),
        Waiting("T1", "web", 9));

    /// <summary>The bug of the original's start case, in web: the greeting has no space after the comma.</summary>
    public static ChangeScript GreetingSpaceBug() => new()
    {
        Slug = "greeting-space",
        Title = "Greeting has a space after the comma",
        Problem = "`greet('Mina')` returns `Hello,Mina`: the space after the comma is missing. Root cause: `src/greet.mjs:1` joins the parts without one.",
        Outcome = "The greeting has a space after the comma.",
        Users = "Visitors.",
        Constraints = "Other greetings are unchanged.",
        Decisions = "None.",
        Requirements = """
            Feature: Greeting has a space after the comma
              @R1 @from:problem
              Rule: The greeting has a space after the comma
                @R1.1
                Scenario: Greeting Mina
                  Given the name "Mina"
                  When the visitor is greeted
                  Then the result is "Hello, Mina"
            """,
        Tasks =
        [
            new PlanTask(
                "T1",
                "web",
                "Add the space",
                "Join the greeting with a space in `src/greet.mjs`.",
                "`test/greet-space.test.mjs` checks `greet('Mina') === 'Hello, Mina'`.",
                ["node", "node_modules/testkit/run.mjs", "test/greet-space.test.mjs"],
                root =>
                {
                    ChangeScripts.Write(root, "test/greet-space.test.mjs", """
                        import test from 'node:test';
                        import assert from 'node:assert/strict';
                        import { greet } from '../src/greet.mjs';
                        test('greet has a space', () => { assert.equal(greet('Mina'), 'Hello, Mina'); });
                        """);
                    ChangeScripts.Write(root, "src/greet.mjs", "export const greet = (name) => `Hello, ${name}`;\n");
                })
            {
                Covers = ["R1.1"],
            },
        ],
        FixRepo = "web",
    };
}
