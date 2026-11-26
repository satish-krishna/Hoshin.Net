using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Engine;

/// <summary>Persists task and post-build fix verdicts (plus matching Red records and synthetic event runs) into a repository ledger.</summary>
internal static class TaskVerdictRecorder
{
    public static WorkflowVerdict Record(WorkflowRepo repo, WorkflowTask? task, WorkflowVerdict verdict, bool appendSyntheticRun)
    {
        repo.UpdateLedger(ledger =>
        {
            if (ledger.WorkflowVerdicts.Any(v => v.Task == verdict.Task && v.Attempt == verdict.Attempt))
            {
                return ledger;
            }

            var verdicts = new List<WorkflowVerdict>(ledger.WorkflowVerdicts) { verdict };
            var reds = ledger.Reds;
            if (task?.Test is { Length: > 0 } && verdict.Passed && !string.IsNullOrEmpty(verdict.TestCommit))
            {
                reds = new List<RedRecord>(reds ?? [])
                {
                    new(
                        task.Id,
                        verdict.TestCommit,
                        task.Compile,
                        task.Test,
                        "failed",
                        1,
                        verdict.Red,
                        verdict.At.ToString("O")),
                };
            }

            var updated = ledger with { Verdicts = verdicts, Reds = reds };
            if (!appendSyntheticRun)
            {
                return updated;
            }

            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["attempt"] = verdict.Attempt,
                ["red"] = verdict.Red,
            };
            if (!string.IsNullOrEmpty(verdict.TestCommit))
            {
                details["testCommit"] = verdict.TestCommit;
            }

            var run = new EventRecord(
                Event: "task.done",
                Repo: ledger.Repo,
                Key: verdict.Head,
                Subject: verdict.Task,
                Outcome: verdict.Passed ? "passed" : "failed",
                At: verdict.At.ToString("O"),
                Hooks: [],
                Problems: verdict.Reasons.ToList(),
                Failed: null,
                Details: details);

            return LedgerStore.AppendRun(updated, run);
        });
        return verdict;
    }
}
