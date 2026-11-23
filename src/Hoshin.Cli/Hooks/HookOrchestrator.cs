using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Thin dual-harness hook orchestrator dispatching <c>hoshin hook &lt;start|result|subagent-stop|stop&gt;</c>
/// to <see cref="StartHandler"/>, <see cref="ResultHandler"/>, and <see cref="StopHandler"/>.
/// </summary>
public sealed class HookOrchestrator
{
    private readonly StartHandler _start;
    private readonly ResultHandler _result;
    private readonly StopHandler _stop;

    public HookOrchestrator(
        IHookAdapter adapter,
        ITaskEvaluator taskEvaluator,
        ICheckRunner checkRunner,
        IApprovalRecorder approvals,
        IPublisher publisher,
        IDocumentCommitter documentCommitter,
        IGitWorktrees gitWorktrees,
        IGitQuery gitQuery,
        IGitStatus gitStatus,
        IFileSystem fileSystem,
        LedgerStore ledgerStore,
        WorkflowStateReader stateReader,
        IWorkflowEventBridge eventBridge,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(taskEvaluator);
        ArgumentNullException.ThrowIfNull(checkRunner);
        ArgumentNullException.ThrowIfNull(approvals);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(documentCommitter);
        ArgumentNullException.ThrowIfNull(gitWorktrees);
        ArgumentNullException.ThrowIfNull(gitQuery);
        ArgumentNullException.ThrowIfNull(gitStatus);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(ledgerStore);
        ArgumentNullException.ThrowIfNull(stateReader);
        ArgumentNullException.ThrowIfNull(eventBridge);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var advancer = new WorkflowAdvancer(taskEvaluator, checkRunner, publisher, gitWorktrees, gitQuery, gitStatus, fileSystem, ledgerStore, stateReader, eventBridge);
        _start = new StartHandler(adapter, taskEvaluator, approvals, documentCommitter, advancer, fileSystem, timeProvider);
        _result = new ResultHandler(adapter, taskEvaluator, advancer, fileSystem, timeProvider);
        _stop = new StopHandler(adapter, documentCommitter, advancer, fileSystem, timeProvider);
    }

    public (string? Output, string Decision) HandleStart(NormalizedHookEvent ev, string? rolesRoot) =>
        _start.Handle(ev, rolesRoot);

    public (string? Output, string Decision) HandleResult(NormalizedHookEvent ev, string? rolesRoot) =>
        _result.HandleResult(ev, rolesRoot);

    public (string? Output, string Decision) HandleSubagentStop(NormalizedHookEvent ev, string? rolesRoot) =>
        _result.HandleSubagentStop(ev, rolesRoot);

    public (string? Output, string Decision) HandleStop(NormalizedHookEvent ev, string? rolesRoot) =>
        _stop.Handle(ev, rolesRoot);
}
