using System.Diagnostics;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class ProcessCommandRunnerTimeoutTests
{
    [Fact]
    public async Task RunAsync_LargeStdinWithShortTimeout_TimesOutWithinThreeSeconds()
    {
        var largeStdin = new string('a', 1024 * 1024); // 1 MB
        var request = new CommandRequest(
            Directory.GetCurrentDirectory(),
            ["node", "-e", "setTimeout(() => {}, 6000)"],
            StandardInput: largeStdin,
            Timeout: TimeSpan.FromSeconds(1));

        var sw = Stopwatch.StartNew();
        var outcome = await ProcessCommandRunner.Default.RunAsync(request);
        sw.Stop();

        Assert.True(outcome.Started);
        Assert.True(outcome.TimedOut, $"Expected TimedOut=true, got TimedOut=false exit={outcome.ExitCode} in {sw.Elapsed.TotalSeconds:F2}s");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.5), $"Expected return within 3.5s, took {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task RunAsync_BackgroundGrandchildHoldingPipeOpen_ReturnsWithinFourSecondsWithOutput()
    {
        string[] argv = OperatingSystem.IsWindows()
            ? ["node", "-e", "const { spawn } = require('node:child_process'); spawn(process.execPath, ['-e', 'setTimeout(() => {}, 6000)'], { detached: true, stdio: ['ignore', process.stdout, process.stderr] }).unref(); process.stdout.write('hi\\n');"]
            : ["sh", "-c", "sleep 6 & echo hi"];

        var request = new CommandRequest(
            Directory.GetCurrentDirectory(),
            argv,
            Timeout: TimeSpan.FromSeconds(10));

        var sw = Stopwatch.StartNew();
        var outcome = await ProcessCommandRunner.Default.RunAsync(request);
        sw.Stop();

        Assert.True(outcome.Ok, $"Expected Ok, got timedOut={outcome.TimedOut} exit={outcome.ExitCode} err={outcome.StartError} stderr={outcome.Stderr}");
        Assert.Equal("hi", outcome.Stdout.Trim());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4.5), $"Expected return within 4.5s, took {sw.Elapsed.TotalSeconds:F2}s");
    }

    [Fact]
    public async Task RunAsync_HonorsCallerCancellationToken()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var request = new CommandRequest(
            Directory.GetCurrentDirectory(),
            ["node", "-e", "setTimeout(() => {}, 6000)"],
            Timeout: TimeSpan.FromSeconds(10));

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessCommandRunner.Default.RunAsync(request, cts.Token));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"Expected prompt cancellation, took {sw.Elapsed.TotalSeconds:F2}s");
    }
}
