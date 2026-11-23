using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;

namespace Hoshin.Cli.Hooks;

/// <summary>Shared workflow state reader, automatic-step advancer, and verdict reporter used by hook handlers.</summary>
internal sealed class WorkflowAdvancer(
    ITaskEvaluator tasks,
    ICheckRunner checks,
    IPublisher publisher,
    IGitWorktrees gitWorktrees,
    IGitQuery gitQuery,
    IGitStatus gitStatus,
    IFileSystem fileSystem,
    LedgerStore ledgerStore,
    WorkflowStateReader stateReader,
    IWorkflowEventBridge eventBridge)
{
    private readonly ITaskEvaluator _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    private readonly ICheckRunner _checks = checks ?? throw new ArgumentNullException(nameof(checks));
    private readonly IPublisher _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitStatus _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly LedgerStore _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
    private readonly WorkflowStateReader _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
    private readonly IWorkflowEventBridge _events = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));

    public IFileSystem FileSystem => _fs;

    public WorkflowStateReader StateReader => _stateReader;

    public WorkflowRepo? ResolveRepo(NormalizedHookEvent ev) =>
        WorkflowRepo.Find(ev.Cwd ?? Directory.GetCurrentDirectory(), _gitWorktrees, _gitQuery, _gitStatus, _fs, _ledgerStore);

    public WorkflowRepo? ResolveRepoAt(string directory, string? explicitChangeFolder = null) =>
        WorkflowRepo.Find(directory, _gitWorktrees, _gitQuery, _gitStatus, _fs, _ledgerStore, explicitChangeFolder);

    public WorkflowState? ReadState(WorkflowRepo repo, string? rolesRoot, out string? error)
    {
        error = null;
        try
        {
            return _stateReader.Read(repo, rolesRoot);
        }
        catch (CouldNotRunException e)
        {
            error = e.Message;
            return null;
        }
    }

    public (WorkflowVerdict Verdict, Step Next) JudgeAndAdvance(WorkflowRepo repo, string? rolesRoot, CurrentStep current, string? sessionId)
    {
        var state = ReadState(repo, rolesRoot, out _)!;
        var verdict = _tasks.EvaluateAndCommitTask(repo, state, current.Task!, current.Attempt);
        StopCounterStore.ResetAll(repo);
        return (verdict, Advance(repo, rolesRoot, sessionId));
    }

    public Step Advance(WorkflowRepo repo, string? rolesRoot, string? sessionId, string? invokedSkill = null)
    {
        while (true)
        {
            var state = ReadState(repo, rolesRoot, out var error);
            if (state is null)
            {
                return new Done(false, error ?? "no plan");
            }

            var step = WorkflowEngine.NextForSkill(state, invokedSkill);
            invokedSkill = null;
            switch (step)
            {
                case RunChecks:
                    _checks.RunChecks(repo, state);
                    continue;

                case Publish p:
                    var publish = _publisher.Publish(repo, state, p.Ref);
                    if (!publish.Ok)
                    {
                        var detail = string.IsNullOrWhiteSpace(publish.Error) ? "" : $": {publish.Error}";
                        return new Done(false, $"could not push to {p.Ref}{detail}");
                    }

                    continue;

                case Implement impl when _events.IsWorkspaceRepo(repo):
                    var task = state.Plan.Tasks.FirstOrDefault(t => t.Id == impl.Agent.Task);
                    var targetRepoName = !string.IsNullOrWhiteSpace(task?.Repo) ? task.Repo : repo.RepoKey;
                    var hasBaseline = state.RepoLedgers is not null && state.RepoLedgers.TryGetValue(targetRepoName, out var repoLedger)
                        ? repoLedger.WorkflowBaselines.Count > 0
                        : state.Ledger.WorkflowBaselines.Count > 0;
                    if (!hasBaseline)
                    {
                        _checks.RunBaseline(repo, state, targetRepoName);
                    }

                    _tasks.Issue(repo, state, step, sessionId);
                    return step;

                default:
                    _tasks.Issue(repo, state, step, sessionId);
                    return step;
            }
        }
    }

    public static string ReportVerdict(CurrentStep current, WorkflowVerdict verdict) => verdict.Passed
        ? $"{current.Task} attempt {current.Attempt} is accepted: test committed first ({verdict.Red}) and green at {WorkflowEngine.Short(verdict.Head)}.\n\n"
        : $"{current.Task} attempt {current.Attempt} is not accepted:\n- " + string.Join("\n- ", verdict.Reasons) + "\n\n";
}
