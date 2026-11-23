using System.Text.RegularExpressions;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed partial class ConventionalWorkflowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string WorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "conventional.yml");

    [GeneratedRegex(@"^(feat|fix|docs|test|refactor|perf|build|ci|chore)\([a-z0-9]+(-[a-z0-9]+)*\)!?: \S.*$")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^(feat|fix|docs|test|refactor|perf|build|ci|chore)/[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex BranchRegex();

    [Fact]
    public void ConventionalWorkflow_ChecksTitleBranchEveryCommitHeaderAndProgramTaskTrailers()
    {
        Assert.True(File.Exists(WorkflowPath), $"Missing workflow: {WorkflowPath}");
        var text = File.ReadAllText(WorkflowPath);

        Assert.Contains("name: conventional names", text, StringComparison.Ordinal);
        Assert.Contains("actions/checkout@v4", text, StringComparison.Ordinal);
        Assert.Contains("fetch-depth: 0", text, StringComparison.Ordinal);
        Assert.Contains("TITLE: ${{ github.event.pull_request.title }}", text, StringComparison.Ordinal);
        Assert.Contains("BRANCH: ${{ github.head_ref }}", text, StringComparison.Ordinal);
        Assert.Contains("git log", text, StringComparison.Ordinal);
        Assert.Contains("fix/review-findings", text, StringComparison.Ordinal);
        Assert.Contains("Task:", text, StringComparison.Ordinal);
        Assert.Contains("docs/review/TASKS.md", text, StringComparison.Ordinal);

        // Untrusted PR metadata must never be interpolated directly into a run: script body.
        var runIdx = text.IndexOf("run: |", StringComparison.Ordinal);
        Assert.True(runIdx >= 0);
        var scriptBody = text[runIdx..];
        Assert.DoesNotContain("${{ github.event.pull_request.title }}", scriptBody, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ github.head_ref }}", scriptBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fix(workflow)!: address review findings", true)]
    [InlineData("ci(packaging): run packager on Linux and Windows", true)]
    [InlineData("feat(guard): block bypasses", true)]
    [InlineData("fix: missing scope", false)]
    [InlineData("update(cli): invalid type", false)]
    [InlineData("fix(CLI): uppercase scope", false)]
    public void TitlePattern_MatchesExpectedConventionalHeaders(string title, bool expected) =>
        Assert.Equal(expected, TitleRegex().IsMatch(title));

    [Theory]
    [InlineData("fix/review-findings", true)]
    [InlineData("feat/t01-guard-scope", true)]
    [InlineData("main", false)]
    [InlineData("fix/ReviewFindings", false)]
    public void BranchPattern_MatchesExpectedBranchNames(string branch, bool expected) =>
        Assert.Equal(expected, BranchRegex().IsMatch(branch));
}
