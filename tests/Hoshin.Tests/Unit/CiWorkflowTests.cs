using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class CiWorkflowTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string CiWorkflowPath = Path.Combine(RepoRoot, ".github", "workflows", "ci.yml");

    [Fact]
    public void CiWorkflow_RunsChangedLineCoverageGateWithDotnetCoverageAndDiffCover()
    {
        Assert.True(File.Exists(CiWorkflowPath), $"Missing workflow: {CiWorkflowPath}");
        var text = File.ReadAllText(CiWorkflowPath);

        Assert.Contains("src_changed", text, StringComparison.Ordinal);
        Assert.Contains("dotnet-coverage collect -f cobertura -o coverage.cobertura.xml", text, StringComparison.Ordinal);
        Assert.Contains("diff-cover coverage.cobertura.xml", text, StringComparison.Ordinal);
        Assert.Contains("--fail-under=90", text, StringComparison.Ordinal);
        Assert.Contains("--include 'src/**'", text, StringComparison.Ordinal);
        Assert.Contains("--html-report", text, StringComparison.Ordinal);
        Assert.Contains("coverage-diff.html", text, StringComparison.Ordinal);
    }
}
