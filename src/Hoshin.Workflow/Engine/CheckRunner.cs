namespace Hoshin.Workflow.Engine;

/// <summary>Executes base-branch baseline checks and post-build checks via <see cref="IAllowListedCommandRunner"/>.</summary>
public sealed class CheckRunner(
    IAllowListedCommandRunner commandRunner,
    TimeProvider timeProvider) : ICheckRunner
{
    private readonly IAllowListedCommandRunner _runner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state)
    {
        var baseHead = repo.Git("rev-parse", state.Plan.Base);
        var sha = baseHead.Ok ? baseHead.Output.Trim() : state.Head;
        var outputs = new List<string>();
        var passed = true;

        var env = AllowListedCommandRunner.CollectAdditionalEnvironment(
            state.Plan.EnvAllow,
            new Dictionary<string, string> { ["HOSHIN_BASELINE"] = "1" });
        foreach (var check in state.Plan.Checks)
        {
            var run = _runner.Run(repo.Root, check, state.Plan.EffectiveCheckTimeout, env);
            passed &= run.Ok;
            outputs.Add($"$ {string.Join(' ', check)} -> {run.ExitCode}\n{run.Tail(20)}");
        }

        var baseline = new WorkflowBaselineRun(sha, passed, string.Join("\n", outputs), _time.GetUtcNow());
        repo.WithLedger(l =>
        {
            l.Baselines.Add(baseline);
            return 0;
        });
        return baseline;
    }

    public WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state)
    {
        var outputs = new List<string>();
        var passed = true;
        var env = AllowListedCommandRunner.CollectAdditionalEnvironment(state.Plan.EnvAllow);

        foreach (var check in state.Plan.Checks)
        {
            var run = _runner.Run(repo.Root, check, state.Plan.EffectiveCheckTimeout, env);
            passed &= run.Ok;
            outputs.Add($"$ {string.Join(' ', check)} -> {run.ExitCode}\n{run.Tail(20)}");
        }

        var codeHead = state.EffectiveCodeHead;
        var result = new WorkflowCheckRun(codeHead, passed, string.Join("\n", outputs), _time.GetUtcNow());
        repo.WithLedger(l =>
        {
            l.Checks.Add(result);
            return 0;
        });
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
        repo.WithLedger(l =>
        {
            if (!l.Approvals.Any(a => a.Head == codeHead))
            {
                l.Approvals.Add(approval);
            }

            return 0;
        });
        return approval;
    }
}
