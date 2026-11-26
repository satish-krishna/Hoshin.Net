namespace Hoshin.Workflow.Engine;

/// <summary>
/// Pure progress authority for the workflow engine (R05 / W6): in Hoshin workspaces where
/// <see cref="WorkflowState.ChangeState"/> provides committed build-log progress, a task is done
/// if and only if its build-log entry is committed in HEAD; the local ledger is a cache of attempts
/// and verdict details. Standalone <c>.hoshin/</c> repositories without build logs fall back to ledger verdicts.
/// </summary>
public static class WorkflowProgress
{
    public static HashSet<string> CompletedTaskIds(WorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ChangeState?.Tasks is { } taskMap)
        {
            return LoggedTaskIds(taskMap);
        }

        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in state.Plan.Tasks)
        {
            if (state.VerdictsForTask(task).Any(v => v.Passed))
            {
                completed.Add(task.Id);
            }
        }

        return completed;
    }

    public static IReadOnlyList<string> DescribeDisagreements(WorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ChangeState?.Tasks is not { } taskMap)
        {
            return [];
        }

        var logged = LoggedTaskIds(taskMap);
        var messages = new List<string>();
        foreach (var task in state.Plan.Tasks)
        {
            var inLog = logged.Contains(task.Id);
            var verdicts = state.VerdictsForTask(task);
            var passedInLedger = verdicts.Any(v => v.Passed);

            if (inLog && !passedInLedger && verdicts.Count > 0)
            {
                messages.Add($"{task.Id} is logged in committed build logs while the local ledger has no passing verdict; committed build logs win.");
            }
            else if (!inLog && passedInLedger)
            {
                messages.Add($"{task.Id} has a passing verdict in the local ledger but no committed build-log entry; committed build logs win.");
            }
        }

        return messages;
    }

    private static HashSet<string> LoggedTaskIds(Dictionary<string, Changes.TaskWorkState> taskMap)
    {
        var logged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in taskMap.Values)
        {
            foreach (var id in entry.Logged)
            {
                logged.Add(id);
            }
        }

        return logged;
    }
}
