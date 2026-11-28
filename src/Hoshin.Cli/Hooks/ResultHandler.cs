using Hoshin.Workflow.Common;
using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>Handles <c>hoshin hook result</c> (subagent launch/completion) and <c>hoshin hook subagent-stop</c>.</summary>
internal sealed class ResultHandler(
    IHookAdapter adapter,
    IHookRunResolver runResolver,
    ITaskEvaluator tasks,
    WorkSubmitter submitter,
    IFileSystem fileSystem,
    TimeProvider timeProvider)
{
    private readonly IHookAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IHookRunResolver _runResolver = runResolver ?? throw new ArgumentNullException(nameof(runResolver));
    private readonly ITaskEvaluator _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    private readonly WorkSubmitter _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public (string? Output, string Decision) HandleResult(NormalizedHookEvent ev, string? rolesRoot)
    {
        if (!TryResolveActiveBuildStep(ev, out var resolved, out var current, out var skipReason))
        {
            return (_adapter.EmptyOutput(ev.Harness), skipReason);
        }

        var repo = resolved.WorkflowRepo;
        if (ev.IsAntigravity && ev.IsTaskManageTool)
        {
            return (_adapter.EmptyOutput(ev.Harness), $"observed {ev.ToolName}");
        }

        if (!HasMatchingDispatchNonce(current, ev))
        {
            return (_adapter.EmptyOutput(ev.Harness), "subagent prompt missing dispatch nonce");
        }

        if (ev.IsAntigravity)
        {
            return HandleAntigravityAsyncLaunch(ev, repo, current);
        }

        if (ev.IsAsyncLaunch)
        {
            return HandleClaudeAsyncLaunch(ev, repo, current);
        }

        // Foreground subagent completed: judge attempt immediately and hand out next step.
        var (verdict, step) = Judge(repo, rolesRoot, current, ev.SessionId, resolved.ProductRepoName);
        return (
            _adapter.ContextOutput(ev, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: StepAdvancer.ReportVerdict(current, verdict))),
            $"verdict {(verdict.Passed ? "passed" : "failed")}; next {step.Kind}");
    }

    private bool TryResolveActiveBuildStep(
        NormalizedHookEvent ev,
        out ResolvedHookRun resolved,
        out CurrentStep current,
        out string skipReason)
    {
        resolved = null!;
        current = null!;
        skipReason = "no run";

        var found = _runResolver.Resolve(ev);
        if (found is null || !_fs.FileExists(found.WorkflowRepo.RunMarkerPath))
        {
            return false;
        }

        resolved = found;
        var repo = found.WorkflowRepo;
        if (!HookSessionScope.IsRunSession(repo, ev, _time.GetUtcNow(), out var marker, out var step)
            || !HookSessionScope.IsParentRunSession(marker!, ev))
        {
            skipReason = "not the run session";
            return false;
        }

        if (step is not { Kind: StepKind.Implement or StepKind.Retry, Task: not null })
        {
            skipReason = $"step is {step?.Kind.ToString() ?? "none"}";
            return false;
        }

        current = step;
        return true;
    }

    public (string? Output, string Decision) HandleSubagentStop(NormalizedHookEvent ev, string? rolesRoot)
    {
        var resolved = _runResolver.Resolve(ev);
        var repo = resolved?.WorkflowRepo;
        if (repo is null || !_fs.FileExists(repo.RunMarkerPath))
        {
            return (_adapter.EmptyOutput(ev.Harness), "no run");
        }

        if (!HookSessionScope.IsRunSession(repo, ev, _time.GetUtcNow(), out _, out var current))
        {
            return (_adapter.EmptyOutput(ev.Harness), "not the run session");
        }

        var active = repo.ReadLedger().ActiveSubagent;
        var expectedId = current?.AgentId ?? active?.AgentId;
        var stoppedId = ev.StoppedAgentId ?? ev.SessionId;

        if (current is null || expectedId is null || !string.Equals(expectedId, stoppedId, StringComparison.Ordinal))
        {
            return (_adapter.EmptyOutput(ev.Harness), "not the step's subagent");
        }

        var (verdict, step) = Judge(repo, rolesRoot, current, repo.ReadRunMarker()?.SessionId, resolved!.ProductRepoName);
        TurnState.QueuePendingReport(repo, StepAdvancer.ReportVerdict(current, verdict), _fs);
        return (_adapter.EmptyOutput(ev.Harness), $"verdict {(verdict.Passed ? "passed" : "failed")}; next {step.Kind}");
    }

    private (WorkflowVerdict Verdict, Step Next) Judge(WorkflowRepo repo, string? rolesRoot, CurrentStep current, string? sessionId, string? sessionRepo)
    {
        var judged = _submitter.SubmitTask(repo, rolesRoot, current, sessionId, sessionRepo);
        StopCounterStore.ResetAll(repo);
        return (judged.Verdict, judged.Next);
    }

    private static bool HasMatchingDispatchNonce(CurrentStep current, NormalizedHookEvent ev) =>
        string.IsNullOrEmpty(current.DispatchNonce)
        || (!string.IsNullOrEmpty(ev.SubagentPrompt) && ev.SubagentPrompt.Contains(current.DispatchNonce, StringComparison.Ordinal));

    private (string? Output, string Decision) HandleAntigravityAsyncLaunch(NormalizedHookEvent ev, WorkflowRepo repo, CurrentStep current)
    {
        var agyId = ev.LaunchedAgentId ?? $"agy-{current.Task}-{current.Attempt}";
        if (!_tasks.TryRegisterActiveSubagent(repo, current.Task!, current.Attempt, agyId, out var existing))
        {
            TurnState.QueuePendingContext(repo, $"A subagent ({existing}) is already building {current.Task}. Do not start another subagent and do not edit or run anything yourself. End your turn and wait for it.", _fs);
            return (_adapter.EmptyOutput(ev.Harness), $"duplicate launch {agyId} while {existing} runs");
        }

        TurnState.QueuePendingContext(repo, $"The subagent for {current.Task} is running in the background ({agyId}). Do not start another subagent and do not edit or run anything yourself. End your turn now (call no more tools) and wait for its completion message.", _fs);
        return (_adapter.EmptyOutput(ev.Harness), $"async launched {agyId}");
    }

    private (string? Output, string Decision) HandleClaudeAsyncLaunch(NormalizedHookEvent ev, WorkflowRepo repo, CurrentStep current)
    {
        var id = ev.LaunchedAgentId ?? $"agent-{current.Task}-{current.Attempt}";
        if (!_tasks.TryRegisterActiveSubagent(repo, current.Task!, current.Attempt, id, out var existing))
        {
            return (_adapter.ContextOutput(ev, $"A subagent ({existing}) is already building {current.Task}. Do not start another subagent and do not edit or run anything yourself. End your turn and wait for it."), $"duplicate launch {id} while {existing} runs");
        }

        return (_adapter.ContextOutput(ev, $"The subagent for {current.Task} is running in the background ({id}). Do not start another subagent and do not edit or run anything yourself. End your turn; the next step arrives when it finishes."), $"async launched {id}");
    }
}
