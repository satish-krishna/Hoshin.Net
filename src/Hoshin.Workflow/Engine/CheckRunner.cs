using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Executes base-branch baseline checks and post-build checks via <see cref="IWorkflowEventBridge"/> (with standalone fallback).</summary>
public sealed class CheckRunner(
    IGitQuery gitQuery,
    IAllowListedCommandRunner checkRunner,
    IWorkflowEventBridge eventBridge,
    ITaskRepoResolver taskRepoResolver,
    TimeProvider timeProvider) : ICheckRunner
{
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IAllowListedCommandRunner _runner = checkRunner ?? throw new ArgumentNullException(nameof(checkRunner));
    private readonly IWorkflowEventBridge _events = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
    private readonly ITaskRepoResolver _taskRepos = taskRepoResolver ?? throw new ArgumentNullException(nameof(taskRepoResolver));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state, string? targetRepo = null)
    {
        var effectiveRepo = !string.IsNullOrWhiteSpace(targetRepo) ? targetRepo : repo.RepoKey;
        var target = _taskRepos.ResolveByName(repo, effectiveRepo);
        var sha = _gitQuery.CommitOf(target.Root, state.Plan.Base) ?? _gitQuery.CommitOf(target.Root, "HEAD") ?? state.Head;
        if (_events.TryFire(repo, "build.start", repoOverride: effectiveRepo) is { } outcome)
        {
            var eventBaseline = new WorkflowBaselineRun(
                sha,
                outcome.Outcome == "passed",
                WorkflowEventBridge.FormatOutcome(outcome),
                _time.GetUtcNow());
            target.UpdateLedger(l => l with { Baselines = new List<WorkflowBaselineRun>(l.WorkflowBaselines) { eventBaseline } });
            return eventBaseline;
        }

        var outputs = new List<string>();
        var passed = true;

        var env = AllowListedCommandRunner.CollectAdditionalEnvironment(
            state.Plan.EnvAllow,
            new Dictionary<string, string> { ["HOSHIN_BASELINE"] = "1" });
        foreach (var check in state.Plan.Checks)
        {
            var run = _runner.Run(target.Root, check, state.Plan.EffectiveCheckTimeout, env);
            passed &= run.Ok;
            outputs.Add($"$ {string.Join(' ', check)} -> {run.ExitCode}\n{run.Tail(20)}");
        }

        var baseline = new WorkflowBaselineRun(sha, passed, string.Join("\n", outputs), _time.GetUtcNow());
        target.UpdateLedger(l => l with { Baselines = new List<WorkflowBaselineRun>(l.WorkflowBaselines) { baseline } });
        return baseline;
    }

    public WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state)
    {
        var codeHead = state.EffectiveCodeHead;
        if (_events.IsWorkspaceRepo(repo))
        {
            var reposToCheck = state.Plan.Tasks
                .Select(t => t.Repo)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (reposToCheck.Count == 0)
            {
                reposToCheck.Add(repo.RepoKey);
            }

            var allPassed = true;
            var parts = new List<string>();
            foreach (var r in reposToCheck)
            {
                if (_events.TryFire(repo, "build.finish", repoOverride: r) is { } outcome)
                {
                    allPassed &= outcome.Outcome == "passed";
                    var formatted = WorkflowEventBridge.FormatOutcome(outcome);
                    if (!string.IsNullOrWhiteSpace(formatted))
                    {
                        parts.Add($"[{r}]\n{formatted}");
                    }
                }
            }

            var eventCheck = new WorkflowCheckRun(codeHead, allPassed, string.Join("\n", parts), _time.GetUtcNow());
            repo.UpdateLedger(l => l with { Checks = new List<WorkflowCheckRun>(l.WorkflowChecks) { eventCheck } });
            return eventCheck;
        }

        var outputs = new List<string>();
        var passed = true;
        var env = AllowListedCommandRunner.CollectAdditionalEnvironment(state.Plan.EnvAllow);

        foreach (var check in state.Plan.Checks)
        {
            var run = _runner.Run(repo.Root, check, state.Plan.EffectiveCheckTimeout, env);
            passed &= run.Ok;
            outputs.Add($"$ {string.Join(' ', check)} -> {run.ExitCode}\n{run.Tail(20)}");
        }

        var result = new WorkflowCheckRun(codeHead, passed, string.Join("\n", outputs), _time.GetUtcNow());
        repo.UpdateLedger(l => l with { Checks = new List<WorkflowCheckRun>(l.WorkflowChecks) { result } });
        return result;
    }
}

/// <summary>Records human approval or rejection decisions in the workflow ledger keyed by code head.</summary>
public sealed class ApprovalRecorder(TimeProvider timeProvider) : IApprovalRecorder
{
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public WorkflowApproval Answer(WorkflowRepo repo, WorkflowState state, bool approved, string note)
    {
        var codeHead = state.EffectiveCodeHead;
        var approval = new WorkflowApproval(codeHead, approved, note, _time.GetUtcNow());
        repo.UpdateLedger(l =>
            l.WorkflowApprovalRecords.Any(a => a.Head == codeHead)
                ? l
                : l with { WorkflowApprovals = new List<WorkflowApproval>(l.WorkflowApprovalRecords) { approval } });
        return approval;
    }
}
