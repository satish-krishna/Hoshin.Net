using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Issues workflow steps, tracks active subagents, and evaluates/commits task attempts via <see cref="ITaskVerdictPolicy"/> and Git role interfaces.</summary>
public sealed class TaskEvaluator(
    IGitQuery gitQuery,
    IGitStatus gitStatus,
    IGitWorktrees gitWorktrees,
    IGitCommitter gitCommitter,
    IAllowListedCommandRunner testRunner,
    TimeProvider timeProvider) : ITaskEvaluator
{
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitStatus _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitCommitter _gitCommitter = gitCommitter ?? throw new ArgumentNullException(nameof(gitCommitter));
    private readonly IAllowListedCommandRunner _runner = testRunner ?? throw new ArgumentNullException(nameof(testRunner));
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
        var registered = repo.WithLedger(ledger =>
        {
            if (ledger.ActiveSubagent is { } active && !string.Equals(active.AgentId, agentId, StringComparison.Ordinal))
            {
                conflict = active.AgentId;
                return false;
            }

            ledger.ActiveSubagent = new ActiveSubagent(taskId, attempt, agentId, _time.GetUtcNow());
            return true;
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
        repo.WithLedger(ledger =>
        {
            if (agentId is null || ledger.ActiveSubagent?.AgentId == agentId)
            {
                ledger.ActiveSubagent = null;
            }

            return 0;
        });

        var step = repo.ReadStep();
        if (step is not null && (agentId is null || step.AgentId == agentId))
        {
            repo.WriteStep(step with { AgentId = null });
        }
    }

    public WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt)
    {
        var task = state.Plan.Tasks.First(t => t.Id == taskId);

        using var runLock = repo.AcquireRunLock();
        var liveLedger = repo.ReadLedger();
        if (liveLedger.Verdicts.FirstOrDefault(v => v.Task == taskId && v.Attempt == attempt) is { } existing)
        {
            return existing;
        }

        var startHead = _gitQuery.CommitOf(repo.Root, "HEAD") ?? "";
        ClearActiveSubagent(repo);
        var now = _time.GetUtcNow();

        if (ValidatePreConditions(repo, liveLedger, task, attempt, startHead, now, out var changed) is { } earlyFail)
        {
            return RecordVerdict(repo, earlyFail);
        }

        var policy = _verdictPolicies.First(p => p.AppliesTo(task));
        var verdict = policy.EvaluateAndCommit(new TaskEvaluationContext(
            repo,
            state,
            task,
            attempt,
            startHead,
            changed,
            _gitWorktrees,
            _gitCommitter,
            _runner,
            now));
        return RecordVerdict(repo, verdict);
    }

    private void IssueTaskStep(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId)
    {
        var agent = step is Implement i ? i.Agent : ((Retry)step).Agent;
        var task = state.Plan.Tasks.FirstOrDefault(t => t.Id == agent.Task);
        var hasTest = task?.Test is { Length: > 0 };
        var taskTestFiles = hasTest
            ? WorkflowRepo.ExtractTestFilesFromArgv(task!.Test!, repo.Root, state.Plan.EffectiveTestPatterns).ToArray()
            : null;

        RecordIssued(repo, agent);
        WriteIssuedStep(repo, state, step.Kind, agent, sessionId, hasTest, taskTestFiles);
    }

    private void RecordIssued(WorkflowRepo repo, AgentTask agent)
    {
        repo.WithLedger(ledger =>
        {
            if (!ledger.Issued.Any(x => x.Task == agent.Task && x.Attempt == agent.Attempt))
            {
                var firstForTask = ledger.Issued.FirstOrDefault(x => x.Task == agent.Task);
                var taskStart = firstForTask?.StartHead ?? _gitQuery.CommitOf(repo.Root, "HEAD") ?? "";
                var snapshot = WorkingTreeSnapshot.Capture(repo, _gitStatus);
                var preIssuePaths = firstForTask?.PreIssuePaths ?? snapshot.PathDigests;
                ledger.Issued.Add(new WorkflowIssued(
                    agent.Task,
                    agent.Attempt,
                    taskStart,
                    _time.GetUtcNow(),
                    snapshot.IndexHadStagedChanges,
                    preIssuePaths));
            }

            return 0;
        });
    }

    private void WriteIssuedStep(
        WorkflowRepo repo,
        WorkflowState state,
        StepKind kind,
        AgentTask agent,
        string? sessionId,
        bool hasTest,
        string[]? taskTestFiles)
    {
        var start = repo.ReadLedger().Issued.First(x => x.Task == agent.Task).StartHead;
        var previous = repo.ReadStep();
        var same = previous is not null && previous.Task == agent.Task && previous.Attempt == agent.Attempt;
        var activeSession = sessionId ?? previous?.SessionId ?? repo.ReadRunMarker()?.SessionId;
        var nonce = same && !string.IsNullOrEmpty(previous!.DispatchNonce)
            ? previous.DispatchNonce
            : $"<!-- hoshin:{agent.Task}:attempt-{agent.Attempt}:{Guid.NewGuid().ToString("N")[..8]} -->";

        var nextStep = same
            ? previous! with
            {
                SessionId = activeSession,
                ChangeFolder = repo.ChangeFolder,
                TestPatterns = state.Plan.EffectiveTestPatterns,
                TaskTestFiles = taskTestFiles,
                DispatchNonce = nonce,
                HasTest = hasTest,
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
                HasTest: hasTest);

        repo.WriteStep(nextStep);
    }

    private WorkflowVerdict? ValidatePreConditions(
        WorkflowRepo repo,
        WorkflowLedger liveLedger,
        WorkflowTask task,
        int attempt,
        string startHead,
        DateTimeOffset now,
        out List<WorkingTreeChange> changed)
    {
        changed = [];
        if (WorkflowHeadGuard.HasUnexpectedHeadMove(liveLedger, task.Id, startHead))
        {
            return new WorkflowVerdict(task.Id, attempt, startHead, false, "not observed: HEAD moved outside the workflow", [WorkflowHeadGuard.UnexpectedHeadMoveReason], now);
        }

        var issued = liveLedger.Issued.FirstOrDefault(x => x.Task == task.Id && x.Attempt == attempt);
        if (issued?.IndexHadStagedChanges == true)
        {
            return new WorkflowVerdict(task.Id, attempt, startHead, false, "not observed: index had staged changes", [WorkingTreeSnapshot.StagedIndexRefusalReason], now);
        }

        changed = WorkingTreeSnapshot.FilterTaskChanges(repo, WorkingTreeSnapshot.GetChanges(repo, _gitStatus), issued?.PreIssuePaths);
        var defaultRed = task.Test is { Length: > 0 } ? "not observed: no test commit" : NoTestPolicy.SkippedRedSummary;

        if (changed.Count == 0)
        {
            return new WorkflowVerdict(task.Id, attempt, startHead, false, defaultRed, [$"work: no working-tree changes were made for {task.Id}"], now);
        }

        var protectedHits = changed.Where(c => ProtectedPaths.IsProtectedDuringBuild(c.Path, repo.Root, repo.ChangeFolder)).Select(c => c.Path).ToList();
        if (protectedHits.Count > 0)
        {
            return new WorkflowVerdict(task.Id, attempt, startHead, false, defaultRed, [$"protected: modified protected workflow files: {string.Join(", ", protectedHits)}"], now);
        }

        return null;
    }

    private static string? StepTargetName(Step step) => step switch
    {
        WriteDocument w => w.Document.ToWireName(),
        AwaitAccept a => a.Document.ToWireName(),
        ReviewDocument => "review",
        WriteFix => "fix",
        _ => null,
    };

    private static WorkflowVerdict RecordVerdict(WorkflowRepo repo, WorkflowVerdict verdict)
    {
        repo.WithLedger(ledger =>
        {
            if (!ledger.Verdicts.Any(v => v.Task == verdict.Task && v.Attempt == verdict.Attempt))
            {
                ledger.Verdicts.Add(verdict);
            }

            return 0;
        });
        return verdict;
    }
}
