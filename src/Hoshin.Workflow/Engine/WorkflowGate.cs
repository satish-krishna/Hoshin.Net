using Hoshin.Workflow.Changes;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Pure skill-gating rules driven by <see cref="ChangeStateService"/>'s <see cref="ReadinessMap"/> and
/// <see cref="FixStateService"/>'s 8-row table, with step-owner fallback for standalone repositories.
/// </summary>
internal static class WorkflowGate
{
    public static string? Evaluate(WorkflowState state, string skill)
    {
        var normalized = skill.ToLowerInvariant();
        if (normalized is "setup" or "context")
        {
            return null;
        }

        if (normalized == "reject")
        {
            return GateReject(state);
        }

        if (normalized == "reopen")
        {
            return GateReopen(state);
        }

        if (normalized == "decline")
        {
            var step = WorkflowEngine.Next(state);
            return step is NeedsApproval
                ? null
                : $"`decline` can only run when the change is awaiting approval. The next step is {WorkflowEngine.Describe(step)}.";
        }

        if (state.ChangeState is { Ready: var ready })
        {
            if (ReadinessStop(state, ready, normalized) is { } stop)
            {
                return stop;
            }

            if (IsReadyCandidate(ready, normalized))
            {
                return null;
            }
        }

        return GateByStepOwner(state, normalized, skill);
    }

    public static string? EvaluateFixAccept(WorkflowState state)
    {
        if (state.ChangeState?.Fix is { } fix)
        {
            return fix.Row switch
            {
                1 => $"`{state.Plan.Change}` is closed: its intent was rejected.",
                2 => "The light-track fix documents are in an inconsistent state; resolve them before continuing.",
                3 => $"The light-track fix has unrecorded document changes ({string.Join(", ", fix.Facts.Unrecorded)}). Commit or discard them before accepting.",
                4 or 5 or 6 => null,
                7 => fix.Facts.Built
                    ? "The light-track fix has build work with unaccepted documents; settle it before accepting."
                    : null,
                8 => $"All light-track fix documents are already accepted. The next step is {WorkflowEngine.Describe(WorkflowEngine.Next(state))}.",
                _ => null,
            };
        }

        var step = WorkflowEngine.Next(state);
        return step is AwaitAcceptFix ? null : $"`fix --accept` cannot run on this change now. The next step is {WorkflowEngine.Describe(step)}.";
    }

    private static string? GateReject(WorkflowState state)
    {
        if (state.ChangeState is { Ready.Reject: var reject })
        {
            return reject.Stop;
        }

        if (state.Intent.HeadStatus == "rejected")
        {
            return $"`{state.Plan.Change}` is closed: its intent was rejected.";
        }

        if (state.Ledger.WorkflowVerdicts.Count > 0 || state.Ledger.WorkflowReviews.Count > 0)
        {
            return $"`{state.Plan.Change}` has build work (a build log or a review), so it is not rejected. The engineer settles that work first.";
        }

        var dirty = new[] { state.Intent, state.Spec, state.PlanDoc }.FirstOrDefault(d => d.Uncommitted);
        return dirty is not null
            ? $"`{Path.GetFileName(dirty.Path)}` has uncommitted changes. Commit or discard them first, so the rejection does not carry them unread."
            : null;
    }

    private static string? GateReopen(WorkflowState state)
    {
        if (state.Intent.HeadStatus == "rejected")
        {
            return $"`{state.Plan.Change}` is closed: its intent was rejected.";
        }

        var hasBuildWork = state.ChangeState is { BuildLogs.Count: > 0 }
            || state.Ledger.WorkflowVerdicts.Count > 0
            || state.Ledger.WorkflowReviews.Count > 0;
        if (hasBuildWork)
        {
            return $"`{state.Plan.Change}` already has build work; settle or clear it before reopening authoring documents.";
        }

        var dirty = new[] { state.Intent, state.Spec, state.PlanDoc }.FirstOrDefault(d => d.Uncommitted);
        if (dirty is not null)
        {
            return $"`{Path.GetFileName(dirty.Path)}` has uncommitted changes. Commit or discard them first before reopening.";
        }

        var anyAccepted = new[] { state.Intent, state.Spec, state.PlanDoc }.Any(d => d.InHead && d.HeadStatus == "accepted");
        return anyAccepted ? null : "No accepted document exists to reopen.";
    }

    private static string? GateByStepOwner(WorkflowState state, string normalized, string rawSkill)
    {
        if (normalized == "fix" && state.ChangeState?.Fix is { } fix)
        {
            if (fix.Row == 1)
            {
                return $"`{state.Plan.Change}` is closed: its intent was rejected.";
            }

            if (fix.Row == 2)
            {
                return "The light-track fix documents are in an inconsistent state; resolve them before continuing.";
            }

            if (fix.Row is 3 or 4 or 5 or 6 or 7)
            {
                return null;
            }
        }

        var step = WorkflowEngine.Next(state);
        if (IsSkillAllowedAtStep(normalized, step))
        {
            return null;
        }

        return $"`{rawSkill}` cannot run on this change now. The next step is {WorkflowEngine.Describe(step)}.";
    }

    private static bool IsSkillAllowedAtStep(string normalized, Step step)
    {
        if (normalized == "review" && step is RunChecks or NeedsApproval or Publish)
        {
            return true;
        }

        if (normalized == "fix" && step is WriteDocument or AwaitAccept or WriteFix or AwaitAcceptFix)
        {
            return true;
        }

        if (normalized == "done" && step is NeedsApproval or Publish)
        {
            return true;
        }

        return string.Equals(WorkflowEngine.OwnerOf(step), normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadinessStop(WorkflowState state, ReadinessMap ready, string skill) => skill switch
    {
        "spec" => ready.Spec.Stop,
        "plan" => ready.Plan.Stop,
        "build" => ready.Build.Stop,
        "review" => ReviewReadinessStop(state, ready),
        "reject" => ready.Reject.Stop,
        "done" => ready.Build.Stop,
        _ => null,
    };

    private static string? ReviewReadinessStop(WorkflowState state, ReadinessMap ready)
    {
        if (ready.Build.Stop is not null)
        {
            return ready.Build.Stop;
        }

        if (ready.Review.Stop is null)
        {
            return null;
        }

        var completed = WorkflowProgress.CompletedTaskIds(state);
        var allCompleted = state.Plan.Tasks.Count > 0
            && state.Plan.Tasks.All(t => completed.Contains(t.Id));
        var isOnlyUnloggedTasks = ready.Review.Stop.StartsWith("These tasks have no build log entry yet:", StringComparison.Ordinal);
        return allCompleted && isOnlyUnloggedTasks ? null : ready.Review.Stop;
    }

    private static bool IsReadyCandidate(ReadinessMap ready, string skill) => skill switch
    {
        "spec" => ready.Spec.Candidate && ready.Spec.Stop is null,
        "plan" => ready.Plan.Candidate && ready.Plan.Stop is null,
        _ => false,
    };
}
