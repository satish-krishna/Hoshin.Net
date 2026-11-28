namespace Hoshin.Workflow.Engine;

/// <summary>
/// Pure post-build fix-loop decisions (R06 / W3) for failing <c>build.finish</c> checks (<c>onFailure: "fix"</c>
/// bounded by <c>gateRetries</c>) and high-severity review findings (<c>/hoshin:build &lt;instructions&gt;</c>).
/// </summary>
internal static class PostBuildFixEngine
{
    public static Step NextForCheckFailure(WorkflowState state, WorkflowCheckRun check)
    {
        var codeHead = state.EffectiveCodeHead;
        if (!string.Equals(check.OnFailure, "fix", StringComparison.OrdinalIgnoreCase) || check.GateRetries <= 0)
        {
            return new Done(false, $"checks failed at {WorkflowEngine.Short(codeHead)}");
        }

        var failures = CountConsecutiveFixableCheckFailures(state.Ledger.WorkflowChecks, check.FailedHookId);
        if (failures > check.GateRetries)
        {
            var hookName = string.IsNullOrWhiteSpace(check.FailedHookId) ? "build.finish" : check.FailedHookId;
            return new Done(false, $"checks failed at {WorkflowEngine.Short(codeHead)}: hook {hookName} still failing after {check.GateRetries} fix attempts");
        }

        var targetRepo = !string.IsNullOrWhiteSpace(check.FailedRepo)
            ? check.FailedRepo
            : (state.Plan.Tasks.Count > 0 ? state.Plan.Tasks[0].Repo : null);
        if (!string.IsNullOrWhiteSpace(state.SessionRepo)
            && !string.IsNullOrWhiteSpace(targetRepo)
            && !string.Equals(state.SessionRepo, targetRepo, StringComparison.Ordinal))
        {
            return new Done(true, $"check `{check.FailedHookId}` failed in `{targetRepo}`; next step: run `build` in `{targetRepo}` to fix it");
        }

        var taskId = $"FIX-CHECK-{failures}";
        var verdicts = state.VerdictsForTaskId(taskId, targetRepo);
        var failedVerdicts = verdicts.Where(v => !v.Passed).ToList();
        if (failedVerdicts.Count >= state.Plan.EffectiveMaxAttempts)
        {
            return new Done(false, $"{taskId} failed {failedVerdicts.Count} attempts: {string.Join("; ", failedVerdicts[^1].Reasons)}");
        }

        var attempt = verdicts.Count + 1;
        var handoff = failedVerdicts.Count > 0 ? failedVerdicts[^1].Reasons : [];
        var agent = WorkflowBriefs.FixCheckFailure(state, check, taskId, attempt, targetRepo, failures, handoff);
        return failedVerdicts.Count == 0
            ? new Implement(agent)
            : new Retry(agent, handoff);
    }

    public static Step NextForReviewFindings(WorkflowState state, WorkflowReviewRun review)
    {
        if (string.IsNullOrWhiteSpace(state.BuildInstructions))
        {
            return new Done(false, $"review {review.ReviewNumber} has {review.HighCount} high findings");
        }

        var targetRepo = ResolveReviewFixRepo(state);
        var taskId = $"FIX-R{review.ReviewNumber}";
        var verdicts = state.VerdictsForTaskId(taskId, targetRepo);
        var failedVerdicts = verdicts.Where(v => !v.Passed).ToList();
        if (failedVerdicts.Count >= state.Plan.EffectiveMaxAttempts)
        {
            return new Done(false, $"{taskId} failed {failedVerdicts.Count} attempts: {string.Join("; ", failedVerdicts[^1].Reasons)}");
        }

        var attempt = verdicts.Count + 1;
        var handoff = failedVerdicts.Count > 0 ? failedVerdicts[^1].Reasons : [];
        var agent = WorkflowBriefs.FixReviewFindings(state, review, taskId, attempt, targetRepo, state.BuildInstructions, handoff);
        return failedVerdicts.Count == 0
            ? new Implement(agent)
            : new Retry(agent, handoff);
    }

    public static Step NextForDeclinedApproval(WorkflowState state, WorkflowApproval approval)
    {
        var targetRepo = !string.IsNullOrWhiteSpace(state.SessionRepo)
            ? state.SessionRepo
            : (state.Plan.Tasks.Count > 0 ? state.Plan.Tasks[0].Repo : null);
        var declineNumber = Math.Max(1, state.Ledger.WorkflowApprovalRecords.Count(a => !a.Approved));
        var taskId = $"FIX-DECLINE-{declineNumber}";
        var verdicts = state.VerdictsForTaskId(taskId, targetRepo);
        var failedVerdicts = verdicts.Where(v => !v.Passed).ToList();
        if (failedVerdicts.Count >= state.Plan.EffectiveMaxAttempts)
        {
            return new Done(false, $"{taskId} failed {failedVerdicts.Count} attempts: {string.Join("; ", failedVerdicts[^1].Reasons)}");
        }

        var attempt = verdicts.Count + 1;
        var handoff = failedVerdicts.Count > 0 ? failedVerdicts[^1].Reasons : [];
        var agent = WorkflowBriefs.FixDeclinedApproval(state, approval, taskId, attempt, targetRepo, state.BuildInstructions, handoff);
        return failedVerdicts.Count == 0
            ? new Implement(agent)
            : new Retry(agent, handoff);
    }

    private static int CountConsecutiveFixableCheckFailures(IReadOnlyList<WorkflowCheckRun> checks, string? hookId)
    {
        var count = 0;
        for (var i = checks.Count - 1; i >= 0; i--)
        {
            var entry = checks[i];
            if (entry.Passed)
            {
                break;
            }

            if (!string.Equals(entry.OnFailure, "fix", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(hookId)
                && !string.IsNullOrWhiteSpace(entry.FailedHookId)
                && !string.Equals(entry.FailedHookId, hookId, StringComparison.Ordinal))
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static string? ResolveReviewFixRepo(WorkflowState state)
    {
        if (!string.IsNullOrWhiteSpace(state.SessionRepo))
        {
            return state.SessionRepo;
        }

        var findingRepos = (state.HighReviewFindings ?? [])
            .Select(f => f.Repo)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (findingRepos.Count == 1)
        {
            return findingRepos[0];
        }

        return state.Plan.Tasks.Count > 0 ? state.Plan.Tasks[0].Repo : null;
    }
}
