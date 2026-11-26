using System.Text.Json;
using Hoshin.Tests.Acceptance.Harness;
using Xunit;

namespace Hoshin.Tests.Acceptance;

/// <summary>
/// R0 acceptance gate (docs/review/ANALYSIS.md §12.2). Written by the reviewer, not the implementer; the implementer
/// must not edit this file to make it pass. Changes to it go through the reviewer.
///
/// It drives the shipped CLI (<c>dotnet hoshin.dll</c>) with Claude Code hook payloads, exactly as the harness does,
/// through one whole change in a realistic workspace, and judges only what an engineer can see: commits, branches,
/// remote refs, committed documents, pull requests, and whether the session is allowed to end. It never reads
/// <c>.git/hoshin</c>, so it does not care how the fix is designed.
///
/// The user path is the original hoshin plugin's (D:\workspaces\hoshin):
/// - the master repository is not named <c>master</c> (here <c>ai-sdlc</c>, as in a real workspace);
/// - intent, spec and plan are written and accepted from a session in the master repository;
/// - each repository is built from a session started inside it (hoshin <c>skills/build/SKILL.md</c> lines 25 and 28),
///   so that repository's own CLAUDE.md, skills and hooks are loaded; a session never builds another repository's tasks;
/// - review findings are fixed by a build run from inside the repository, with the engineer's instructions
///   (hoshin <c>skills/review/SKILL.md</c> line 111), and then reviewed again;
/// - only the engineer approves, and approving publishes: every touched repository is pushed and gets a pull
///   request, the master's last; after that the change stays done.
///
/// The plan has one task whose test runs against compiled output (<c>compile</c>, output gitignored) and one whose test
/// runs a gitignored tool (<c>node_modules</c>), because real repositories look like that. The workspace, the compliant
/// model and the session live in <c>Harness/</c>, shared with the round-3 tests, and belong to the reviewer too.
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class WorkspaceLifecycleAcceptanceTests
{
    [Fact]
    public void WholeChange_FromIntentToMergedReadyPullRequests_InAWorkspaceWhoseMasterIsNotNamedMaster()
    {
        using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
        var po = ws.Session("po-session", ws.Master);

        // 1. Authoring, from the master repository, by the product owner and the engineer.
        var plan = ws.AuthorAndAcceptAll(po);
        BuildReviewFixAndPublish(ws, po, plan);
    }

    /// <summary>Stages 2 to 9: from an accepted plan to published pull requests, and done stays done.</summary>
    private static void BuildReviewFixAndPublish(AcceptanceWorkspace ws, Session po, DocumentRef plan)
    {
        var changeFolder = plan.Folder;

        // 2. Build api from a session started inside api: T1 only (its test needs `compile` output, gitignored).
        var apiDev = ws.Session("api-session", ws.Api);
        apiDev.Turn(apiDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Api, "T1", apiDev);
        ws.AssertNotCommitted(ws.Api, "dist/", apiDev);

        // 3. Build web from a session started inside web: T2 only (its test runs a gitignored tool).
        var webDev = ws.Session("web-session", ws.Web);
        webDev.Turn(webDev.Prompt("build"));
        ws.AssertTaskCommitted(ws.Web, "T2", webDev);
        ws.AssertNotCommitted(ws.Web, "node_modules/", webDev);

        // Both tasks are logged in the change folder, which is how every later step knows they are done.
        ws.AssertBuildLogHas(changeFolder, "api", "T1", webDev);
        ws.AssertBuildLogHas(changeFolder, "web", "T2", webDev);

        // 4. Review 1, from the master repository unless the build already asked for it. It finds one high bug in web.
        if (!ws.ReviewCommitted(changeFolder, 1))
        {
            po.Turn(po.Prompt("review"));
        }

        Assert.True(ws.ReviewCommitted(changeFolder, 1), po.Transcript("review 1 must be written and committed"));

        // 5. The high finding is fixed by a build run from inside web, with the engineer's instructions.
        ws.FixRequested = true;
        var headBeforeFix = ws.Head(ws.Web);
        webDev.Turn(webDev.Prompt("build", "fix F1 from review 1: shout must trim the name before shouting"));
        Assert.True(ws.FixApplied, webDev.Transcript("a build run in web with fix instructions must dispatch the fix"));
        Assert.NotEqual(headBeforeFix, ws.Head(ws.Web));
        Assert.Contains("shout-trim.test.mjs", ws.Git(ws.Web, "log", "--name-only", "--format=", $"{headBeforeFix}..HEAD"));

        // 6. Review 2 of the fixed change, from the master repository unless the fix run already asked for it. Clean.
        if (!ws.ReviewCommitted(changeFolder, 2))
        {
            po.Turn(po.Prompt("review"));
        }

        Assert.True(ws.ReviewCommitted(changeFolder, 2), po.Transcript("review 2 must be written and committed after the fix"));

        // 7. Approval is the engineer's: a model-typed accept must not publish anything.
        po.Turn(po.ModelInvokedSkill("accept"));
        Assert.Empty(ws.PullRequests());
        Assert.False(ws.RemoteHasChangeBranch(ws.Web), po.Transcript("a model-invoked accept must not publish"));

        // 8. The engineer approves; approving publishes.
        po.Turn(po.Prompt("accept"));
        var prs = ws.PullRequests();
        Assert.True(prs.Count == 3, po.Transcript($"expected one pull request per touched repository (api, web, master), got {prs.Count}: {string.Join(", ", prs.Select(p => p.Repo))}"));
        Assert.Equal(["api", "web"], prs.Take(2).Select(p => p.Repo).Order(StringComparer.Ordinal));
        Assert.NotEqual("api", prs[2].Repo);
        Assert.NotEqual("web", prs[2].Repo);
        Assert.Equal(ws.Head(ws.Api), ws.RemoteHead(ws.Api));
        Assert.Equal(ws.Head(ws.Web), ws.RemoteHead(ws.Web));
        var masterCopy = plan.Root;
        Assert.Equal(ws.Head(masterCopy), ws.RemoteHead(masterCopy));

        // 9. Done stays done: nothing re-runs checks, review or approval, nothing is published again, and no session is held.
        var headsAfterPublish = (Api: ws.Head(ws.Api), Web: ws.Head(ws.Web), Master: ws.Head(masterCopy));
        po.AssertTurnEndsQuietly("after publishing, the master session must be allowed to end");
        webDev.AssertTurnEndsQuietly("after publishing, the web session must be allowed to end");
        apiDev.AssertTurnEndsQuietly("after publishing, the api session must be allowed to end");
        webDev.Turn(webDev.Prompt("build"));
        Assert.Equal(headsAfterPublish, (ws.Head(ws.Api), ws.Head(ws.Web), ws.Head(masterCopy)));
        Assert.Equal(3, ws.PullRequests().Count);
        Assert.False(ws.ReviewCommitted(changeFolder, 3), webDev.Transcript("a published change must not ask for another review"));
    }

    [Fact]
    public void InterruptedBuild_IsResumedByTheEngineer_AndNeverTrapsAnotherSession()
    {
        using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
        var po = ws.Session("po-session", ws.Master);
        ws.AuthorAndAcceptAll(po);

        // The engineer starts the web build, then the model ends its turn again and again without dispatching.
        var first = ws.Session("web-first", ws.Web);
        var brief = first.Context(first.Hook("hook start", first.PromptPayload("build", "")));
        Assert.True(brief is not null && brief.Contains(AcceptanceWorkspace.BeginMarker, StringComparison.Ordinal), first.Transcript("/hoshin:build in web must dispatch T2"));
        for (var i = 0; i < 6; i++)
        {
            first.Hook("hook stop", first.StopPayload(stopHookActive: i > 0));
        }

        // Another session in the same repository, unrelated to the build, must be able to end its turn.
        var other = ws.Session("web-unrelated", ws.Web);
        other.AssertTurnEndsQuietly("an unrelated session must never be blocked by a parked build");

        // The engineer comes back in a new session and resumes: T2 is dispatched again and completes.
        var resumed = ws.Session("web-resumed", ws.Web);
        resumed.Turn(resumed.Prompt("build"));
        ws.AssertTaskCommitted(ws.Web, "T2", resumed);
        resumed.AssertTurnEndsQuietly("after T2, the resumed web session must be allowed to end");
    }

    [Fact]
    public void ActiveBuild_DeniesPushesThroughEveryShellTool_TheHarnessOffers()
    {
        using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
        var po = ws.Session("po-session", ws.Master);
        ws.AuthorAndAcceptAll(po);

        var webDev = ws.Session("web-session", ws.Web);
        var brief = webDev.Context(webDev.Hook("hook start", webDev.PromptPayload("build", "")));
        Assert.True(brief is not null && brief.Contains(AcceptanceWorkspace.BeginMarker, StringComparison.Ordinal), webDev.Transcript("/hoshin:build in web must dispatch T2"));

        // Claude Code on Windows offers PowerShell as a shell tool next to Bash; both must be guarded.
        foreach (var tool in new[] { "Bash", "PowerShell" })
        {
            var decision = AcceptanceWorkspace.PermissionDecision(webDev.Hook("guard", webDev.ToolPayload(tool, new { command = "git push origin HEAD:refs/heads/main" })));
            Assert.True(decision == "deny", webDev.Transcript($"`git push` through the {tool} tool during an active build must be denied, got `{decision ?? "allow"}`"));
        }

        // And the shipped hook configuration must route those tools to the guard at all.
        var hooksJson = File.ReadAllText(Path.Combine(AcceptanceWorkspace.RepoRoot, "packaging", "claude-code", "hooks", "hooks.json"));
        var matcher = JsonDocument.Parse(hooksJson).RootElement.GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray().Select(e => e.GetProperty("matcher").GetString() ?? "").Single(m => m.Contains("Bash", StringComparison.Ordinal));
        Assert.Contains("PowerShell", matcher.Split('|'));
    }
}
