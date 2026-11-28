using Hoshin.Workflow.Engine;

namespace Hoshin.Workflow.Driver;

/// <summary>What a person's decision did. <paramref name="Applied"/> is false when nothing could be recorded or committed.</summary>
public sealed record DecisionOutcome(DecisionKind Kind, bool Applied, string Summary, IReadOnlyList<string> Commits)
{
    public static DecisionOutcome NotApplied(DecisionKind kind, string summary) => new(kind, false, summary, []);
}

/// <summary>
/// Applies a person's decision to the change: accept a document or the fix documents, approve, decline, reject the
/// intent, or reopen an accepted document. It checks the change accepts the decision now (through
/// <see cref="WorkflowEngine.Gate"/>) and never decides who may make it: that is the host's job.
/// </summary>
public sealed class DecisionApplier(IApprovalRecorder approvals, IDocumentCommitter documents)
{
    private readonly IApprovalRecorder _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
    private readonly IDocumentCommitter _documents = documents ?? throw new ArgumentNullException(nameof(documents));

    /// <summary>The skill name whose gate decides whether <paramref name="kind"/> is allowed now.</summary>
    public static string GateSkill(DecisionKind kind) => kind switch
    {
        DecisionKind.Accept or DecisionKind.Approve => "accept",
        DecisionKind.Decline => "decline",
        DecisionKind.Reject => "reject",
        DecisionKind.Reopen => "reopen",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown decision."),
    };

    /// <summary>The decisions the change accepts in <paramref name="state"/>, for a host to offer.</summary>
    public static IReadOnlyList<DecisionKind> Allowed(WorkflowState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var next = WorkflowEngine.Next(state);
        return Enum.GetValues<DecisionKind>()
            .Where(k => FitsStep(k, next) && WorkflowEngine.Gate(state, GateSkill(k)) is null)
            .ToList();
    }

    /// <summary>Why <paramref name="kind"/> is refused in <paramref name="state"/>, or null when it is allowed.</summary>
    public static string? Refusal(WorkflowState state, DecisionKind kind)
    {
        ArgumentNullException.ThrowIfNull(state);
        var next = WorkflowEngine.Next(state);
        if (!FitsStep(kind, next))
        {
            return $"`{kind.ToString().ToLowerInvariant()}` does not apply now. The next step is {WorkflowEngine.Describe(next)}.";
        }

        return WorkflowEngine.Gate(state, GateSkill(kind));
    }

    /// <summary>Accepts what is awaiting acceptance, or approves when the change awaits approval.</summary>
    public DecisionOutcome Accept(WorkflowRepo repo, WorkflowState state, string approvalNote)
    {
        ArgumentNullException.ThrowIfNull(state);
        switch (WorkflowEngine.Next(state))
        {
            case NeedsApproval:
                return Approve(repo, state, approvalNote);
            case AwaitAccept a:
                var commit = _documents.AcceptDocument(repo, state, a.Document);
                return commit is null
                    ? DecisionOutcome.NotApplied(DecisionKind.Accept, "The acceptance could not be committed.")
                    : new DecisionOutcome(DecisionKind.Accept, true, $"The {a.Document.ToWireName()} is accepted, committed as {WorkflowEngine.Short(commit)}.", [commit]);
            case AwaitAcceptFix:
                var commits = _documents.AcceptFixDocuments(repo, state);
                return commits.Count == 0
                    ? DecisionOutcome.NotApplied(DecisionKind.Accept, "The light-track fix acceptance could not be committed.")
                    : new DecisionOutcome(DecisionKind.Accept, true, $"The light-track fix documents are accepted ({string.Join(", ", commits.Select(WorkflowEngine.Short))}).", commits);
            default:
                return DecisionOutcome.NotApplied(DecisionKind.Accept, "Nothing is awaiting acceptance.");
        }
    }

    /// <summary>Records the person's approval of the built change.</summary>
    public DecisionOutcome Approve(WorkflowRepo repo, WorkflowState state, string note)
    {
        _approvals.Answer(repo, state, approved: true, note);
        return new DecisionOutcome(DecisionKind.Approve, true, "The engineer's approval is recorded.", []);
    }

    /// <summary>Records a declined approval; <paramref name="reason"/> becomes the fix the next build makes.</summary>
    public DecisionOutcome Decline(WorkflowRepo repo, WorkflowState state, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return DecisionOutcome.NotApplied(DecisionKind.Decline, "Declining approval requires a reason.");
        }

        _approvals.Answer(repo, state, approved: false, reason.Trim());
        return new DecisionOutcome(DecisionKind.Decline, true, $"The engineer declined approval: {reason.Trim()}.", []);
    }

    /// <summary>Rejects the intent, which closes the change.</summary>
    public DecisionOutcome Reject(WorkflowRepo repo, WorkflowState state)
    {
        var commit = _documents.RejectIntent(repo, state);
        return commit is null
            ? DecisionOutcome.NotApplied(DecisionKind.Reject, "The rejection could not be committed.")
            : new DecisionOutcome(DecisionKind.Reject, true, $"The intent is rejected, committed as {WorkflowEngine.Short(commit)}.", [commit]);
    }

    /// <summary>Reopens <paramref name="document"/>, or the latest accepted document when it is null.</summary>
    public DecisionOutcome Reopen(WorkflowRepo repo, WorkflowState state, WorkflowDocument? document)
    {
        var target = ReopenTarget(state, document);
        if (target is null)
        {
            return DecisionOutcome.NotApplied(DecisionKind.Reopen, "Specify an accepted document to reopen (`intent`, `spec`, or `plan`).");
        }

        var name = target.Value.ToWireName();
        var commit = _documents.ReopenDocument(repo, state, target.Value);
        return commit is null
            ? DecisionOutcome.NotApplied(DecisionKind.Reopen, $"Could not reopen {name}.")
            : new DecisionOutcome(DecisionKind.Reopen, true, $"The {name} is reopened (`status: draft`), committed as {WorkflowEngine.Short(commit)}.", [commit]);
    }

    /// <summary>The document a reopen applies to: the one named when it is accepted, else the latest accepted one.</summary>
    public static WorkflowDocument? ReopenTarget(WorkflowState state, WorkflowDocument? document)
    {
        ArgumentNullException.ThrowIfNull(state);
        static bool Accepted(WorkflowDocState d) => d is { InHead: true, HeadStatus: "accepted" };
        if (document is { } named)
        {
            return Accepted(state.Document(named)) ? named : null;
        }

        foreach (var candidate in new[] { WorkflowDocument.Plan, WorkflowDocument.Spec, WorkflowDocument.Intent })
        {
            if (Accepted(state.Document(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool FitsStep(DecisionKind kind, Step next) => kind switch
    {
        DecisionKind.Accept => next is AwaitAccept or AwaitAcceptFix,
        DecisionKind.Approve or DecisionKind.Decline => next is NeedsApproval,
        DecisionKind.Reject or DecisionKind.Reopen => true,
        _ => false,
    };
}
