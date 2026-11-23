using Hoshin.Workflow.Documents;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Pure snapshot of where a workflow run stands, computed from git, JSONC change documents, and the local ledger in <c>.git/hoshin/</c>.
/// Holds nothing owned by any specific harness.
/// </summary>
public sealed record WorkflowState(
    string RepoRoot,
    string ChangeFolder,
    WorkflowPlan Plan,
    string Head,
    WorkflowLedger Ledger,
    string? SkillsRoot,
    WorkflowDocState Intent,
    WorkflowDocState Spec,
    WorkflowDocState PlanDoc,
    string? CodeHead = null)
{
    public string EffectiveCodeHead => string.IsNullOrWhiteSpace(CodeHead) ? Head : CodeHead;

    public static WorkflowState? Read(WorkflowRepo repo, PlanReader planReader, string? skillsRoot = null)
    {
        var plan = repo.ReadPlan(planReader);
        if (plan is null)
        {
            return null;
        }

        return new WorkflowState(
            repo.Root,
            repo.ChangeFolder,
            plan,
            repo.Head(),
            repo.ReadLedger(),
            skillsRoot,
            repo.DocumentStatus("intent.md"),
            repo.DocumentStatus("spec.md"),
            repo.DocumentStatus("plan.md"),
            repo.CodeHead());
    }

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

        foreach (var task in state.Plan.Tasks)
        {
            var verdicts = state.Ledger.Verdicts.Where(v => v.Task == task.Id).ToList();
            if (verdicts.Any(v => v.Passed))
            {
                continue;
            }

            if (verdicts.Count >= state.Plan.EffectiveMaxAttempts)
            {
                return new Done(false, $"{task.Id} failed {verdicts.Count} attempts: {string.Join("; ", verdicts[^1].Reasons)}");
            }

            var attempt = verdicts.Count + 1;
            return verdicts.Count == 0
                ? new Implement(WorkflowBriefs.Implement(state, task, attempt, []))
                : new Retry(WorkflowBriefs.Implement(state, task, attempt, verdicts[^1].Reasons), verdicts[^1].Reasons);
        }

        var codeHead = state.EffectiveCodeHead;
        var check = state.Ledger.Checks.LastOrDefault(c => c.Head == codeHead);
        if (check is null)
        {
            return new RunChecks(codeHead);
        }

        if (!check.Passed)
        {
            return new Done(false, $"checks failed at {Short(codeHead)}");
        }

        var review = state.Ledger.Reviews.LastOrDefault(r => r.Head == codeHead);
        if (review is null)
        {
            return new ReviewDocument(Path.Combine(state.ChangeFolder, "build", "review.md"), codeHead, WorkflowBriefs.Review(state));
        }

        if (review.HighCount > 0)
        {
            return new Done(false, $"review {review.ReviewNumber} has {review.HighCount} high findings");
        }

        var approval = state.Ledger.Approvals.LastOrDefault(a => a.Head == codeHead);
        if (approval is null)
        {
            return new NeedsApproval(codeHead, $"every task passed, checks are green, and review {review.ReviewNumber} has no high findings at {Short(codeHead)}");
        }

        if (!approval.Approved)
        {
            return new Done(false, $"rejected at {Short(codeHead)}: {approval.Note}");
        }

        var targetRef = $"refs/heads/{state.Plan.EffectiveBranch}";
        var published = state.Ledger.Published.LastOrDefault(p => p.Head == codeHead || p.Head == state.Head);
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
                case "accepted":
                    continue;
                default:
                    return new AwaitAccept(document, doc.Path, state.Head);
            }
        }

        return null;
    }

    /// <summary>
    /// Gate check: returns <c>null</c> if <paramref name="skill"/> may act at the current <see cref="Next"/> step;
    /// otherwise returns a clear refusal naming the step that comes next.
    /// </summary>
    public static string? Gate(WorkflowState state, string skill)
    {
        var normalized = skill.ToLowerInvariant();
        if (normalized is "setup" or "context")
        {
            return null;
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
