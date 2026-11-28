using System.Globalization;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Driver;

/// <summary>The workflow records in a change's ledgers at one moment, so the driver can report what it did in between.</summary>
internal sealed record LedgerSnapshot(
    IReadOnlyList<WorkflowVerdict> Verdicts,
    IReadOnlyList<WorkflowCheckRun> Checks,
    IReadOnlyList<WorkflowPublished> Published)
{
    public static LedgerSnapshot Of(IEnumerable<RepoLedger> ledgers)
    {
        var all = ledgers.Distinct().ToList();
        return new(
            [.. all.SelectMany(l => l.WorkflowVerdicts).DistinctBy(VerdictKey)],
            [.. all.SelectMany(l => l.WorkflowChecks).DistinctBy(CheckKey)],
            [.. all.SelectMany(l => l.WorkflowPublishedRecords).DistinctBy(PublishedKey)]);
    }

    /// <summary>The records added since <paramref name="before"/>, oldest kind first.</summary>
    public IReadOnlyList<Happened> Since(LedgerSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(before);
        var happened = new List<Happened>();
        foreach (var v in Verdicts.ExceptBy(before.Verdicts.Select(VerdictKey), VerdictKey))
        {
            happened.Add(new Happened(
                v.Passed ? "task accepted" : "task refused",
                v.Passed
                    ? string.Create(CultureInfo.InvariantCulture, $"{v.Task} attempt {v.Attempt}: {v.Red}, green at {WorkflowEngine.Short(v.Head)}")
                    : string.Create(CultureInfo.InvariantCulture, $"{v.Task} attempt {v.Attempt}: {string.Join("; ", v.Reasons)}")));
        }

        foreach (var c in Checks.ExceptBy(before.Checks.Select(CheckKey), CheckKey))
        {
            happened.Add(new Happened(c.Passed ? "checks passed" : "checks failed", $"at {WorkflowEngine.Short(c.Head)}{(c.Passed ? "" : $": {c.FailedHookId ?? c.Output}")}"));
        }

        foreach (var p in Published.ExceptBy(before.Published.Select(PublishedKey), PublishedKey))
        {
            happened.Add(new Happened("published", $"{p.Ref} at {WorkflowEngine.Short(p.Head)}{(p.PullRequestUrl is null ? "" : $" ({p.PullRequestUrl})")}"));
        }

        return happened;
    }

    private static (string, int, DateTimeOffset) VerdictKey(WorkflowVerdict v) => (v.Task, v.Attempt, v.At);

    private static (string, DateTimeOffset) CheckKey(WorkflowCheckRun c) => (c.Head, c.At);

    private static (string, string, DateTimeOffset) PublishedKey(WorkflowPublished p) => (p.Ref, p.Head, p.At);
}
