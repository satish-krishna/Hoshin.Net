using Hoshin.Tests.Acceptance.Harness;
using Xunit;

namespace Hoshin.Tests.Acceptance;

/// <summary>
/// R-new-1 acceptance gate (R3-rule 6) (docs/review/TASKS.md, round 3). Written by the reviewer, not the implementer; the implementer
/// must not edit this file or <c>Harness/</c> to make it pass, and raises a gate dispute instead.
///
/// Found by B02's smoke case on its first paid run: the eval sandbox's home folder is an empty git repository, and the
/// workspace sits inside it. A session started in the workspace folder (the folder that holds the master and the
/// product repositories, where the workspace instructions tell the engineer to work) asks git for its top level, gets
/// the enclosing repository, finds neither the master nor a system repository there, and is answered "There is no
/// change plan in this repository". An engineer who keeps dotfiles in a git repository at <c>~</c> meets the same
/// refusal on every command. A repository the workspace merely sits inside is not part of any system, so it must not
/// decide which run a session belongs to.
/// </summary>
public static class WorkspaceInsideARepositoryAcceptanceTests
{
    /// <summary>R-new-1 (control): the workspace folder with no repository around it starts an intent. Passes today; it must stay green.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class RNew1_WorkspaceFolderAlone
    {
        [Fact]
        public void IntentFromTheWorkspaceFolder_StartsTheChange() =>
            WorkspaceInsideARepositoryAcceptanceTests.IntentFromTheWorkspaceFolder(enclosing: null);
    }

    /// <summary>R-new-1: the same workspace inside an enclosing repository, empty (the eval sandbox's home) or with history (a dotfiles repository at <c>~</c>), starts the same intent.</summary>
    [Trait("Category", "Acceptance")]
    public sealed class RNew1_WorkspaceInsideARepository
    {
        [Theory]
        [InlineData("empty")]
        [InlineData("with history")]
        public void IntentFromTheWorkspaceFolder_StartsTheChange(string enclosing) =>
            WorkspaceInsideARepositoryAcceptanceTests.IntentFromTheWorkspaceFolder(enclosing);
    }

    private static void IntentFromTheWorkspaceFolder(string? enclosing)
    {
        using var ws = AcceptanceWorkspace.Create(WorkspaceSpec.Shop(ChangeScripts.ShoutGreeting()));
        if (enclosing is not null)
        {
            var home = Path.GetDirectoryName(ws.Root)!;
            ws.Git(home, "init", "-q", "-b", "main");
            if (enclosing == "with history")
            {
                File.WriteAllText(Path.Combine(home, ".bashrc"), "export EDITOR=vi\n");
                ws.Git(home, "add", ".bashrc");
                ws.Git(home, "-c", "user.name=Home", "-c", "user.email=home@example.com", "commit", "-q", "-m", "dotfiles");
            }
        }

        var po = ws.Session("po-session", ws.Root);
        var brief = po.Prompt("intent", ws.Script.Slug);
        var where = enclosing is null ? "the workspace folder" : $"a workspace folder inside a repository ({enclosing})";
        Assert.True(
            brief is not null && !brief.Contains("no change plan", StringComparison.OrdinalIgnoreCase),
            po.Transcript($"/hoshin:intent typed in {where} must start the change, but the session was told: {brief ?? "(nothing)"}"));

        po.Turn(brief);
        var intent = po.LastDocument;
        Assert.True(intent?.FileName == "intent.md", po.Transcript($"/hoshin:intent in {where} must ask for intent.md to be written"));
        ws.AssertCommitted(intent!, po.Transcript("the intent draft is committed when the turn ends"));
        Assert.True(ws.ChangeBranches(ws.Master).Length == 1, po.Transcript($"the change must be the master's, got: {string.Join(", ", ws.ChangeBranches(ws.Master))}"));
    }
}
