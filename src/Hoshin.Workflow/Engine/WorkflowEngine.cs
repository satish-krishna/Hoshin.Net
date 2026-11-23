using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Pure snapshot of where a workflow run stands, computed from <see cref="ChangeStateService"/>, git, JSONC change documents,
/// and the unified <see cref="RepoLedger"/> in <c>.git/hoshin/</c>. Holds nothing owned by any specific harness.
/// </summary>
public sealed record WorkflowState(
    string RepoRoot,
    string ChangeFolder,
    WorkflowPlan Plan,
    string Head,
    RepoLedger Ledger,
    string? SkillsRoot,
    WorkflowDocState Intent,
    WorkflowDocState Spec,
    WorkflowDocState PlanDoc,
    string? CodeHead = null,
    ChangeStateItem? ChangeState = null,
    Dictionary<string, RepoLedger>? RepoLedgers = null,
    Dictionary<string, string>? RepoPaths = null)
{
    public string EffectiveCodeHead => string.IsNullOrWhiteSpace(CodeHead) ? Head : CodeHead;

    public IReadOnlyList<WorkflowVerdict> VerdictsForTask(WorkflowTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Repo)
            && RepoLedgers is not null
            && RepoLedgers.TryGetValue(task.Repo, out var repoLedger))
        {
            var repoVerdicts = repoLedger.WorkflowVerdicts.Where(v => v.Task == task.Id).ToList();
            if (repoVerdicts.Count > 0)
            {
                return repoVerdicts;
            }
        }

        return Ledger.WorkflowVerdicts.Where(v => v.Task == task.Id).ToList();
    }

    public string RepositoryPathFor(WorkflowTask task) =>
        !string.IsNullOrWhiteSpace(task.Repo)
        && RepoPaths is not null
        && RepoPaths.TryGetValue(task.Repo, out var path)
            ? path
            : RepoRoot;

    public WorkflowDocState Document(WorkflowDocument document) => document switch
    {
        WorkflowDocument.Intent => Intent,
        WorkflowDocument.Spec => Spec,
        WorkflowDocument.Plan => PlanDoc,
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown workflow document."),
    };
}

/// <summary>
/// The single pure state machine for the entire change lifecycle:
/// <c>WriteIntent → AwaitAccept(intent) → WriteSpec → AwaitAccept(spec) → WritePlan → AwaitAccept(plan) → Implement/Retry → RunChecks → Review → NeedsApproval → Publish → Done</c>.
/// All step ordering, retry caps, and skill gate rules live strictly here.
/// </summary>
public static class WorkflowEngine
{
    public const int MaxAttempts = 3;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public static Step Next(WorkflowState state)
    {
        // Authoring pipeline runs when an intent exists or no plan exists yet.
        if (state.Intent.Exists || !state.Plan.HasPlan)
        {
            var authoring = NextAuthoring(state);
            if (authoring is not null)
            {
                return authoring;
            }
        }
        else if (state.PlanDoc.Exists)
        {
            // Direct plan-based change: still honor explicit draft/rejected status on plan.md.
            if (state.PlanDoc.HeadStatus == "rejected")
            {
                return new Done(false, "the plan is rejected");
            }

            if (state.PlanDoc.InHead && state.PlanDoc.HeadStatus == "draft")
            {
                return new AwaitAccept(WorkflowDocument.Plan, state.PlanDoc.Path, state.Head);
            }
        }

        if (NextScheduledTaskStep(state) is { } taskStep)
        {
            return taskStep;
        }

        var codeHead = state.EffectiveCodeHead;
        var check = state.Ledger.WorkflowChecks.LastOrDefault(c => c.Head == codeHead);
        if (check is null)
        {
            return new RunChecks(codeHead);
        }

        if (!check.Passed)
        {
            return new Done(false, $"checks failed at {Short(codeHead)}");
        }

        var review = state.Ledger.WorkflowReviews.LastOrDefault(r => r.Head == codeHead);
        if (review is null)
        {
            return new ReviewDocument(Path.Combine(state.ChangeFolder, "build", "review.md"), codeHead, WorkflowBriefs.Review(state));
        }

        if (review.HighCount > 0)
        {
            return new Done(false, $"review {review.ReviewNumber} has {review.HighCount} high findings");
        }

        var approval = state.Ledger.WorkflowApprovalRecords.LastOrDefault(a => a.Head == codeHead);
        if (approval is null)
        {
            return new NeedsApproval(codeHead, $"every task passed, checks are green, and review {review.ReviewNumber} has no high findings at {Short(codeHead)}");
        }

        if (!approval.Approved)
        {
            return new Done(false, $"rejected at {Short(codeHead)}: {approval.Note}");
        }

        var targetRef = $"refs/heads/{state.Plan.EffectiveBranch}";
        var published = state.Ledger.WorkflowPublishedRecords.LastOrDefault(p => p.Head == codeHead || p.Head == state.Head);
        return published is not null
            ? new Done(true, string.IsNullOrEmpty(published.PullRequestUrl)
                ? $"published {Short(codeHead)} to {targetRef}"
                : $"published {Short(codeHead)} to {targetRef} ({published.PullRequestUrl})")
            : new Publish(codeHead, targetRef);
    }

