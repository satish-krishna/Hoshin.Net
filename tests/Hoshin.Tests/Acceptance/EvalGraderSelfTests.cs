using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Acceptance;

/// <summary>
/// B03 item 4: runs the eval suite's grader self-tests (<c>evals/test/*.test.mjs</c>) against the test build of
/// <c>hoshin.dll</c>. They cost nothing and need no model, so they run with the acceptance folder at every commit, unlike the eval cases
/// themselves: a grader that can't match a passing run, or a case file the runner would refuse, is found on every
/// commit instead of on a paid run. Written by the reviewer; the implementer must not edit it (TASKS.md, R3-rule 2).
/// </summary>
[Trait("Category", "Acceptance")]
public sealed class EvalGraderSelfTests
{
    private static readonly string RepoRoot = Harness.AcceptanceWorkspace.RepoRoot;
    private static readonly string HoshinDll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");

    [Fact]
    public void EveryGraderMatchesAPassingRunAndRefusesAFailingOne()
    {
        var tests = Path.Combine(RepoRoot, "evals", "test");
        Assert.True(Directory.Exists(tests), $"no grader self-tests at {tests}");
        Assert.True(File.Exists(HoshinDll), $"hoshin.dll not found at {HoshinDll}");
        var files = Directory.GetFiles(tests, "*.test.mjs").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);

        var result = new ProcessCommandRunner().Run(new CommandRequest(
            WorkingDirectory: RepoRoot,
            Argv: ["node", "--test", "--test-concurrency=4", .. files],
            ExtraEnvironment: new Dictionary<string, string>
            {
                ["TEST_HOSHIN_DLL"] = HoshinDll,
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
            Timeout: TimeSpan.FromMinutes(30))); // about 6 minutes alone; the acceptance folder runs beside it

        Assert.True(
            result.Started && !result.TimedOut && result.ExitCode == 0,
            $"the grader self-tests failed (started={result.Started}, timedOut={result.TimedOut}, exit={result.ExitCode}, err={result.StartError}):\n--- STDOUT ---\n{result.Stdout}\n--- STDERR ---\n{result.Stderr}");
    }
}
