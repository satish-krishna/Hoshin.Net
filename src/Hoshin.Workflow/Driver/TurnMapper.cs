using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Driver;

/// <summary>Pure mapping from the engine's <see cref="Step"/> to the host-facing <see cref="Turn"/>.</summary>
public static class TurnMapper
{
    private static readonly string[] FixDocuments = ["intent.md", "spec.md", "plan.md"];

    /// <summary>Paths no agent may write during any step, relative to the repository root.</summary>
    public static readonly IReadOnlyList<string> AlwaysProtected =
        [.. ProtectedPaths.ProtectedDirectories.Select(d => d + "/**"), MarkerConstants.FileName];

    /// <summary>
    /// Maps <paramref name="step"/> to a turn. <paramref name="current"/> is the issued step (<c>step.json</c>) the work id comes
    /// from; <paramref name="feedback"/> is why the previous attempt at the same work was refused.
    /// </summary>
    public static Turn ToTurn(
        Step step,
        WorkflowState state,
        CurrentStep? current,
        string? rolesRoot,
        IReadOnlyList<string> feedback,
        IReadOnlyList<Happened> happened)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(state);
        return step switch
        {
            WriteDocument w => DocumentWork(w, state, current, rolesRoot, feedback, happened),
            WriteFix f => FixWork(f, state, current, rolesRoot, feedback, happened),
            ReviewDocument r => ReviewWork(r, state, current, rolesRoot, feedback, happened),
            Implement i => TaskWork(i.Agent, [], state, current, feedback, happened),
            Retry r => TaskWork(r.Agent, r.Handoff, state, current, feedback, happened),
            AwaitAccept or AwaitAcceptFix or NeedsApproval => new HumanDecision(DecisionSummary(step), DecisionApplier.Allowed(state), happened),
            Done d => new Finished(d.Success, d.Outcome, happened),
            Handoff h => new Finished(false, h.Message, happened),
            _ => throw new InvalidOperationException($"The driver does not hand out {step.Kind}; it runs it."),
        };
    }

    /// <summary>The work id of the issued step <paramref name="current"/>.</summary>
    public static WorkId IdOf(CurrentStep current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var nonce = !string.IsNullOrEmpty(current.DispatchNonce)
            ? current.DispatchNonce
            : current.At.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return new WorkId(current.Kind, current.Task, current.Attempt, nonce);
    }

    public static Role RoleOf(WorkflowDocument document) => document switch
    {
        WorkflowDocument.Intent => Role.Intent,
        WorkflowDocument.Spec => Role.Spec,
        WorkflowDocument.Plan => Role.Plan,
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown workflow document."),
    };

    private static AgentWork DocumentWork(WriteDocument w, WorkflowState state, CurrentStep? current, string? rolesRoot, IReadOnlyList<string> feedback, IReadOnlyList<Happened> happened)
    {
        var role = RoleOf(w.Document);
        var scope = new WorkScope(state.RepoRoot, [Relative(state.RepoRoot, w.Path)], AlwaysProtected, []);
        return Work(role, WorkflowBriefs.LoadRoleProcedure(rolesRoot, w.Document.ToWireName()), w.Brief, scope, current, 1, feedback, happened);
    }

    private static AgentWork FixWork(WriteFix f, WorkflowState state, CurrentStep? current, string? rolesRoot, IReadOnlyList<string> feedback, IReadOnlyList<Happened> happened)
    {
        var docs = FixDocuments.Select(n => Relative(state.RepoRoot, Path.Combine(f.ChangeFolder, n))).ToList();
        var scope = new WorkScope(state.RepoRoot, docs, AlwaysProtected, []);
        return Work(Role.Fix, WorkflowBriefs.LoadRoleProcedure(rolesRoot, "fix"), f.Brief, scope, current, 1, feedback, happened);
    }

    private static AgentWork ReviewWork(ReviewDocument r, WorkflowState state, CurrentStep? current, string? rolesRoot, IReadOnlyList<string> feedback, IReadOnlyList<Happened> happened)
    {
        var scope = new WorkScope(state.RepoRoot, [Relative(state.RepoRoot, r.Path)], AlwaysProtected, []);
        return Work(Role.Review, WorkflowBriefs.LoadRoleProcedure(rolesRoot, "review"), r.Brief, scope, current, 1, feedback, happened);
    }

    private static AgentWork TaskWork(AgentTask agent, IReadOnlyList<string> handoff, WorkflowState state, CurrentStep? current, IReadOnlyList<string> feedback, IReadOnlyList<Happened> happened)
    {
        var task = state.Plan.Tasks.FirstOrDefault(t => t.Id == agent.Task);
        var root = task is null ? state.RepoRoot : state.RepositoryPathFor(task);
        var mayNotWrite = AlwaysProtected.ToList();
        var changeRel = Relative(root, state.ChangeFolder);
        if (!changeRel.StartsWith("..", StringComparison.Ordinal))
        {
            mayNotWrite.Add(changeRel + "/**");
        }

        var mayRun = new List<IReadOnlyList<string>>();
        if (task?.Compile is { Length: > 0 } compile)
        {
            mayRun.Add(compile);
        }

        if (task?.Test is { Length: > 0 } test)
        {
            mayRun.Add(test);
        }

        var scope = new WorkScope(root, ["**"], mayNotWrite, mayRun);
        return Work(Role.Implement, agent.Instructions, agent.Brief, scope, current, agent.Attempt, [.. handoff, .. feedback], happened);
    }

    private static AgentWork Work(Role role, string instructions, string brief, WorkScope scope, CurrentStep? current, int attempt, IReadOnlyList<string> feedback, IReadOnlyList<Happened> happened)
    {
        var id = current is null ? new WorkId(StepKindOf(role), null, attempt, "") : IdOf(current);
        return new AgentWork(id, role, instructions, brief, scope, attempt, feedback, happened);
    }

    private static StepKind StepKindOf(Role role) => role switch
    {
        Role.Fix => StepKind.Fix,
        Role.Review => StepKind.Review,
        Role.Implement => StepKind.Implement,
        _ => StepKind.WriteDocument,
    };

    private static string DecisionSummary(Step step) => step switch
    {
        AwaitAccept a => $"The {a.Document.ToWireName()} is drafted and committed at {a.Path} ({WorkflowEngine.Short(a.Head)}). Accept it, reopen it, or reject the change.",
        AwaitAcceptFix f => $"The light-track fix documents are drafted and committed in {f.ChangeFolder} ({WorkflowEngine.Short(f.Head)}). Accept them or reject the fix.",
        NeedsApproval n => $"The change is built, checked and reviewed at {WorkflowEngine.Short(n.Head)}: {n.Reason}. Approve it to publish, or decline with a reason.",
        _ => WorkflowEngine.Describe(step),
    };

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
