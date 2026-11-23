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
    TimeProvider timeProvider) : ITaskEvaluator
{
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitStatus _gitStatus = gitStatus ?? throw new ArgumentNullException(nameof(gitStatus));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitCommitter _gitCommitter = gitCommitter ?? throw new ArgumentNullException(nameof(gitCommitter));
    private readonly IAllowListedCommandRunner _runner = testRunner ?? throw new ArgumentNullException(nameof(testRunner));
    private readonly BuildLogWriter _buildLog = buildLogWriter ?? throw new ArgumentNullException(nameof(buildLogWriter));
    private readonly IWorkflowEventBridge _events = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
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
        var task = state.Plan.Tasks.First(t => t.Id == taskId);

        using var runLock = repo.AcquireRunLock();
        var liveLedger = repo.ReadLedger();
        if (liveLedger.WorkflowVerdicts.FirstOrDefault(v => v.Task == taskId && v.Attempt == attempt) is { } existing)
        {
            return existing;
        }

        var startHead = _gitQuery.CommitOf(repo.Root, "HEAD") ?? "";
        ClearActiveSubagent(repo);
        var now = _time.GetUtcNow();

        if (ValidatePreConditions(repo, liveLedger, task, attempt, startHead, now, out var changed) is { } earlyFail)
        {
            return RecordVerdict(repo, task, earlyFail, appendSyntheticRun: true);
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

        if (!verdict.Passed)
        {
            return RecordVerdict(repo, task, verdict, appendSyntheticRun: true);
        }

        var isWorkspace = _events.IsWorkspaceRepo(repo);
        if (isWorkspace)
        {
            verdict = CommitBuildLogEntry(repo, state, task, verdict);
        }

        RecordVerdict(repo, task, verdict, appendSyntheticRun: !isWorkspace);
        if (isWorkspace)
        {
            _events.TryFire(repo, "task.done", new Dictionary<string, string>(StringComparer.Ordinal) { ["task"] = task.Id });
        }

        return verdict;
    }

    private WorkflowVerdict CommitBuildLogEntry(WorkflowRepo repo, WorkflowState state, WorkflowTask task, WorkflowVerdict verdict)
    {
        var commits = string.IsNullOrEmpty(verdict.TestCommit)
            ? new[] { verdict.Head }
            : [verdict.TestCommit, verdict.Head];
        var logPath = _buildLog.AppendEntry(repo.ChangeFolder, repo.RepoKey, task.Id, commits, BuildLogWriter.RedValueFor(task));
        var logRel = Path.GetRelativePath(repo.Root, logPath).Replace('\\', '/');
        var logCommit = _gitCommitter.CommitPaths(
            repo.Root,
            [logRel],
            [$"docs({state.Plan.Scope}): log {task.Id} {state.Plan.Change}", "Generated-By: hoshin"]);
        return logCommit.Ok && !string.IsNullOrEmpty(logCommit.Sha)
            ? verdict with { Head = logCommit.Sha }
            : verdict;
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
        repo.UpdateLedger(ledger =>
        {
            if (ledger.WorkflowIssued.Any(x => x.Task == agent.Task && x.Attempt == agent.Attempt))
            {
                return ledger;
            }

            var firstForTask = ledger.WorkflowIssued.FirstOrDefault(x => x.Task == agent.Task);
            var taskStart = firstForTask?.StartHead ?? _gitQuery.CommitOf(repo.Root, "HEAD") ?? "";
            var snapshot = WorkingTreeSnapshot.Capture(repo, _gitStatus);
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
        WorkflowState state,
        StepKind kind,
        AgentTask agent,
        string? sessionId,
        bool hasTest,
        string[]? taskTestFiles)
    {
        var start = repo.ReadLedger().WorkflowIssued.First(x => x.Task == agent.Task).StartHead;
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
        RepoLedger liveLedger,
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

        var issued = liveLedger.WorkflowIssued.FirstOrDefault(x => x.Task == task.Id && x.Attempt == attempt);
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

    private static WorkflowVerdict RecordVerdict(WorkflowRepo repo, WorkflowTask task, WorkflowVerdict verdict, bool appendSyntheticRun)
    {
        repo.UpdateLedger(ledger =>
        {
            if (ledger.WorkflowVerdicts.Any(v => v.Task == verdict.Task && v.Attempt == verdict.Attempt))
            {
                return ledger;
            }

            var verdicts = new List<WorkflowVerdict>(ledger.WorkflowVerdicts) { verdict };
            var reds = ledger.Reds;
            if (task.Test is { Length: > 0 } && verdict.Passed && !string.IsNullOrEmpty(verdict.TestCommit))
            {
                reds = new List<RedRecord>(reds ?? [])
                {
                    new(
                        task.Id,
                        verdict.TestCommit,
                        task.Compile,
                        task.Test,
                        "failed",
                        1,
                        verdict.Red,
                        verdict.At.ToString("O")),
                };
            }

            var updated = ledger with { Verdicts = verdicts, Reds = reds };
            if (!appendSyntheticRun)
            {
                return updated;
            }

            var details = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["attempt"] = verdict.Attempt,
                ["red"] = verdict.Red,
            };
            if (!string.IsNullOrEmpty(verdict.TestCommit))
            {
                details["testCommit"] = verdict.TestCommit;
            }

            var run = new EventRecord(
                Event: "task.done",
                Repo: ledger.Repo,
                Key: verdict.Head,
                Subject: verdict.Task,
                Outcome: verdict.Passed ? "passed" : "failed",
                At: verdict.At.ToString("O"),
                Hooks: [],
                Problems: verdict.Reasons.ToList(),
                Failed: null,
                Details: details);

            return LedgerStore.AppendRun(updated, run);
        });
        return verdict;
    }
}
