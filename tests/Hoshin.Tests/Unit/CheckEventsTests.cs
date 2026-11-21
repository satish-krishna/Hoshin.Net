using System.Text.Json;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class CheckEventsTests
{
    private static string ChecksPath(string master) =>
        CheckLog.ChecksFile(ProcessGitClient.Default, master, "demo", TestGitFixture.ChangeName);

    private static List<JsonElement> LinesOf(string master) =>
        File.ReadAllText(ChecksPath(master))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();

    [Fact]
    public async Task LoggedCheck_WhenRunThrowsAfterStartLine_WritesCouldNotRunEndLineBeforeRethrowing()
    {
        using var ws = TestGitFixture.CreateWorkspace(built: true);
        var problems = new List<string>();
        var log = new CheckLog(ChecksPath(ws.Master), "build.finish", "app", problems);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            log.LoggedAsync("a", "head", 30, true, () => Task.FromException<HookRecord>(new InvalidOperationException("boom"))));

        var lines = LinesOf(ws.Master);
        Assert.Equal(2, lines.Count);

        var start = lines[0];
        Assert.Equal("check-start", start.GetProperty("type").GetString());
        Assert.Equal("a", start.GetProperty("check").GetString());
        Assert.Equal("head", start.GetProperty("phase").GetString());
        Assert.Equal(30, start.GetProperty("timeout").GetInt32());
        Assert.True(start.GetProperty("pid").GetInt32() > 0);

        var end = lines[1];
        Assert.Equal("check-end", end.GetProperty("type").GetString());
        Assert.Equal("a", end.GetProperty("check").GetString());
        Assert.Equal("head", end.GetProperty("phase").GetString());
        Assert.Equal("could-not-run", end.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, end.GetProperty("ids").ValueKind);
        Assert.True(end.GetProperty("blocks").GetBoolean());
        Assert.False(end.TryGetProperty("timeout", out _));
        Assert.False(end.TryGetProperty("pid", out _));
    }

    [Fact]
    public void CheckLog_UnwritablePath_ReportsProblemOnce()
    {
        using var ws = TestGitFixture.CreateWorkspace(built: true);
        var path = ChecksPath(ws.Master);
        Directory.CreateDirectory(path);
        var problems = new List<string>();
        var log = new CheckLog(path, "build.finish", "app", problems);
        log.Start("a", "head", 30);
        log.Start("b", "head", 30);

        Assert.Single(problems);
        Assert.StartsWith($"check events could not be written to {path}: ", problems[0]);
    }
}
