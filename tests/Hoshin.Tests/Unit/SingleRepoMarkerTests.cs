using System.Text.Json;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class SingleRepoMarkerTests
{
    private static List<string> ProblemsOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return MarkerReader.MarkerProblems(doc.RootElement);
    }

    private static Marker ReadFromJson(TempDir dir, string json)
    {
        var path = Path.Combine(dir.Path, $"marker-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return TestServices.MarkerReader.ReadMarker(path);
    }

    private static void Mentions(List<string> problems, string text) =>
        Assert.Contains(problems, p => p.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void MarkerWithoutLayout_ReadsAsWorkspaceWithNoApps()
    {
        using var dir = new TempDir("hoshin-marker-");
        var m = ReadFromJson(dir, """{"version":1,"systems":{"demo":{"repos":["app"]}}}""");
        Assert.Equal(WorkspaceLayout.Workspace, m.Layout);
        Assert.Equal(["app"], m.Systems["demo"].Repos);
        Assert.Empty(m.Systems["demo"].Apps);
    }

    [Fact]
    public void SingleRepoMarker_NormalizesAppFolders()
    {
        using var dir = new TempDir("hoshin-marker-");
        var m = ReadFromJson(dir, """{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"api":"apps/api/","web":"apps//web","ui":"./libs/ui"}}}}""");
        Assert.Equal(WorkspaceLayout.SingleRepo, m.Layout);
        Assert.Equal(new Dictionary<string, string> { ["api"] = "apps/api", ["web"] = "apps/web", ["ui"] = "libs/ui" }, m.Systems["demo"].Apps);
        Assert.Empty(m.Systems["demo"].Repos);
    }

    [Fact]
    public void ValidMarkers_HaveNoProblems()
    {
        Assert.Empty(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"api":"apps/api","web":"apps/web"}}}}"""));
        Assert.Empty(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"shop":"."}}}}"""));
        Assert.Empty(ProblemsOf("""{"version":1,"systems":{"demo":{"repos":["app"]}}}"""));
    }

    [Fact]
    public void InvalidLayoutsVersionsFieldsAndApps_ProduceExactProblems()
    {
        Mentions(ProblemsOf("""{"version":1,"layout":"monorepo","systems":{"demo":{"repos":["app"]}}}"""), "layout");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"a":{"apps":{"x":"x"}},"b":{"apps":{"y":"y"}}}}"""), "exactly one system");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"api":"apps/api"},"repos":["app"]}}}"""), "repos");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{}}}"""), "apps");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{}}}}"""), "apps");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"master":"apps/m"}}}}"""), "master");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"a b":"apps/a"}}}}"""), "a b");

        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"x":"..\\x"}}}}"""), "x");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"y":"../y"}}}}"""), "y");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"z":"/abs"}}}}"""), "z");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"w":"C:/abs"}}}}"""), "w");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"e":""}}}}"""), "e");
        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"n":3}}}}"""), "n");

        var nested = ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"top":"apps","api":"apps/api"}}}}""");
        Mentions(nested, "api");
        Mentions(nested, "top");

        Mentions(ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"all":".","api":"apps/api"}}}}"""), "all");
        Mentions(ProblemsOf("""{"version":1,"systems":{"demo":{"repos":["app"],"apps":{"api":"apps/api"}}}}"""), "apps");
        Mentions(ProblemsOf("""{"version":1,"layout":"workspace","systems":{"demo":{"repos":["app"],"apps":{"api":"apps/api"}}}}"""), "apps");

        Mentions(ProblemsOf("""{"systems":{"demo":{"repos":["app"]}}}"""), "version must be 1, not missing");
        Mentions(ProblemsOf("""{"version":2,"systems":{"demo":{"repos":["app"]}}}"""), "version must be 1, not 2");
        Mentions(ProblemsOf("""{"version":"1","systems":{"demo":{"repos":["app"]}}}"""), "version must be 1, not \"1\"");

        var unknown = ProblemsOf("""{"version":1,"sytems":{},"systems":{"demo":{"repos":["app"],"comitScope":"x"}}}""");
        Mentions(unknown, "unknown field sytems");
        Mentions(unknown, "system demo: unknown field comitScope");
        Assert.Empty(ProblemsOf("""{"version":1,"layout":"workspace","hooks":{},"gateRetries":3,"systems":{"demo":{"repos":["app"],"commitScope":"shop-api","naming":{}}}}"""));

        Mentions(ProblemsOf("""{"version":1,"systems":{"demo":["app"]}}"""), "system demo: must be an object");
        Assert.Equal(["system demo: must be an object"], ProblemsOf("""{"version":1,"systems":{"demo":null}}"""));
        Assert.Equal(["system demo: must be an object"], ProblemsOf("""{"version":1,"layout":"single-repo","systems":{"demo":"x"}}"""));

        foreach (var scopeJson in new[] { "\"Shop\"", "\"shop_api\"", "\"shop-\"", "\"\"", "3" })
        {
            Mentions(
                ProblemsOf("{\"version\":1,\"systems\":{\"demo\":{\"repos\":[\"app\"],\"commitScope\":" + scopeJson + "}}}"),
                $"commitScope must be lowercase kebab case, like \"billing-api\", not {scopeJson}");
        }

        Assert.Equal(
            ["unknown field extra", "layout must be \"workspace\" or \"single-repo\", not \"monorepo\""],
            ProblemsOf("""{"version":1,"layout":"monorepo","extra":true,"systems":{"demo":{"repos":["app"]}}}"""));
    }

    [Fact]
    public void ReadMarker_DoesNotThrowOnSingleRepoProblems()
    {
        using var dir = new TempDir("hoshin-marker-");
        var m = ReadFromJson(dir, """{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"master":"../x"}}}}""");
        Assert.Equal(WorkspaceLayout.SingleRepo, m.Layout);
    }

    [Fact]
    public void CheckCommand_ValidatesSingleRepoMarkerFoldersAndProblems()
    {
        using var s = TestGitFixture.CreateSingleRepo();
        var ok = TestGitFixture.RunCli(s.Repo, "check", ".hoshin.json");
        Assert.Equal(0, ok.ExitCode);

        File.WriteAllText(Path.Combine(s.Repo, ".hoshin.json"), """{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"api":"apps/api","web":"apps/gone"}}}}""");
        var missing = TestGitFixture.RunCli(s.Repo, "check", ".hoshin.json");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains(".hoshin.json:1:", missing.Stdout);
        Assert.Contains("web", missing.Stdout);
        Assert.Contains("apps/gone", missing.Stdout);
        Assert.DoesNotContain("api", missing.Stdout);

        File.WriteAllText(Path.Combine(s.Repo, ".hoshin.json"), """{"version":1,"layout":"single-repo","systems":{"demo":{"apps":{"master":"apps/api"}}}}""");
        var reserved = TestGitFixture.RunCli(s.Repo, "check", ".hoshin.json");
        Assert.Equal(1, reserved.ExitCode);
        Assert.Contains("master", reserved.Stdout);
    }
}
