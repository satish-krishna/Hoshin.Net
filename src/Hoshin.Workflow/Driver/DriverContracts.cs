using Hoshin.Workflow.Engine;

namespace Hoshin.Workflow.Driver;

/// <summary>
/// Drives one hoshin change for any host (a Microsoft Agent Framework workflow, a harness adapter, a test). The host asks
/// what is next, does that one thing, and reports back. Checks, Red/Green verdicts, commits and publishing happen inside
/// the driver. The driver keeps no state between calls: everything lives in git and <c>.git/hoshin</c>, so a host only
/// persists the <see cref="ChangeRef"/>.
/// </summary>
/// <remarks>
/// Not safe to call concurrently for the same change. A refused call throws <see cref="DriverRefusedException"/> and
/// changes nothing; a repository the driver cannot read throws <see cref="Common.CouldNotRunException"/>.
/// </remarks>
public interface IHoshinDriver
{
    /// <summary>Lists the open changes of the workspace or repository at <paramref name="workspace"/>.</summary>
    Task<IReadOnlyList<ChangeRef>> ListOpenAsync(WorkspaceRef workspace, CancellationToken cancellationToken = default);

    /// <summary>Starts a change (<c>change.start</c>): creates its folder, branch and, in a workspace, its copy.</summary>
    Task<ChangeRef> StartAsync(StartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns what the change needs next. Safe to call repeatedly; runs any automatic steps that are due.</summary>
    Task<Turn> NextAsync(ChangeRef change, CancellationToken cancellationToken = default);

    /// <summary>Reports that the agent finished <paramref name="work"/>; the driver judges and commits it.</summary>
    Task<Turn> SubmitAsync(ChangeRef change, WorkId work, CancellationToken cancellationToken = default);

    /// <summary>Applies a person's decision. Hosts call this only from a human channel, never from an agent tool.</summary>
    Task<Turn> DecideAsync(ChangeRef change, Decision decision, DecidedBy by, CancellationToken cancellationToken = default);
}

/// <summary>A folder inside a hoshin workspace or a single-repo repository.</summary>
public sealed record WorkspaceRef(string Path);

/// <summary>Everything a host needs to persist to come back to a change: the checkout that holds its folder, and the folder.</summary>
public sealed record ChangeRef(string CheckoutRoot, string ChangeFolder, string Change);

/// <summary>Which track a new change follows.</summary>
public enum Track
{
    /// <summary>Intent, spec and plan, each accepted by a person.</summary>
    Full,

    /// <summary>The light track: all three documents drafted together, at most one repository and three tasks.</summary>
    Fix,
}

/// <summary>A request to start a change. <paramref name="Name"/> becomes the change's slug (for example <c>add-login</c>).</summary>
public sealed record StartRequest(WorkspaceRef Workspace, string Name, Track Track = Track.Full);

/// <summary>The kind of agent work a step asks for; each maps to <c>skills/&lt;role&gt;/procedure.md</c>.</summary>
public enum Role
{
    Intent,
    Spec,
    Plan,
    Fix,
    Review,
    Implement,
}

/// <summary>Identifies one issued piece of work. A submit for work that is no longer current is refused.</summary>
public sealed record WorkId(StepKind Kind, string? Task, int Attempt, string Nonce);

/// <summary>Where an agent may work and what it may do there. Hosts build the agent's tools from this.</summary>
/// <param name="WorkingDirectory">The checkout the work happens in (for a multi-repo task, the task's repository).</param>
/// <param name="MayWrite">Repository-relative globs the agent may create or change.</param>
/// <param name="MayNotWrite">Repository-relative globs the agent must not touch, even when <paramref name="MayWrite"/> matches.</param>
/// <param name="MayRun">The exact commands the agent may run (a task's compile and test commands). Never git.</param>
public sealed record WorkScope(
    string WorkingDirectory,
    IReadOnlyList<string> MayWrite,
    IReadOnlyList<string> MayNotWrite,
    IReadOnlyList<IReadOnlyList<string>> MayRun);

/// <summary>Something the driver did on its own since the previous call, for hosts to show as progress.</summary>
public sealed record Happened(string What, string Detail);

/// <summary>What the change needs next. Hosts switch on the concrete type.</summary>
public abstract record Turn(IReadOnlyList<Happened> Happened);

/// <summary>Work for an agent: follow <paramref name="Instructions"/> with <paramref name="Brief"/>, inside <paramref name="Scope"/>.</summary>
public sealed record AgentWork(
    WorkId Id,
    Role Role,
    string Instructions,
    string Brief,
    WorkScope Scope,
    int Attempt,
    IReadOnlyList<string> Feedback,
    IReadOnlyList<Happened> Happened) : Turn(Happened)
{
    /// <summary>The instructions, the brief and any feedback, as one prompt.</summary>
    public string Prompt =>
        Feedback.Count == 0
            ? $"{Instructions.Trim()}\n\n---\n\n{Brief.Trim()}\n"
            : $"{Instructions.Trim()}\n\n---\n\n{Brief.Trim()}\n\n---\n\nYour previous attempt was not accepted:\n- {string.Join("\n- ", Feedback)}\n";
}

/// <summary>A decision only a person can make. <paramref name="Allowed"/> lists the decisions the change accepts now.</summary>
public sealed record HumanDecision(
    string Summary,
    IReadOnlyList<DecisionKind> Allowed,
    IReadOnlyList<Happened> Happened) : Turn(Happened);

/// <summary>The change is finished: published (<paramref name="Success"/>), or stopped with the reason in <paramref name="Outcome"/>.</summary>
public sealed record Finished(bool Success, string Outcome, IReadOnlyList<Happened> Happened) : Turn(Happened);

/// <summary>The closed set of decisions a person can make.</summary>
public enum DecisionKind
{
    Accept,
    Approve,
    Decline,
    Reject,
    Reopen,
}

/// <summary>A person's decision.</summary>
public abstract record Decision
{
    public abstract DecisionKind Kind { get; }
}

/// <summary>Accepts the document (or light-track fix documents) awaiting acceptance.</summary>
public sealed record Accept : Decision
{
    public override DecisionKind Kind => DecisionKind.Accept;
}

/// <summary>Approves the built and reviewed change, which publishes it.</summary>
public sealed record Approve(string Note) : Decision
{
    public override DecisionKind Kind => DecisionKind.Approve;
}

/// <summary>Declines approval; the reason becomes a fix task, then checks, review and approval run again.</summary>
public sealed record Decline(string Reason) : Decision
{
    public override DecisionKind Kind => DecisionKind.Decline;
}

/// <summary>Rejects the change's intent, which closes the change.</summary>
public sealed record Reject : Decision
{
    public override DecisionKind Kind => DecisionKind.Reject;
}

/// <summary>Reopens an accepted document as a draft. Null reopens the latest accepted one.</summary>
public sealed record Reopen(WorkflowDocument? Document = null) : Decision
{
    public override DecisionKind Kind => DecisionKind.Reopen;
}

/// <summary>Who made a decision, and through which channel; recorded with the approval.</summary>
public sealed record DecidedBy(string Who, string Channel);

/// <summary>Thrown when the driver refuses a call (stale work, a decision the change does not accept now). Nothing changed.</summary>
public sealed class DriverRefusedException : Exception
{
    public DriverRefusedException()
    {
    }

    public DriverRefusedException(string message)
        : base(message)
    {
    }

    public DriverRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
