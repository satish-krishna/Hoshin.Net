using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class LedgerStoreSafetyTests
{
    [Fact]
    public void RedShowCli_CorruptLedger_ExitsWithCode2LeavesFileUnchangedAndNamesFile()
    {
        using var ws = TestGitFixture.CreateWorkspace();
        var ledgerDir = TestServices.Ledger.LedgerFolder(ws.Master, "demo", TestGitFixture.ChangeName);
        Directory.CreateDirectory(ledgerDir);
        var ledgerPath = Path.Combine(ledgerDir, "app.json");
        const string garbage = "{ not valid json !!!";
        File.WriteAllText(ledgerPath, garbage);
        var originalBytes = File.ReadAllBytes(ledgerPath);

        var cli = TestGitFixture.RunCli(
            ws.Ws,
            "red",
            "show",
            "--json",
            "--change",
            TestGitFixture.ChangeName,
            "--repo",
            "app",
            "--task",
            "T1",
            "--master",
            ws.Master);

        Assert.Equal(2, cli.ExitCode);
        Assert.Equal(originalBytes, File.ReadAllBytes(ledgerPath));
        var combined = cli.Stdout + "\n" + cli.Stderr;
        Assert.Contains("app.json", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LedgerStore_ReadCorruptJson_ThrowsCouldNotRunExceptionNamingFile()
    {
        using var tmp = new TempDir("hoshin-t19-corrupt-");
        var file = Path.Combine(tmp.Path, "app.json");
        File.WriteAllText(file, "{ broken");

        var ex = Assert.Throws<CouldNotRunException>(() => TestServices.Ledger.Read(tmp.Path, "app"));
        Assert.Contains(file, ex.Message, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("{ broken", File.ReadAllText(file));
    }

    [Fact]
    public async Task LedgerStore_Update_SerializesConcurrentWritersWithoutLosingUpdates()
    {
        using var tmp = new TempDir("hoshin-t19-lock-");
        var initial = LedgerStore.Empty("demo", "c1", "app", tmp.Path);
        TestServices.Ledger.Write(tmp.Path, initial);

        const int workers = 8;
        using var barrier = new Barrier(workers);
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(() =>
        {
            barrier.SignalAndWait();
            TestServices.Ledger.Update(tmp.Path, "app", ledger =>
            {
                var updatedBlocks = new Dictionary<string, int>(ledger.GateBlocks, StringComparer.Ordinal)
                {
                    [$"hook-{i}"] = i,
                };
                return ledger with { GateBlocks = updatedBlocks };
            });
        })).ToArray();

        await Task.WhenAll(tasks);

        var finalLedger = TestServices.Ledger.Read(tmp.Path, "app");
        Assert.NotNull(finalLedger);
        Assert.Equal(workers, finalLedger!.GateBlocks.Count);
    }
}
