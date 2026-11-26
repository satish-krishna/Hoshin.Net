using Hoshin.Workflow.Changes;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// The single pure state machine for the entire change lifecycle:
/// <c>WriteIntent → AwaitAccept(intent) → WriteSpec → AwaitAccept(spec) → WritePlan → AwaitAccept(plan) → Implement/Retry → RunChecks → Review → NeedsApproval → Publish → Done</c>
/// plus the light-track <c>fix</c> lifecycle (<c>WriteFix → AwaitAcceptFix → Implement/Retry → ...</c>), <c>reject</c>, and <c>reopen</c>.
/// All step ordering, retry caps, and skill gate rules live strictly here (and in <see cref="WorkflowGate"/>).
/// </summary>
public static class WorkflowEngine
{
    public const int MaxAttempts = 3;

    public static Step Next(WorkflowState state)
    {
        if (NextAuthoringPhase(state) is { } authoring)
        {
            return authoring;
        }

        if (NextScheduledTaskStep(state) is { } taskStep)
        {
            return taskStep;
        }

        return NextPostBuild(state);
    }

    /// <summary>
    /// Computes the next step when a specific skill initiates a run (for example <c>fix</c> entering the light track on a fresh change),
    /// keeping every <see cref="Step"/> construction strictly inside <see cref="WorkflowEngine"/>.
    /// </summary>
    public static Step NextForSkill(WorkflowState state, string? invokedSkill)
    {
        if (string.Equals(invokedSkill, "fix", StringComparison.OrdinalIgnoreCase)
            && !state.IsLightTrack
            && !state.Intent.Exists
            && !state.Plan.HasPlan)
        {
            return Next(state with { IsLightTrack = true });
        }

        return Next(state);
    }

    /// <summary>
    /// Gate check: returns <c>null</c> if <paramref name="skill"/> may act at the current <see cref="Next"/> step;
    /// when <see cref="WorkflowState.ChangeState"/> is present, refuses with <see cref="ReadinessMap"/>'s exact
    /// <see cref="SkillReadiness.Stop"/> reason before falling back to step-owner gating.
    /// </summary>
    public static string? Gate(WorkflowState state, string skill) => WorkflowGate.Evaluate(state, skill);

    /// <summary>Pure gate for light-track <c>fix --accept</c> driven by <see cref="FixStateResult"/> rows 1–8.</summary>
    public static string? GateFixAccept(WorkflowState state) => WorkflowGate.EvaluateFixAccept(state);

    private static Step? NextAuthoringPhase(WorkflowState state)
    {
        if (state.IsLightTrack)
        {
            return NextFix(state);
        }

        if (state.Intent.Exists || !state.Plan.HasPlan)
        {
            return NextAuthoring(state);
        }

        return NextDirectPlan(state);
    }

    private static Step? NextDirectPlan(WorkflowState state)
    {
        if (!state.PlanDoc.Exists)
        {
            return null;
        }

        if (state.PlanDoc.HeadStatus == "rejected")
        {
            return new Done(false, "the plan is rejected");
        }

        return state.PlanDoc.InHead && state.PlanDoc.HeadStatus == "draft"
            ? new AwaitAccept(WorkflowDocument.Plan, state.PlanDoc.Path, state.Head)
            : null;
    }

    private static Step? NextFix(WorkflowState state)
    {
        if (state.ChangeState?.Fix is { } fix)
        {
            return fix.Row switch
            {
                1 => new Done(false, "the intent is rejected"),
                2 => new Done(false, "the light-track fix documents are in an inconsistent state; resolve them before continuing"),
                3 => new WriteFix(state.ChangeFolder, WorkflowBriefs.Fix(state)),
                4 or 5 or 6 => new AwaitAcceptFix(state.ChangeFolder, state.Head, FirstUnacceptedFromFacts(fix.Facts)),
                7 => fix.Facts.Built
                    ? new Done(false, "the light-track fix has build work with unaccepted documents; revise or reopen before continuing")
                    : new AwaitAcceptFix(state.ChangeFolder, state.Head, FirstUnacceptedFromFacts(fix.Facts)),
                8 => null,
                _ => new WriteFix(state.ChangeFolder, WorkflowBriefs.Fix(state)),
            };
        }

        if (state.Intent.HeadStatus == "rejected")
        {
            return new Done(false, "the intent is rejected");
        }

        var docs = new[] { state.Intent, state.Spec, state.PlanDoc };
        if (docs.Any(d => !d.Exists || d.Uncommitted || !d.InHead))
        {
            return new WriteFix(state.ChangeFolder, WorkflowBriefs.Fix(state));
        }

        if (docs.Any(d => d.HeadStatus != "accepted"))
        {
            return new AwaitAcceptFix(state.ChangeFolder, state.Head, FirstUnacceptedFromDocs(state));
        }

        return null;
    }

    private static WorkflowDocument FirstUnacceptedFromFacts(FixFacts facts)
    {
        if (!facts.Intent.InHead || facts.Intent.HeadStatus != "accepted")
        {
            return WorkflowDocument.Intent;
        }

        if (!facts.Spec.InHead || facts.Spec.HeadStatus != "accepted")
        {
            return WorkflowDocument.Spec;
        }

        return WorkflowDocument.Plan;
    }

