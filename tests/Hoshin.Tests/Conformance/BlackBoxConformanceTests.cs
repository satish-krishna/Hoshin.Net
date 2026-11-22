using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Conformance;

/// <summary>
/// Shared runner for the 44 ported black-box CLI conformance test suites under <c>BlackBox/*.test.mjs</c>
/// against the compiled <c>dotnet hoshin.dll</c> binary. Split across 4 xUnit test classes so xUnit
/// executes shards in parallel across CPU cores while preserving <c>BlackBoxConformanceTests</c> filter names.
/// </summary>
public abstract class BlackBoxConformanceTestsBase
{
    private const int ShardCount = 4;
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    private static readonly string BlackBoxDir = Path.Combine(ProjectRoot, "BlackBox");
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");

    public static IEnumerable<object[]> SuitesForShard(int shard)
    {
        if (!Directory.Exists(BlackBoxDir))
        {
            yield break;
        }

        var files = Directory.GetFiles(BlackBoxDir, "*.test.mjs")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToArray();

        for (var i = 0; i < files.Length; i++)
        {
            if (i % ShardCount == shard)
            {
                yield return [Path.GetFileName(files[i])];
            }
        }
    }

    protected static void RunSuite(string suiteFileName)
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

public sealed class BlackBoxConformanceTestsShard0 : BlackBoxConformanceTestsBase
{
    public static IEnumerable<object[]> BlackBoxSuites() => SuitesForShard(0);

    [Theory]
    [MemberData(nameof(BlackBoxSuites))]
    public void BlackBoxSuite_PassesAgainstHoshinDll(string suiteFileName) => RunSuite(suiteFileName);
}

public sealed class BlackBoxConformanceTestsShard1 : BlackBoxConformanceTestsBase
{
    public static IEnumerable<object[]> BlackBoxSuites() => SuitesForShard(1);

    [Theory]
    [MemberData(nameof(BlackBoxSuites))]
    public void BlackBoxSuite_PassesAgainstHoshinDll(string suiteFileName) => RunSuite(suiteFileName);
}

public sealed class BlackBoxConformanceTestsShard2 : BlackBoxConformanceTestsBase
{
    public static IEnumerable<object[]> BlackBoxSuites() => SuitesForShard(2);

    [Theory]
    [MemberData(nameof(BlackBoxSuites))]
    public void BlackBoxSuite_PassesAgainstHoshinDll(string suiteFileName) => RunSuite(suiteFileName);
}

public sealed class BlackBoxConformanceTestsShard3 : BlackBoxConformanceTestsBase
{
    public static IEnumerable<object[]> BlackBoxSuites() => SuitesForShard(3);

    [Theory]
    [MemberData(nameof(BlackBoxSuites))]
    public void BlackBoxSuite_PassesAgainstHoshinDll(string suiteFileName) => RunSuite(suiteFileName);
}
