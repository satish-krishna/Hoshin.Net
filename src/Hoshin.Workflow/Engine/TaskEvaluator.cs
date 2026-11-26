using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Issues workflow steps, tracks active subagents, and evaluates/commits task attempts via <see cref="ITaskVerdictPolicy"/> and Git role interfaces.</summary>
public sealed class TaskEvaluator(
    IGitQuery gitQuery,
    IGitStatus gitStatus,
    IGitWorktrees gitWorktrees,
    IGitCommitter gitCommitter,
    IAllowListedCommandRunner testRunner,
    BuildLogWriter buildLogWriter,
    IWorkflowEventBridge eventBridge,
    ITaskRepoResolver taskRepoResolver,
    TimeProvider timeProvider) : ITaskEvaluator
{
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitStatus _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitCommitter _gitCommitter = gitCommitter ?? throw new ArgumentNullException(nameof(gitCommitter));
    private readonly IAllowListedCommandRunner _runner = testRunner ?? throw new ArgumentNullException(nameof(testRunner));
    private readonly BuildLogWriter _buildLog = buildLogWriter ?? throw new ArgumentNullException(nameof(buildLogWriter));
    private readonly IWorkflowEventBridge _events = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
    private readonly ITaskRepoResolver _taskRepos = taskRepoResolver ?? throw new ArgumentNullException(nameof(taskRepoResolver));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly IReadOnlyList<ITaskVerdictPolicy> _verdictPolicies = [new NoTestPolicy(), new RedGreenPolicy()];

    public void Issue(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId = null)
    {
        if (step is Implement or Retry)
        {
            IssueTaskStep(repo, state, step, sessionId);
            return;
        }

        var document = StepTargetName(step);
        var activeSession = sessionId ?? repo.ReadRunMarker()?.SessionId;
        repo.WriteStep(new CurrentStep(
            step.Kind,
            document,
            0,
            null,
            _time.GetUtcNow(),
            AgentId: null,
            SessionId: activeSession,
            ChangeFolder: repo.ChangeFolder));
    }

    public bool TryRegisterActiveSubagent(WorkflowRepo repo, string taskId, int attempt, string agentId, out string? existingAgentId)
    {
        string? conflict = null;
        var registered = false;
        repo.UpdateLedger(ledger =>
        {
            if (ledger.ActiveSubagent is { } active && !string.Equals(active.AgentId, agentId, StringComparison.Ordinal))
            {
                conflict = active.AgentId;
                return ledger;
            }

            registered = true;
            return ledger with { ActiveSubagent = new ActiveSubagent(taskId, attempt, agentId, _time.GetUtcNow()) };
        });

        existingAgentId = conflict;
        if (registered && repo.ReadStep() is { } step)
        {
            repo.WriteStep(step with { AgentId = agentId });
        }

        return registered;
    }

    public void ClearActiveSubagent(WorkflowRepo repo, string? agentId = null)
    {
        repo.UpdateLedger(ledger =>
            agentId is null || ledger.ActiveSubagent?.AgentId == agentId
                ? ledger with { ActiveSubagent = null }
                : ledger);

        var step = repo.ReadStep();
        if (step is not null && (agentId is null || step.AgentId == agentId))
        {
            repo.WriteStep(step with { AgentId = null });
        }
    }

    public WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt)
    {
        var task = state.Plan.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task is null && PostBuildFixPolicy.IsFixTaskId(taskId))
        {
            return EvaluateAndCommitFixTask(repo, state, taskId, attempt);
        }

        task ??= state.Plan.Tasks.First(t => t.Id == taskId);
        var targetRepo = _taskRepos.Resolve(repo, task);

        using var runLock = repo.AcquireRunLock();
        var liveLedger = targetRepo.ReadLedger();
        if (liveLedger.WorkflowVerdicts.FirstOrDefault(v => v.Task == taskId && v.Attempt == attempt) is { } existing)
        {
            return existing;
        }

        var startHead = _gitQuery.CommitOf(targetRepo.Root, "HEAD") ?? "";
        ClearActiveSubagent(repo);
        var now = _time.GetUtcNow();
        var defaultRed = task.Test is { Length: > 0 } ? "not observed: no test commit" : NoTestPolicy.SkippedRedSummary;

        if (ValidatePreConditions(targetRepo, liveLedger, task.Id, defaultRed, attempt, startHead, now, out var changed) is { } earlyFail)
        {
            return RecordVerdict(targetRepo, task, earlyFail, appendSyntheticRun: true);
        }

        var policy = _verdictPolicies.First(p => p.AppliesTo(task));
        var verdict = policy.EvaluateAndCommit(new TaskEvaluationContext(
            targetRepo,
            state,
            task,
            attempt,
            startHead,
            changed,
            _gitWorktrees,
            _gitCommitter,
            _runner,
            now));

        if (!verdict.Passed)
        {
            return RecordVerdict(targetRepo, task, verdict, appendSyntheticRun: true);
        }

        var isWorkspace = _events.IsWorkspaceRepo(repo);
        if (isWorkspace)
        {
            verdict = CommitBuildLogEntry(repo, targetRepo, state, task, verdict);
        }

        RecordVerdict(targetRepo, task, verdict, appendSyntheticRun: !isWorkspace);
        if (isWorkspace)
        {
            _events.TryFire(
                repo,
                "task.done",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = task.Id },
                repoOverride: targetRepo.RepoKey);
        }

        return verdict;
    }

    private WorkflowVerdict CommitBuildLogEntry(
        WorkflowRepo masterRepo,
        WorkflowRepo targetRepo,
        WorkflowState state,
        WorkflowTask task,
        WorkflowVerdict verdict)
    {
        var commits = string.IsNullOrEmpty(verdict.TestCommit)
            ? new[] { verdict.Head }
            : [verdict.TestCommit, verdict.Head];
        var logPath = _buildLog.AppendEntry(masterRepo.ChangeFolder, targetRepo.RepoKey, task.Id, commits, BuildLogWriter.RedValueFor(task));
        var logRel = Path.GetRelativePath(masterRepo.Root, logPath).Replace('\\', '/');
        var logCommit = _gitCommitter.CommitPaths(
            masterRepo.Root,
            [logRel],
            [$"docs({state.Plan.Scope}): log {task.Id} {state.Plan.Change}", "Generated-By: hoshin"]);
        var sameRepo = string.Equals(masterRepo.Root, targetRepo.Root, StringComparison.OrdinalIgnoreCase);
        return sameRepo && logCommit.Ok && !string.IsNullOrEmpty(logCommit.Sha)
            ? verdict with { Head = logCommit.Sha }
            : verdict;
    }

    private WorkflowVerdict EvaluateAndCommitFixTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt)
    {
        var targetName = repo.ReadStep()?.TargetRepo ?? state.SessionRepo;
        var targetRepo = string.IsNullOrWhiteSpace(targetName) ? repo : _taskRepos.ResolveByName(repo, targetName);

        using var runLock = repo.AcquireRunLock();
        var liveLedger = targetRepo.ReadLedger();
        if (liveLedger.WorkflowVerdicts.FirstOrDefault(v => v.Task == taskId && v.Attempt == attempt) is { } existing)
        {
            return existing;
        }

        var startHead = _gitQuery.CommitOf(targetRepo.Root, "HEAD") ?? "";
        ClearActiveSubagent(repo);
        var now = _time.GetUtcNow();

        if (ValidatePreConditions(targetRepo, liveLedger, taskId, PostBuildFixPolicy.SkippedRedSummary, attempt, startHead, now, out var changed) is { } earlyFail)
        {
            return RecordVerdict(targetRepo, null, earlyFail, appendSyntheticRun: false);
        }

        var verdict = PostBuildFixPolicy.EvaluateAndCommit(targetRepo, state, taskId, attempt, startHead, changed, _gitCommitter, now);
        return RecordVerdict(targetRepo, null, verdict, appendSyntheticRun: false);
    }

    private void IssueTaskStep(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId)
    {
        var agent = step is Implement i ? i.Agent : ((Retry)step).Agent;
        var task = state.Plan.Tasks.FirstOrDefault(t => t.Id == agent.Task);
        var isFix = task is null && PostBuildFixPolicy.IsFixTaskId(agent.Task);
        var targetRepoName = task?.Repo ?? agent.TargetRepo;
        var targetRepo = task is not null
            ? _taskRepos.Resolve(repo, task)
            : (!string.IsNullOrWhiteSpace(targetRepoName) ? _taskRepos.ResolveByName(repo, targetRepoName) : repo);
        var hasTest = task?.Test is { Length: > 0 };
        var taskTestFiles = hasTest
            ? WorkflowRepo.ExtractTestFilesFromArgv(task!.Test!, targetRepo.Root, state.Plan.EffectiveTestPatterns).ToArray()
            : null;

        RecordIssued(targetRepo, agent);
        WriteIssuedStep(repo, targetRepo, state, step.Kind, agent, sessionId, hasTest, taskTestFiles, targetRepoName, isFix);
    }

    private void RecordIssued(WorkflowRepo targetRepo, AgentTask agent)
    {
        targetRepo.UpdateLedger(ledger =>
        {
            if (ledger.WorkflowIssued.Any(x => x.Task == agent.Task && x.Attempt == agent.Attempt))
            {
                return ledger;
            }

            var firstForTask = ledger.WorkflowIssued.FirstOrDefault(x => x.Task == agent.Task);
            var taskStart = firstForTask?.StartHead ?? _gitQuery.CommitOf(targetRepo.Root, "HEAD") ?? "";
            var snapshot = WorkingTreeSnapshot.Capture(targetRepo, _gitStatus);
            var preIssuePaths = firstForTask?.PreIssuePaths ?? snapshot.PathDigests;
            var updatedIssued = new List<WorkflowIssued>(ledger.WorkflowIssued)
            {
                new(
                    agent.Task,
                    agent.Attempt,
                    taskStart,
                    _time.GetUtcNow(),
                    snapshot.IndexHadStagedChanges,
                    preIssuePaths),
            };
            return ledger with { Issued = updatedIssued };
        });
    }

    private void WriteIssuedStep(
        WorkflowRepo repo,
        WorkflowRepo targetRepo,
        WorkflowState state,
        StepKind kind,
        AgentTask agent,
        string? sessionId,
        bool hasTest,
        string[]? taskTestFiles,
        string? targetRepoName,
        bool isFix)
    {
        var start = targetRepo.ReadLedger().WorkflowIssued.First(x => x.Task == agent.Task).StartHead;
        var previous = repo.ReadStep();
        var same = previous is not null && previous.Task == agent.Task && previous.Attempt == agent.Attempt;
        var activeSession = sessionId ?? previous?.SessionId ?? repo.ReadRunMarker()?.SessionId;
        var nonce = same && !string.IsNullOrEmpty(previous!.DispatchNonce)
            ? previous.DispatchNonce
            : $"<!-- hoshin:{agent.Task}:attempt-{agent.Attempt}:{Guid.NewGuid().ToString("N")[..8]} -->";
        var fixInstructions = isFix ? (state.BuildInstructions ?? previous?.FixInstructions) : null;

        var nextStep = same
            ? previous! with
            {
                SessionId = activeSession,
                ChangeFolder = repo.ChangeFolder,
                TestPatterns = state.Plan.EffectiveTestPatterns,
                TaskTestFiles = taskTestFiles,
                DispatchNonce = nonce,
                HasTest = hasTest,
                TargetRepo = targetRepoName,
                IsFix = isFix,
                FixInstructions = fixInstructions,
            }
            : new CurrentStep(
                kind,
                agent.Task,
                agent.Attempt,
                start,
                _time.GetUtcNow(),
                AgentId: null,
                SessionId: activeSession,
                ChangeFolder: repo.ChangeFolder,
                TestPatterns: state.Plan.EffectiveTestPatterns,
                TaskTestFiles: taskTestFiles,
                DispatchNonce: nonce,
                HasTest: hasTest,
                TargetRepo: targetRepoName,
                IsFix: isFix,
                FixInstructions: fixInstructions);

        repo.WriteStep(nextStep);
    }

    private WorkflowVerdict? ValidatePreConditions(
        WorkflowRepo repo,
        RepoLedger liveLedger,
        string taskId,
        string defaultRed,
        int attempt,
        string startHead,
        DateTimeOffset now,
        out List<WorkingTreeChange> changed)
    {
        changed = [];
        if (WorkflowHeadGuard.HasUnexpectedHeadMove(liveLedger, taskId, startHead))
        {
            return new WorkflowVerdict(taskId, attempt, startHead, false, "not observed: HEAD moved outside the workflow", [WorkflowHeadGuard.UnexpectedHeadMoveReason], now);
        }

        var issued = liveLedger.WorkflowIssued.FirstOrDefault(x => x.Task == taskId && x.Attempt == attempt);
        if (issued?.IndexHadStagedChanges == true)
        {
            return new WorkflowVerdict(taskId, attempt, startHead, false, "not observed: index had staged changes", [WorkingTreeSnapshot.StagedIndexRefusalReason], now);
        }

        changed = WorkingTreeSnapshot.FilterTaskChanges(repo, WorkingTreeSnapshot.GetChanges(repo, _gitStatus), issued?.PreIssuePaths);

        if (changed.Count == 0)
        {
            return new WorkflowVerdict(taskId, attempt, startHead, false, defaultRed, [$"work: no working-tree changes were made for {taskId}"], now);
        }

        var protectedHits = changed.Where(c => ProtectedPaths.IsProtectedDuringBuild(c.Path, repo.Root, repo.ChangeFolder)).Select(c => c.Path).ToList();
        if (protectedHits.Count > 0)
        {
            return new WorkflowVerdict(taskId, attempt, startHead, false, defaultRed, [$"protected: modified protected workflow files: {string.Join(", ", protectedHits)}"], now);
        }

        return null;
    }

    private static string? StepTargetName(Step step) => step switch
    {
        WriteDocument w => w.Document.ToWireName(),
        AwaitAccept a => a.Document.ToWireName(),
        AwaitAcceptFix f => f.NextDocument.ToWireName(),
        ReviewDocument => "review",
        WriteFix => "fix",
        _ => null,
    };

    private static WorkflowVerdict RecordVerdict(WorkflowRepo repo, WorkflowTask? task, WorkflowVerdict verdict, bool appendSyntheticRun) =>
        TaskVerdictRecorder.Record(repo, task, verdict, appendSyntheticRun);
}