    private static Step? NextAuthoring(WorkflowState state)
    {
        foreach (var document in new[] { WorkflowDocument.Intent, WorkflowDocument.Spec, WorkflowDocument.Plan })
        {
            var doc = state.Document(document);
            if (!doc.Exists || doc.Uncommitted || !doc.InHead)
            {
                var brief = document switch
                {
                    WorkflowDocument.Intent => WorkflowBriefs.Intent(state),
                    WorkflowDocument.Spec => WorkflowBriefs.Spec(state),
                    WorkflowDocument.Plan => WorkflowBriefs.Plan(state),
                    _ => throw new ArgumentOutOfRangeException(nameof(state)),
                };
                return new WriteDocument(document, doc.Path, brief);
            }

            switch (doc.HeadStatus)
            {
                case "rejected":
                    return new Done(false, $"the {document.ToWireName()} is rejected");
                case "superseded" when document == WorkflowDocument.Plan:
                    return new WriteDocument(WorkflowDocument.Plan, doc.Path, WorkflowBriefs.Plan(state));
                case "accepted":
                    if (NeedsRegenerationFromPin(state, document, doc))
                    {
                        return WriteDocumentFor(state, document, doc.Path);
                    }

                    continue;
                default:
                    return new AwaitAccept(document, doc.Path, state.Head);
            }
        }

        return null;
    }

    private static bool NeedsRegenerationFromPin(WorkflowState state, WorkflowDocument document, WorkflowDocState doc) =>
        document switch
        {
            WorkflowDocument.Spec => state.ChangeState?.Ready.Spec is { Candidate: true, Stop: null } && doc.Pin != "holds",
            WorkflowDocument.Plan => doc.Pin is "broken" or "missing" && state.ChangeState?.Ready.Plan.Stop is null,
            _ => false,
        };

    private static WriteDocument WriteDocumentFor(WorkflowState state, WorkflowDocument document, string path)
    {
        var brief = document switch
        {
            WorkflowDocument.Intent => WorkflowBriefs.Intent(state),
            WorkflowDocument.Spec => WorkflowBriefs.Spec(state),
            WorkflowDocument.Plan => WorkflowBriefs.Plan(state),
            _ => throw new ArgumentOutOfRangeException(nameof(document)),
        };
        return new WriteDocument(document, path, brief);
    }

