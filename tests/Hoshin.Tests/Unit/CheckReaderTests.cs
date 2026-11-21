using Hoshin.Workflow.Events;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class CheckReaderTests
{
    [Fact]
    public void IsPidAlive_CurrentProcessIsAliveAndInvalidPidIsNot()
    {
        Assert.True(CheckReader.IsPidAlive(Environment.ProcessId));
        Assert.False(CheckReader.IsPidAlive(0));
        Assert.False(CheckReader.IsPidAlive(-1));
    }

    [Fact]
    public void Read_MarksDeadPidAsAbandonedAndAlivePidAsRunning()
    {
        using var temp = new TempDir("hoshin-checkread-");
        var file = Path.Combine(temp.Path, "checks.jsonl");
        var now = DateTimeOffset.UtcNow;
        var at = now.AddSeconds(-5).ToString("O");
        File.WriteAllText(file,
            $$"""
            {"run":"r1","event":"build.finish","repo":"app","at":"{{at}}","type":"check-start","check":"unit","phase":"head","pid":123,"timeout":60}
            {"run":"r2","event":"build.finish","repo":"web","at":"{{at}}","type":"check-start","check":"lint","phase":"head","pid":456,"timeout":60}
            """.Replace("\r\n", "\n") + "\n");

        var dead = CheckReader.Read(file, now: now, pidAlive: pid => pid == 456);
        Assert.Empty(dead.Problems);
        Assert.Equal(2, dead.Runs.Count);
        Assert.Equal("abandoned", dead.Runs[0].Checks[0].State);
        Assert.Equal("running", dead.Runs[1].Checks[0].State);
    }

    [Fact]
    public void Read_MarksExpiredAndSupersededChecksAsAbandoned()
    {
        using var temp = new TempDir("hoshin-checkread-");
        var file = Path.Combine(temp.Path, "checks.jsonl");
        var now = DateTimeOffset.Parse("2026-11-21T12:00:00Z");
        File.WriteAllText(file,
            """
            {"run":"r1","event":"build.finish","repo":"app","at":"2026-11-21T11:59:50Z","type":"check-start","check":"a","phase":"head","pid":10,"timeout":60}
            {"run":"r2","event":"build.finish","repo":"app","at":"2026-11-21T11:58:00Z","type":"check-start","check":"b","phase":"head","pid":11,"timeout":30}
            """.Replace("\r\n", "\n") + "\n");

        var res = CheckReader.Read(file, now: now, pidAlive: _ => true);
        Assert.Empty(res.Problems);
        Assert.Equal("abandoned", res.Runs[0].Checks[0].State); // superseded by r2
        Assert.Equal("abandoned", res.Runs[1].Checks[0].State); // expired (11:58 + 30s <= 12:00)
    }

    [Fact]
    public void Read_IgnoresIncompleteTrailingLineAndReportsMalformedCompleteLine()
    {
        using var temp = new TempDir("hoshin-checkread-");
        var file = Path.Combine(temp.Path, "checks.jsonl");
        File.WriteAllText(file, "not-json\n{\"run\":\"r1\",\"type\":\"check-start\"");

        var res = CheckReader.Read(file);
        Assert.Empty(res.Runs);
        Assert.Single(res.Problems);
        Assert.Contains("line 1", res.Problems[0]);
    }
}
