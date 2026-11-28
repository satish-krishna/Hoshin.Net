using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Workflow.Driver;

/// <summary>What happened to a piece of finished agent work.</summary>
public abstract record SubmitOutcome;

/// <summary>Nothing was written for the work: the document is missing or unchanged since its last commit.</summary>
public sealed record SubmitUnchanged(string What) : SubmitOutcome;

/// <summary>The work was read and refused; <paramref name="Problems"/> say why. Nothing was committed.</summary>
public sealed record SubmitRefused(string What, IReadOnlyList<Problem> Problems) : SubmitOutcome;

/// <summary>The document work was committed as <paramref name="Commit"/>; <paramref name="Next"/> is the step after it.</summary>
public sealed record SubmitCommitted(string What, string Commit, Step Next) : SubmitOutcome;

/// <summary>An implement attempt was judged (Red/Green, protected paths, commits); <paramref name="Next"/> follows the verdict.</summary>
public sealed record SubmitJudged(WorkflowVerdict Verdict, Step Next) : SubmitOutcome;

/// <summary>
/// Judges and commits finished agent work for the current step: a drafted document, a review, the light-track fix
/// documents, or an implement attempt. Shared by the driver and the harness adapters, so every host commits work the
/// same way.
/// </summary>
public sealed class WorkSubmitter(IDocumentCommitter documents, StepAdvancer advancer)
{
    private readonly IDocumentCommitter _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    private readonly StepAdvancer _advancer = advancer ?? throw new ArgumentNullException(nameof(advancer));

    /// <summary>Validates, pins and commits the drafted <paramref name="document"/>, then advances.</summary>
    public SubmitOutcome SubmitDocument(WorkflowRepo repo, string? rolesRoot, WorkflowDocument document, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var state = ReadState(repo, rolesRoot);
        var name = document.ToWireName();
        var doc = state.Document(document);
        if (!doc.Exists || (!doc.Uncommitted && doc.InHead))
        {
            return new SubmitUnchanged(name);
        }

        var (problems, commit) = _documents.CommitDocument(repo, state, document);
        return commit is null
            ? new SubmitRefused(name, problems)
            : new SubmitCommitted(name, commit, _advancer.Advance(repo, rolesRoot, sessionId));
    }

    /// <summary>Validates and commits <c>build/review.md</c>, then advances.</summary>
    public SubmitOutcome SubmitReview(WorkflowRepo repo, string? rolesRoot, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var state = ReadState(repo, rolesRoot);
        var review = repo.DocumentStatus(Path.Combine("build", "review.md"));
        if (!review.Exists || (!review.Uncommitted && review.InHead))
        {
            return new SubmitUnchanged("review");
        }

        var (problems, commit) = _documents.CommitReview(repo, state);
        return commit is null
            ? new SubmitRefused("review", problems)
            : new SubmitCommitted("review", commit, _advancer.Advance(repo, rolesRoot, sessionId));
    }

    /// <summary>Validates and commits the light-track intent, spec and plan together, then advances.</summary>
    public SubmitOutcome SubmitFix(WorkflowRepo repo, string? rolesRoot, string? sessionId)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var state = ReadState(repo, rolesRoot);
        var anyDirty = new[] { state.Intent, state.Spec, state.PlanDoc }.Any(d => d.Exists && (d.Uncommitted || !d.InHead));
        if (!anyDirty)
        {
            return new SubmitUnchanged("fix");
        }

        var (problems, commit) = _documents.CommitFixDocuments(repo, state);
        return commit is null
            ? new SubmitRefused("fix", problems)
            : new SubmitCommitted("fix", commit, _advancer.Advance(repo, rolesRoot, sessionId));
    }

    /// <summary>Judges the implement attempt <paramref name="current"/> names, commits it when it passes, then advances.</summary>
    public SubmitJudged SubmitTask(WorkflowRepo repo, string? rolesRoot, CurrentStep current, string? sessionId, string? sessionRepo = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        var (verdict, next) = _advancer.JudgeAndAdvance(repo, rolesRoot, current, sessionId, sessionRepo);
        return new SubmitJudged(verdict, next);
    }

    private WorkflowState ReadState(WorkflowRepo repo, string? rolesRoot) =>
        _advancer.ReadState(repo, rolesRoot, out var error)
            ?? throw new CouldNotRunException(error ?? $"Could not read workflow state for {repo.Root} ({repo.ChangeFolder}).");
}