    /// <summary>
    /// Gate check: returns <c>null</c> if <paramref name="skill"/> may act at the current <see cref="Next"/> step;
    /// when <see cref="WorkflowState.ChangeState"/> is present, refuses with <see cref="ReadinessMap"/>'s exact
    /// <see cref="SkillReadiness.Stop"/> reason before falling back to step-owner gating.
    /// </summary>
    public static string? Gate(WorkflowState state, string skill)
    {
        var normalized = skill.ToLowerInvariant();
        if (normalized is "setup" or "context")
        {
            return null;
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

        var step = Next(state);
        if (normalized == "review" && step is RunChecks or NeedsApproval or Publish)
        {
            return null;
        }

        if (normalized == "fix" && step is WriteDocument or AwaitAccept)
        {
            return null;
        }

        if (normalized == "done" && step is NeedsApproval or Publish)
        {
            return null;
        }

        var owner = OwnerOf(step);
        if (string.Equals(owner, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"`{skill}` cannot run on this change now. The next step is {Describe(step)}.";
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

        var allPassedInLedger = state.Plan.Tasks.Count > 0
            && state.Plan.Tasks.All(t => state.VerdictsForTask(t).Any(v => v.Passed));
        var isOnlyUnloggedTasks = ready.Review.Stop.StartsWith("These tasks have no build log entry yet:", StringComparison.Ordinal);
        return allPassedInLedger && isOnlyUnloggedTasks ? null : ready.Review.Stop;
    }

    private static Step? NextScheduledTaskStep(WorkflowState state)
    {
        if (state.Plan.Tasks.Count == 0)
        {
            return null;
        }

        var completed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in state.Plan.Tasks)
        {
            if (state.VerdictsForTask(task).Any(v => v.Passed))
            {
                completed.Add(task.Id);
            }
        }

        var scheduleInputs = state.Plan.Tasks
            .Select(t => new ScheduledTaskInput(
                t.Id,
                string.IsNullOrWhiteSpace(t.Repo) ? "default" : t.Repo,
                t.DependsOn ?? []))
            .ToList();
        var schedule = ScheduleCalculator.Compute(scheduleInputs, completed, oneAtATime: false);
        if (schedule.Next == "review")
        {
            return null;
        }

        if (schedule.Next == "blocked")
        {
            return new Done(false, "plan tasks are blocked by circular or unsatisfied cross-repository dependencies");
        }

        var nextId = schedule.Repos.First(r => r.State == "ready" && r.Task is not null).Task!;
        var nextTask = state.Plan.Tasks.First(t => t.Id == nextId);
        var verdicts = state.VerdictsForTask(nextTask);
        if (verdicts.Count >= state.Plan.EffectiveMaxAttempts)
        {
            return new Done(false, $"{nextTask.Id} failed {verdicts.Count} attempts: {string.Join("; ", verdicts[^1].Reasons)}");
        }

        var attempt = verdicts.Count + 1;
        return verdicts.Count == 0
            ? new Implement(WorkflowBriefs.Implement(state, nextTask, attempt, []))
            : new Retry(WorkflowBriefs.Implement(state, nextTask, attempt, verdicts[^1].Reasons), verdicts[^1].Reasons);
    }

    private static bool IsReadyCandidate(ReadinessMap ready, string skill) => skill switch
    {
        "spec" => ready.Spec.Candidate && ready.Spec.Stop is null,
        "plan" => ready.Plan.Candidate && ready.Plan.Stop is null,
        _ => false,
    };

    public static string? OwnerOf(Step step) => step switch
    {
        WriteDocument w => w.Document.ToWireName(),
        AwaitAccept or NeedsApproval => "accept",
        ReviewDocument => "review",
        WriteFix => "fix",
        Implement or Retry or RunChecks or Publish => "build",
        _ => null,
    };

    public static string Describe(Step step) => step switch
    {
        WriteDocument { Document: WorkflowDocument.Intent } => "writing the intent (`intent`)",
        WriteDocument { Document: WorkflowDocument.Spec } => "writing the spec from the accepted intent (`spec`)",
        WriteDocument { Document: WorkflowDocument.Plan } => "writing the plan from the accepted spec (`plan`)",
        AwaitAccept a => $"accepting the {a.Document.ToWireName()}, which is committed at {a.Path} (`accept`)",
        ReviewDocument => "reviewing the built change (`review`)",
        WriteFix => "drafting the light-track fix documents (`fix`)",
        NeedsApproval => "approving the built change (`accept`)",
        Implement or Retry or RunChecks or Publish => "building the accepted plan (`build`)",
        Done d => $"nothing: the change is finished ({d.Outcome})",
        _ => step.Kind.ToString(),
    };

    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
