using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Composite abstraction for workflow execution steps (prefer narrow role interfaces).</summary>
public interface IWorkflowRunner : ITaskEvaluator, ICheckRunner, IApprovalRecorder, IPublisher, IDocumentCommitter
{
}

/// <summary>
/// Thin composite facade implementing <see cref="IWorkflowRunner"/> by delegating to
/// <see cref="ITaskEvaluator"/>, <see cref="ICheckRunner"/>, <see cref="IApprovalRecorder"/>,
/// <see cref="IPublisher"/>, and <see cref="IDocumentCommitter"/>.
/// </summary>
public sealed class WorkflowRunner(
    ITaskEvaluator taskEvaluator,
    ICheckRunner checkRunner,
    IApprovalRecorder approvalRecorder,
    IPublisher publisher,
    IDocumentCommitter documentCommitter) : IWorkflowRunner
{
    private readonly ITaskEvaluator _tasks = taskEvaluator ?? throw new ArgumentNullException(nameof(taskEvaluator));
    private readonly ICheckRunner _checks = checkRunner ?? throw new ArgumentNullException(nameof(checkRunner));
    private readonly IApprovalRecorder _approvals = approvalRecorder ?? throw new ArgumentNullException(nameof(approvalRecorder));
    private readonly IPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IDocumentCommitter _documents = documentCommitter ?? throw new ArgumentNullException(nameof(documentCommitter));

    public WorkflowRunner(
        IAllowListedCommandRunner commandRunner,
        IPullRequestClient pullRequests,
        IntentReader intentReader,
        SpecReader specReader,
        PlanReader planReader,
        ReviewReader reviewReader,
        FrontMatterEdit frontMatterEdit,
        CrossChecker crossChecker,
        IFileSystem fileSystem,
        TimeProvider timeProvider)
        : this(
            new TaskEvaluator(commandRunner, timeProvider),
            new CheckRunner(commandRunner, timeProvider),
            new ApprovalRecorder(timeProvider),
            new Publisher(pullRequests, timeProvider),
            new DocumentCommitter(intentReader, specReader, planReader, reviewReader, frontMatterEdit, crossChecker, fileSystem, timeProvider))
    {
    }

    public void Issue(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId = null) =>
        _tasks.Issue(repo, state, step, sessionId);

    public bool TryRegisterActiveSubagent(WorkflowRepo repo, string taskId, int attempt, string agentId, out string? existingAgentId) =>
        _tasks.TryRegisterActiveSubagent(repo, taskId, attempt, agentId, out existingAgentId);

    public void ClearActiveSubagent(WorkflowRepo repo, string? agentId = null) =>
        _tasks.ClearActiveSubagent(repo, agentId);

    public WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt) =>
        _tasks.EvaluateAndCommitTask(repo, state, taskId, attempt);

    public WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state) =>
        _checks.RunBaseline(repo, state);

    public WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state) =>
        _checks.RunChecks(repo, state);

    public WorkflowApproval Answer(WorkflowRepo repo, WorkflowState state, bool approved, string note) =>
        _approvals.Answer(repo, state, approved, note);

    public WorkflowPublishResult Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = true) =>
        _publisher.Publish(repo, state, targetRef, openPullRequest);

    public (IReadOnlyList<Problem> Problems, string? Commit) CommitDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document) =>
        _documents.CommitDocument(repo, state, document);

    public (IReadOnlyList<Problem> Problems, string? Commit) CommitReview(WorkflowRepo repo, WorkflowState state) =>
        _documents.CommitReview(repo, state);

    public (IReadOnlyList<Problem> Problems, string? Commit) CommitFixDocuments(WorkflowRepo repo, WorkflowState state) =>
        _documents.CommitFixDocuments(repo, state);

    public string? AcceptDocument(WorkflowRepo repo, WorkflowState state, WorkflowDocument document) =>
        _documents.AcceptDocument(repo, state, document);

    public static string? TryAutoWrapBareJsonFrontMatter(string text) =>
        BareFrontMatterWrapper.TryAutoWrap(text);
}
