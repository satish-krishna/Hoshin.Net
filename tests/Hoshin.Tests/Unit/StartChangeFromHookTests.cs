using System.Text.Json;
using Hoshin.Cli;
using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class StartChangeFromHookTests
{
    [Fact]
    public void HookStartIntent_InEmptyWorkspace_CreatesChangeJsoncAndBranchAndInjectsIntentBrief()
    {
        using var tmp = new TempDir("hoshin-a7-start-");
        var wsDir = Path.Combine(tmp.Path, "ws");
        var repoDir = Path.Combine(wsDir, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin.json"] = """
                {
                  "version": 1,
                  "layout": "single-repo",
                  "systems": { "demo": { "apps": { "app": "." } } }
                }
                """,
            ["README.md"] = "# demo\n",
        });

        var fixedTime = new FixedTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var services = new HoshinServices(
            TestServices.SchemaValidator,
            TestServices.CommandRunner,
            TestServices.Git,
            TestServices.PullRequests,
            fixedTime,
            TestServices.FileSystem);

        var startEvent = services.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "claude-a7",
            cwd = repoDir,
            command_name = "hoshin:intent",
            command_args = "add-login",
        }));

        var (output, _) = services.HookOrchestrator.HandleStart(startEvent, rolesRoot: null);
        Assert.NotNull(output);
        var context = ExtractContext(output!);

        var expectedChange = "2026-11-22-add-login";
        var expectedBranch = $"change/{expectedChange}";

        // Branch exists in git and holds intent/demo/<date>-add-login/change.jsonc.
        var branches = TestGitFixture.Git(repoDir, "branch", "--list", expectedBranch);
        Assert.Contains(expectedBranch, branches, StringComparison.Ordinal);

        var copyDir = Path.Combine(wsDir, ".hoshin", "copies", expectedChange);
        var changeJsonPath = Path.Combine(copyDir, "intent", "demo", expectedChange, "change.jsonc");
        Assert.True(File.Exists(changeJsonPath), $"Expected change.jsonc at {changeJsonPath}; context was:\n{context}");

        // Intent brief was injected and step.json is WriteDocument(intent).
        Assert.Contains($"# The intent for change {expectedChange}", context, StringComparison.Ordinal);
        Assert.Contains("intent.md", context, StringComparison.Ordinal);

        var repo = TestServices.FindRepo(repoDir)!;
        var step = repo.ReadStep();
        Assert.NotNull(step);
        Assert.Equal(StepKind.WriteDocument, step!.Kind);
        Assert.Equal("intent", step.Task);
    }

    [Fact]
    public void HookStart_WithTwoOpenChangesAndNoName_AsksWhichChangeInsteadOfPicking()
    {
        using var tmp = new TempDir("hoshin-a7-ambig-");
        var repoDir = CreateTwoOpenChangesWorkspace(tmp.Path);

        // ChangeFolderResolver itself must never pick one by sort order.
        var gitCommon = Path.Combine(repoDir, ".git");
        var resolved = ChangeFolderResolver.Resolve(repoDir, gitCommon, null, TestServices.FileSystem);
        Assert.DoesNotContain("2026-11-01-alpha", resolved, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-11-02-beta", resolved, StringComparison.Ordinal);

        var startEvent = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "claude-a7-ambig",
            cwd = repoDir,
            command_name = "hoshin:spec",
            command_args = "",
        }));

        var (output, _) = TestServices.HookOrchestrator.HandleStart(startEvent, rolesRoot: null);
        Assert.NotNull(output);
        var context = ExtractContext(output!);

        Assert.Contains("2026-11-01-alpha", context, StringComparison.Ordinal);
        Assert.Contains("2026-11-02-beta", context, StringComparison.Ordinal);
        Assert.Contains("which change", context, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(gitCommon, "hoshin", "run.json")), "Must not start a run before disambiguating.");
    }

    [Fact]
    public void HookStart_WithTwoOpenChangesAndExplicitName_SelectsNamedChange()
    {
        using var tmp = new TempDir("hoshin-a7-named-");
        var repoDir = CreateTwoOpenChangesWorkspace(tmp.Path);

        var startEvent = TestServices.HookAdapter.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = "UserPromptSubmit",
            session_id = "claude-a7-named",
            cwd = repoDir,
            command_name = "hoshin:spec",
            command_args = "2026-11-01-alpha",
        }));

        var (output, _) = TestServices.HookOrchestrator.HandleStart(startEvent, rolesRoot: null);
        Assert.NotNull(output);
        var context = ExtractContext(output!);

        Assert.Contains("2026-11-01-alpha", context, StringComparison.Ordinal);
        Assert.Contains("spec.md", context, StringComparison.Ordinal);
    }

    private static string CreateTwoOpenChangesWorkspace(string root)
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
            ["README.md"] = "# demo\n",
        });

        // Put both open changes off main so FinishedChecker sees them as open (not merged into main).
        TestGitFixture.Git(repoDir, "checkout", "-q", "-b", "work");
        TestGitFixture.Write(
            Path.Combine(repoDir, "intent", "demo", "2026-11-01-alpha", "change.jsonc"),
            """
            { "version": 1, "change": "2026-11-01-alpha", "type": "feat", "scope": "demo", "base": "main", "branch": "change/2026-11-01-alpha" }
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "intent", "demo", "2026-11-01-alpha", "intent.md"),
            """
            ```jsonc
            { "title": "Alpha", "system": "demo", "author": "t", "date": "2026-11-01", "status": "accepted", "scope": "demo" }
            ```

            # Intent: Alpha
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "intent", "demo", "2026-11-02-beta", "change.jsonc"),
            """
            { "version": 1, "change": "2026-11-02-beta", "type": "feat", "scope": "demo", "base": "main", "branch": "change/2026-11-02-beta" }
            """);
        TestGitFixture.Write(
            Path.Combine(repoDir, "intent", "demo", "2026-11-02-beta", "intent.md"),
            """
            ```jsonc
            { "title": "Beta", "system": "demo", "author": "t", "date": "2026-11-02", "status": "accepted", "scope": "demo" }
            ```

            # Intent: Beta
            """);
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", "docs(demo): add two open changes", "-m", "Generated-By: hoshin");
        return repoDir;
    }

    private static string ExtractContext(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString() ?? "";
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
