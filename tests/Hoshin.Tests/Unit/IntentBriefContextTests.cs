using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class IntentBriefContextTests
{
    [Fact]
    public void IntentBrief_StandardIntentLayout_ListsSystemContextFilesAndUsesRealSystemName()
    {
        using var tmp = new TempDir("hoshin-t30-intent-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            ["intent/demo/context/app.md"] = "# Context: app\n",
            ["intent/demo/context/worker.md"] = "# Context: worker\n",
            ["intent/demo/add-login/change.jsonc"] = """
                { "version": 1, "change": "add-login", "type": "feat", "scope": "auth-api", "base": "main" }
                """,
        });

        var repo = TestServices.FindRepo(repoDir, Path.Combine(repoDir, "intent", "demo", "add-login"))!;
        var state = TestServices.ReadState(repo)!;

        var brief = WorkflowBriefs.Intent(state);

        Assert.Contains("intent/demo/context/app.md", brief, StringComparison.Ordinal);
        Assert.Contains("intent/demo/context/worker.md", brief, StringComparison.Ordinal);
        Assert.Contains("\"system\": \"demo\"", brief, StringComparison.Ordinal);
        Assert.Contains("\"scope\": \"auth-api\"", brief, StringComparison.Ordinal);
    }

    [Fact]
    public void IntentBrief_DotHoshinLayout_StillFindsChangeFolderContextFiles()
    {
        using var tmp = new TempDir("hoshin-t30-dothoshin-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin/context-app.md"] = "# Context: app\n",
            [".hoshin/change.jsonc"] = """
                { "version": 1, "change": "add-login", "type": "feat", "scope": "auth-api", "base": "main" }
                """,
        });

        var repo = TestServices.FindRepo(repoDir)!;
        var state = TestServices.ReadState(repo)!;

        var brief = WorkflowBriefs.Intent(state);

        Assert.Contains(".hoshin/context-app.md", brief, StringComparison.Ordinal);
        Assert.Contains("\"system\": \"auth-api\"", brief, StringComparison.Ordinal);
    }
}
