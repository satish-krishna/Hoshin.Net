using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

/// <summary>Issues steps, tracks active subagents, and evaluates/commits task attempts.</summary>
public interface ITaskEvaluator
{
    void Issue(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId = null);

    bool TryRegisterActiveSubagent(WorkflowRepo repo, string taskId, int attempt, string agentId, out string? existingAgentId);

    void ClearActiveSubagent(WorkflowRepo repo, string? agentId = null);

    WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt);
}

/// <summary>Runs base-branch baseline checks and post-build checks.</summary>
public interface ICheckRunner
{
    WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state);

    WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state);
}

/// <summary>Records human approval or rejection decisions for a built change.</summary>
public interface IApprovalRecorder
{
    WorkflowApproval Answer(WorkflowRepo repo, WorkflowState state, bool approved, string note);
}

/// <summary>Pushes approved changes and opens or reuses pull requests.</summary>
public interface IPublisher
{
    WorkflowPublishResult Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = true);
}

/// <summary>Validates, pins, commits, and accepts change documents.</summary>
public interface IDocumentCommitter
{
    (IReadOnlyList<Problem> Problems, string? Commit) CommitDocument(WorkflowRepo repo, WorkflowState state, string name);

    (IReadOnlyList<Problem> Problems, string? Commit) CommitReview(WorkflowRepo repo, WorkflowState state);

    (IReadOnlyList<Problem> Problems, string? Commit) CommitFixDocuments(WorkflowRepo repo, WorkflowState state);

    string? AcceptDocument(WorkflowRepo repo, WorkflowState state, string name);
}
