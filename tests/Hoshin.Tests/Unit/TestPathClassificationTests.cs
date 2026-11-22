using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class TestPathClassificationTests
{
    [Theory]
    [InlineData("src/a.test.mjs", true)]
    [InlineData("src/App.csproj", false)]
    [InlineData("tests/x/y.cs", true)]
    [InlineData("foo/tests/z.cs", true)]
    [InlineData("latest/x.cs", false)]
    [InlineData("src/FooTests.cs", true)]
    [InlineData("src/FooTest.cs", true)]
    [InlineData("src/math.mjs", false)]
    public void IsTestPath_WithDefaultPatterns_ClassifiesExpectedPaths(string relativePath, bool expected)
    {
        Assert.Equal(expected, WorkflowRepo.IsTestPath(relativePath));
    }

    [Theory]
    [InlineData("barfoo", "**/foo", false)]
    [InlineData("a/barfoo", "**/foo", false)]
    [InlineData("foo", "**/foo", true)]
    [InlineData("a/foo", "**/foo", true)]
    [InlineData("a/b/foo", "**/foo", true)]
    [InlineData("a/b", "a/**/b", true)]
    [InlineData("a/x/y/b", "a/**/b", true)]
    [InlineData("a/xb", "a/**/b", false)]
    public void MatchesGlob_DoubleStarSlash_RespectsDirectoryBoundaries(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, TestPathClassifier.MatchesGlob(path, pattern));
    }

    [Fact]
    public void ExtractTestFilesFromArgv_IgnoresProjectFilesAndOutsideRepoPaths()
    {
        using var tmp = new TempDir("hoshin-t15-");
        TestGitFixture.Write(Path.Combine(tmp.Path, "src", "App", "App.csproj"), "<Project />\n");
        TestGitFixture.Write(Path.Combine(tmp.Path, ".config", "tool.json"), "{}\n");
        TestGitFixture.Write(Path.Combine(tmp.Path, "custom", "run.mjs"), "export {};\n");

        Assert.Empty(WorkflowRepo.ExtractTestFilesFromArgv(["dotnet", "test", "src/App/App.csproj"]));
        Assert.Empty(WorkflowRepo.ExtractTestFilesFromArgv(["dotnet", "test", "src/App/App.csproj"], tmp.Path));
        Assert.Empty(WorkflowRepo.ExtractTestFilesFromArgv(["node", "--test", "../outside.test.mjs"], tmp.Path));
        Assert.Empty(WorkflowRepo.ExtractTestFilesFromArgv(["tool", "--config", ".config/tool.json"], tmp.Path));

        var knownSuffix = WorkflowRepo.ExtractTestFilesFromArgv(["node", "--test", "./test/t1.test.mjs", "pkg/math_test.go", "tests/test_add.py"], tmp.Path);
        Assert.Equal(["test/t1.test.mjs", "pkg/math_test.go", "tests/test_add.py"], knownSuffix);

        var customPattern = WorkflowRepo.ExtractTestFilesFromArgv(["node", "--test", "custom/run.mjs"], tmp.Path, ["custom/**"]);
        Assert.Equal(["custom/run.mjs"], customPattern);
    }
}
