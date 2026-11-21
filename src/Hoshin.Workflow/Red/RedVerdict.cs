using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Red;

public sealed record RedVerdictResult(bool Observed, string? Reason, RedRecord? Record);

/// <summary>Computes the observed-red verdict for a task from the newest ledger record.</summary>
public sealed class RedVerdictService(LedgerStore? ledgerStore = null, RedRecorder? recorder = null)
{
    public static RedVerdictService Default { get; } = new();

    private readonly LedgerStore _ledger = ledgerStore ?? LedgerStore.Default;
    private readonly RedRecorder _recorder = recorder ?? RedRecorder.Default;

    public RedVerdictResult Verdict(EventContext ctx, string task)
    {
        var ledger = _ledger.Read(_ledger.LedgerFolder(ctx.Master, ctx.System, ctx.Change), ctx.Repo);
        var record = (ledger?.Reds ?? []).LastOrDefault(r => r.Task == task);
        if (record is null)
        {
            return new RedVerdictResult(false, $"no red recorded for {task} on this machine", null);
        }

        var at = record.Commit[..Math.Min(7, record.Commit.Length)];
        var commits = _recorder.TaskCommits(ctx, task);
        if (!commits.Contains(record.Commit))
        {
            return new RedVerdictResult(false, $"red was recorded at {at}, which is not a commit of {task} on this branch", record);
        }

        return record.Outcome switch
        {
            "passed" => new RedVerdictResult(false, $"the test passed at {at}, before the production code", record),
            "could-not-compile" => new RedVerdictResult(false, $"the test did not compile at {at}", record),
            "could-not-run" => new RedVerdictResult(false, $"the test could not run at {at}", record),
            _ => new RedVerdictResult(true, null, record),
        };
    }
}
