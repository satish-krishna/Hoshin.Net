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
    WorkflowDocState PlanDoc)
{
    public static WorkflowState? Read(WorkflowRepo repo, string? skillsRoot = null)
    {
        var plan = repo.ReadPlan();
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
            repo.DocumentStatus("plan.md"));
    }

    public WorkflowDocState Document(string name) => name switch
    {
        "intent" => Intent,
        "spec" => Spec,
        "plan" => PlanDoc,
        _ => new WorkflowDocState(Path.Combine(ChangeFolder, $"{name}.md"), false, false, false, null, null),
    };
}

/// <summary>
/// The single pure state machine for the entire change lifecycle:
/// <c>WriteIntent → AwaitAccept(intent) → WriteSpec → AwaitAccept(spec) → WritePlan → AwaitAccept(plan) → Implement/Retry → RunChecks → NeedsApproval → Publish → Done</c>.
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
                return new AwaitAccept("plan", state.PlanDoc.Path, state.Head);
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

        var check = state.Ledger.Checks.LastOrDefault(c => c.Head == state.Head);
        if (check is null)
        {
            return new RunChecks(state.Head);
        }

        if (!check.Passed)
        {
            return new Done(false, $"checks failed at {Short(state.Head)}");
        }

        var approval = state.Ledger.Approvals.LastOrDefault(a => a.Head == state.Head);
        if (approval is null)
        {
            return new NeedsApproval(state.Head, $"every task passed and the checks are green at {Short(state.Head)}");
        }

        if (!approval.Approved)
        {
            return new Done(false, $"rejected at {Short(state.Head)}: {approval.Note}");
        }

        var targetRef = $"refs/heads/{state.Plan.EffectiveBranch}";
        return state.Ledger.Published.Any(p => p.Head == state.Head)
            ? new Done(true, $"published {Short(state.Head)} to {targetRef}")
            : new Publish(state.Head, targetRef);
    }

    private static Step? NextAuthoring(WorkflowState state)
    {
        foreach (var name in new[] { "intent", "spec", "plan" })
        {
            var doc = state.Document(name);
            if (!doc.Exists || doc.Uncommitted || !doc.InHead)
            {
                var brief = name switch
                {
                    "intent" => WorkflowBriefs.Intent(state),
                    "spec" => WorkflowBriefs.Spec(state),
                    _ => WorkflowBriefs.Plan(state),
                };
                return new WriteDocument(name, doc.Path, brief);
            }

            switch (doc.HeadStatus)
            {
                case "rejected":
                    return new Done(false, $"the {name} is rejected");
                case "accepted":
                    continue;
                default:
                    return new AwaitAccept(name, doc.Path, state.Head);
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
        WriteDocument w => w.Document,
        AwaitAccept or NeedsApproval => "accept",
        ReviewDocument => "review",
        WriteFix => "fix",
        Implement or Retry or RunChecks or Publish => "build",
        _ => null,
    };

    public static string Describe(Step step) => step switch
    {
        WriteDocument { Document: "intent" } => "writing the intent (`intent`)",
        WriteDocument { Document: "spec" } => "writing the spec from the accepted intent (`spec`)",
        WriteDocument { Document: "plan" } => "writing the plan from the accepted spec (`plan`)",
        AwaitAccept a => $"accepting the {a.Document}, which is committed at {a.Path} (`accept`)",
        ReviewDocument => "reviewing the built change (`review`)",
        WriteFix => "drafting the light-track fix documents (`fix`)",
        NeedsApproval => "approving the built change (`accept`)",
        Implement or Retry or RunChecks or Publish => "building the accepted plan (`build`)",
        Done d => $"nothing: the change is finished ({d.Outcome})",
        _ => step.Kind,
    };

    public static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
