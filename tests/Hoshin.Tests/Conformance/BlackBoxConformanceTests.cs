using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Conformance;

/// <summary>
/// Executes all 44 ported black-box CLI conformance test suites under <c>BlackBox/*.test.mjs</c>
/// against the compiled <c>dotnet hoshin.dll</c> binary.
/// </summary>
public sealed class BlackBoxConformanceTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    private static readonly string BlackBoxDir = Path.Combine(ProjectRoot, "BlackBox");
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");

    public static IEnumerable<object[]> BlackBoxSuites()
    {
        if (!Directory.Exists(BlackBoxDir))
        {
            yield break;
        }

        foreach (var file in Directory.GetFiles(BlackBoxDir, "*.test.mjs").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            yield return [Path.GetFileName(file)];
        }
    }

    [Theory]
    [MemberData(nameof(BlackBoxSuites))]
    public void BlackBoxSuite_PassesAgainstHoshinDll(string suiteFileName)
    {
        var suitePath = Path.Combine(BlackBoxDir, suiteFileName);
        Assert.True(File.Exists(HoshinDll), $"hoshin.dll not found at {HoshinDll}");
        Assert.True(File.Exists(suitePath), $"test suite not found at {suitePath}");

        var env = new Dictionary<string, string>
        {
            ["TEST_HOSHIN_DLL"] = HoshinDll,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        };

        var result = ProcessCommandRunner.Default.Run(new CommandRequest(
            WorkingDirectory: BlackBoxDir,
            Argv: ["node", "--test", suitePath],
            ExtraEnvironment: env,
            Timeout: TimeSpan.FromMinutes(10)));

        Assert.True(
            result.Started && !result.TimedOut && result.ExitCode == 0,
            $"Suite {suiteFileName} failed (started={result.Started}, timedOut={result.TimedOut}, exit={result.ExitCode}, err={result.StartError}):\n--- STDOUT ---\n{result.Stdout}\n--- STDERR ---\n{result.Stderr}");
    }
}