    private static WorkflowDocument FirstUnacceptedFromDocs(WorkflowState state)
    {
        if (state.Intent.HeadStatus != "accepted")
        {
            return WorkflowDocument.Intent;
        }

        if (state.Spec.HeadStatus != "accepted")
        {
            return WorkflowDocument.Spec;
        }

        return WorkflowDocument.Plan;
    }

    private static Step NextPostBuild(WorkflowState state)
    {
        var codeHead = state.EffectiveCodeHead;
        var check = state.Ledger.WorkflowChecks.LastOrDefault(c => c.Head == codeHead);
        if (check is null)
        {
            return new RunChecks(codeHead);
        }

        if (!check.Passed)
        {
            return PostBuildFixEngine.NextForCheckFailure(state, check);
        }

        var review = state.Ledger.WorkflowReviews.LastOrDefault(r => r.Head == codeHead);
        if (review is null)
        {
            return !string.IsNullOrWhiteSpace(state.SessionRepo)
                ? new Done(true, $"all tasks for `{state.SessionRepo}` are built and checks pass; next step: run `review` from the master repository")
                : new ReviewDocument(Path.Combine(state.ChangeFolder, "build", "review.md"), codeHead, WorkflowBriefs.Review(state));
        }

        if (review.HighCount > 0)
        {
            return PostBuildFixEngine.NextForReviewFindings(state, review);
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
                return WriteDocumentFor(state, document, doc.Path);
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

    private static Step? NextScheduledTaskStep(WorkflowState state)
    {
        if (state.Plan.Tasks.Count == 0)
        {
            return null;
        }

        var completed = WorkflowProgress.CompletedTaskIds(state);
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

        var nextId = SelectNextTaskId(state, schedule, out var sessionHandoff);
        if (sessionHandoff is not null)
        {
            return sessionHandoff;
        }

        var nextTask = state.Plan.Tasks.First(t => t.Id == nextId);
        var verdicts = state.VerdictsForTask(nextTask);
        var failedVerdicts = verdicts.Where(v => !v.Passed).ToList();
        if (failedVerdicts.Count >= state.Plan.EffectiveMaxAttempts)
        {
            return new Done(false, $"{nextTask.Id} failed {failedVerdicts.Count} attempts: {string.Join("; ", failedVerdicts[^1].Reasons)}");
        }

        var attempt = verdicts.Count + 1;
        return failedVerdicts.Count == 0
            ? new Implement(WorkflowBriefs.Implement(state, nextTask, attempt, []))
            : new Retry(WorkflowBriefs.Implement(state, nextTask, attempt, failedVerdicts[^1].Reasons), failedVerdicts[^1].Reasons);
    }

    private static string? SelectNextTaskId(WorkflowState state, ChangeSchedule schedule, out Step? sessionHandoff)
    {
        sessionHandoff = null;
        if (string.IsNullOrWhiteSpace(state.SessionRepo))
        {
            return schedule.Repos.First(r => r.State == "ready" && r.Task is not null).Task!;
        }

        var repoEntry = schedule.Repos.FirstOrDefault(r => string.Equals(r.Repo, state.SessionRepo, StringComparison.Ordinal));
        if (repoEntry is { State: "ready", Task: not null })
        {
            return repoEntry.Task;
        }

        if (repoEntry is { State: "waiting" })
        {
            var deps = string.Join(", ", repoEntry.WaitingOn.Select(w => $"{w.Task} in `{w.Repo}`"));
            sessionHandoff = new Done(true, $"`{state.SessionRepo}` is waiting on {deps}; next step: run `build` in the dependency repository first");
            return null;
        }

        var nextReady = schedule.Repos
            .Where(r => r.State == "ready" && r.Task is not null)
            .Select(r => $"`{r.Repo}` ({r.Task})")
            .ToList();
        var nextHint = nextReady.Count > 0
            ? $"next step: run `build` in {string.Join(", ", nextReady)}"
            : "remaining repositories are blocked";
        sessionHandoff = new Done(true, $"all tasks for `{state.SessionRepo}` are built; {nextHint}");
        return null;
    }

    public static string? OwnerOf(Step step) => step switch
    {
        WriteDocument w => w.Document.ToWireName(),
        AwaitAccept or AwaitAcceptFix or NeedsApproval => "accept",
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
        AwaitAcceptFix f => $"accepting the light-track fix documents starting from {f.NextDocument.ToWireName()} (`accept` or `fix --accept`)",
        ReviewDocument => "reviewing the built change (`review`)",
        WriteFix => "drafting the light-track fix documents (`fix`)",
        NeedsApproval => "approving the built change (`accept`)",
        Implement or Retry or RunChecks or Publish => "building the accepted plan (`build`)",
        Done d => $"nothing: the change is finished ({d.Outcome})",
        _ => step.Kind.ToString(),
    };

    public static string Short(string sha) => WorkflowCodeHead.Short(sha);
}
