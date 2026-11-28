using Hoshin.Agents;
using Hoshin.Workflow.Driver;
using Xunit;

namespace Hoshin.Tests.Unit.Driver;

public sealed class ScopedWorkspaceTests
{
    private static ScopedWorkspace Implementing(string root) => new(
        new WorkScope(root, ["**/*"], [".git/**/*", ".hoshin/**/*", ".hoshin.json", "intent/shop/c/**/*"], [["node", "--version"]]),
        TestServices.AllowListedRunner,
        TimeSpan.FromSeconds(30));

    private static ScopedWorkspace Drafting(string root) => new(
        new WorkScope(root, ["intent/shop/c/plan.md"], [".git/**/*"], []),
        TestServices.AllowListedRunner,
        TimeSpan.FromSeconds(30));

    [Fact]
    public void WriteFile_WritesAFileTheScopeAllows()
    {
        using var tmp = new TempDir("hoshin-scope-");

        var result = Implementing(tmp.Path).WriteFile("src/a.mjs", "export {};\n");

        Assert.Equal("wrote src/a.mjs", result);
        Assert.Equal("export {};\n", File.ReadAllText(Path.Combine(tmp.Path, "src", "a.mjs")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    [InlineData(".hoshin.json")]
    [InlineData(".hoshin/run.json")]
    [InlineData("intent/shop/c/plan.md")]
    public void WriteFile_RefusesProtectedOrOutsidePaths_DuringImplement(string path)
    {
        using var tmp = new TempDir("hoshin-scope-");

        var result = Implementing(tmp.Path).WriteFile(path, "x");

        Assert.StartsWith("refused:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteFile_AllowsOnlyTheDocument_WhileDrafting()
    {
        using var tmp = new TempDir("hoshin-scope-");
        var workspace = Drafting(tmp.Path);

        Assert.Equal("wrote intent/shop/c/plan.md", workspace.WriteFile("intent/shop/c/plan.md", "# Plan\n"));
        Assert.StartsWith("refused:", workspace.WriteFile("src/a.mjs", "x"), StringComparison.Ordinal);
    }

    [Fact]
    public void RunCommand_RunsOnlyTheExactAllowedCommand()
    {
        using var tmp = new TempDir("hoshin-scope-");
        var workspace = Implementing(tmp.Path);

        Assert.StartsWith("exit 0", workspace.RunCommand(["node", "--version"]), StringComparison.Ordinal);
        Assert.StartsWith("refused:", workspace.RunCommand(["git", "commit", "-m", "x"]), StringComparison.Ordinal);
        Assert.StartsWith("refused:", workspace.RunCommand(["node", "--version", "--eval", "1"]), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_RefusesGitInternals()
    {
        using var tmp = new TempDir("hoshin-scope-");
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".git"));
        File.WriteAllText(Path.Combine(tmp.Path, ".git", "config"), "[core]\n");

        Assert.StartsWith("refused:", Implementing(tmp.Path).ReadFile(".git/config"), StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_IncludeRunCommandOnlyWhenTheWorkMayRunSomething()
    {
        using var tmp = new TempDir("hoshin-scope-");

        var drafting = ScopedTools.For(Drafting(tmp.Path)).Select(t => t.Name).ToList();
        var implementing = ScopedTools.For(Implementing(tmp.Path), (_, _) => Task.FromResult("yes")).Select(t => t.Name).ToList();

        Assert.DoesNotContain(ScopedTools.RunCommand, drafting);
        Assert.DoesNotContain(ScopedTools.AskPerson, drafting);
        Assert.Contains(ScopedTools.RunCommand, implementing);
        Assert.Contains(ScopedTools.AskPerson, implementing);
    }

    [Fact]
    public void ListFiles_ListsRelativePaths_AndSkipsGitInternals()
    {
        using var tmp = new TempDir("hoshin-scope-");
        Directory.CreateDirectory(Path.Combine(tmp.Path, ".git"));
        File.WriteAllText(Path.Combine(tmp.Path, ".git", "HEAD"), "ref\n");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "src"));
        File.WriteAllText(Path.Combine(tmp.Path, "src", "a.mjs"), "");
        File.WriteAllText(Path.Combine(tmp.Path, "b.txt"), "");

        var listed = Implementing(tmp.Path).ListFiles(".").Split('\n');

        Assert.Equal(["b.txt", "src/a.mjs"], listed);
    }

    [Fact]
    public void ListFiles_RefusesADirectoryThatIsNotThere()
    {
        using var tmp = new TempDir("hoshin-scope-");

        Assert.StartsWith("refused:", Implementing(tmp.Path).ListFiles("missing"), StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteFile_DeletesAFileTheScopeAllows()
    {
        using var tmp = new TempDir("hoshin-scope-");
        var workspace = Implementing(tmp.Path);
        workspace.WriteFile("src/a.mjs", "x");

        Assert.Equal("deleted src/a.mjs", workspace.DeleteFile("src/a.mjs"));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "src", "a.mjs")));
    }

    [Fact]
    public void DeleteFile_RefusesMissingAndProtectedFiles()
    {
        using var tmp = new TempDir("hoshin-scope-");
        File.WriteAllText(Path.Combine(tmp.Path, ".hoshin.json"), "{}");
        var workspace = Implementing(tmp.Path);

        Assert.StartsWith("refused:", workspace.DeleteFile("src/missing.mjs"), StringComparison.Ordinal);
        Assert.StartsWith("refused:", workspace.DeleteFile(".hoshin.json"), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(tmp.Path, ".hoshin.json")));
    }

    [Theory]
    [InlineData("missing.txt")]
    [InlineData("../outside.txt")]
    [InlineData("")]
    public void ReadFile_RefusesMissingOrOutsidePaths(string path)
    {
        using var tmp = new TempDir("hoshin-scope-");

        Assert.StartsWith("refused:", Implementing(tmp.Path).ReadFile(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_ReadsAFileInTheWorkingDirectory()
    {
        using var tmp = new TempDir("hoshin-scope-");
        File.WriteAllText(Path.Combine(tmp.Path, "notes.md"), "hello");

        Assert.Equal("hello", Drafting(tmp.Path).ReadFile("notes.md"));
    }

    [Fact]
    public void MayWrite_MatchesTheScope()
    {
        using var tmp = new TempDir("hoshin-scope-");
        var workspace = Drafting(tmp.Path);

        Assert.True(workspace.MayWrite("intent/shop/c/plan.md"));
        Assert.False(workspace.MayWrite("intent/shop/c/spec.md"));
    }
}
