using Hoshin.Workflow.Common;
using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>Handles <c>hoshin hook stop</c> (tampering checks, async verdict reporting, document/review/fix commit loops, and step advancement).</summary>
internal sealed class StopHandler(
    IHookAdapter adapter,
    IHookRunResolver runResolver,
    WorkSubmitter submitter,
    StepAdvancer advancer,
    IFileSystem fileSystem,
    TimeProvider timeProvider)
{
    private const int MaxStopBlocks = 3;

    private readonly IHookAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IHookRunResolver _runResolver = runResolver ?? throw new ArgumentNullException(nameof(runResolver));
    private readonly WorkSubmitter _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
    private readonly StepAdvancer _advancer = advancer ?? throw new ArgumentNullException(nameof(advancer));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public (string? Output, string Decision) Handle(NormalizedHookEvent ev, string? rolesRoot)
    {
        var resolved = _runResolver.Resolve(ev);
        if (resolved is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "no run");
        }

        var repo = resolved.WorkflowRepo;
        if (!_fs.FileExists(repo.RunMarkerPath))
        {
            var tamper = GuardTamperingProtection.DetectMissingRunMarkerTampering(repo, ev, _advancer.StateReader, rolesRoot);
            return tamper is not null
                ? (_adapter.BlockStopOutput(ev.Harness, tamper), "block: run.json tampering detected")
                : (_adapter.EmptyOutput(ev.Harness), "no run");
        }

        // Only the run's owning parent session participates in Stop orchestration (unrelated sessions and subagents stay silent).
        if (!HookSessionScope.IsRunSession(repo, ev, _time.GetUtcNow(), out var marker, out var current)
            || !HookSessionScope.IsParentRunSession(marker!, ev))
        {
            return (_adapter.EmptyOutput(ev.Harness), "allow: not the run parent session");
        }

        var trackedAgentId = current?.AgentId ?? repo.ReadLedger().ActiveSubagent?.AgentId;
        if (WaitForRunningBackgroundSubagent(ev, trackedAgentId) is { } wait)
        {
            return wait;
        }

        var sessionRepo = resolved.ProductRepoName;
        var verdictReport = CollectVerdictReport(ev, repo, rolesRoot, current, trackedAgentId, sessionRepo);
        return DispatchActiveStepStop(ev, repo, rolesRoot, current, verdictReport, sessionRepo);
    }

    private (string? Output, string Decision)? WaitForRunningBackgroundSubagent(NormalizedHookEvent ev, string? trackedAgentId)
    {
        if (ev.IsAntigravity && ev.FullyIdle == false)
        {
            return (_adapter.EmptyOutput(ev.Harness), $"allow: waiting for background task ({trackedAgentId ?? "unknown"}, fullyIdle=false)");
        }

        if (!ev.IsAntigravity && trackedAgentId is not null && ev.RunningBackgroundTaskIds.Contains(trackedAgentId))
        {
            return (_adapter.EmptyOutput(ev.Harness), $"allow: waiting for {trackedAgentId}");
        }

        return null;
    }

    private string CollectVerdictReport(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        string? rolesRoot,
        CurrentStep? current,
        string? trackedAgentId,
        string? sessionRepo)
    {
        var report = TurnState.DrainPendingReport(repo, _fs);
        if (current is { Task: not null }
            && trackedAgentId is not null
            && !repo.ReadLedger().WorkflowVerdicts.Any(v => v.Task == current.Task && v.Attempt == current.Attempt))
        {
            var judged = _submitter.SubmitTask(repo, rolesRoot, current, ev.SessionId, sessionRepo);
            StopCounterStore.ResetAll(repo);
            report += StepAdvancer.ReportVerdict(current, judged.Verdict);
        }

        return report;
    }

    private (string? Output, string Decision) DispatchActiveStepStop(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        string? rolesRoot,
        CurrentStep? current,
        string verdictReport,
        string? sessionRepo)
    {
        var hasReport = verdictReport.Length > 0;
        if (current?.Kind == StepKind.Review)
        {
            if (hasReport && _advancer.ReadState(repo, rolesRoot, out _, sessionRepo) is { } rs && WorkflowEngine.Next(rs) is ReviewDocument rv)
            {
                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(rv, ev.Harness, prefix: verdictReport)), "continue: report Review");
            }

            return StopWhileReviewing(ev, repo, rolesRoot);
        }

        if (current?.Kind == StepKind.Fix)
        {
            return StopWhileFixing(ev, repo, rolesRoot);
        }

        var step = _advancer.Advance(repo, rolesRoot, ev.SessionId, sessionRepo: sessionRepo, buildInstructions: current?.FixInstructions);
        return step is WriteDocument write
            ? StopWhileWriting(ev, repo, rolesRoot, write)
            : HandleAdvancedStepStop(ev, repo, step, verdictReport, hasReport);
    }

    private (string? Output, string Decision) HandleAdvancedStepStop(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        Step step,
        string verdictReport,
        bool hasReport)
    {
        switch (step)
        {
            case Implement or Retry:
                var blocks = StopCounterStore.Next(repo, "build", ev.ContinuationActive);
                if (blocks > MaxStopBlocks)
                {
                    TurnState.ClearTurnFiles(repo, _fs);
                    var existing = repo.ReadRunMarker();
                    var parked = existing is not null
                        ? existing with { Parked = true }
                        : new RunMarker(_time.GetUtcNow(), ev.SessionId, ev.HarnessName, repo.ChangeFolder, repo.ChangeKey, Parked: true);
                    repo.WriteRunMarker(parked);
                    return (_adapter.SystemMessageOutput(ev.Harness, $"hoshin: parked after {MaxStopBlocks} stops without taking the step ({step.Kind})."), $"parked after {blocks - 1} blocks");
                }

                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: verdictReport)), $"block {blocks}: {step.Kind}");

            case ReviewDocument when hasReport:
                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report Review");

            case NeedsApproval when hasReport:
                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report NeedsApproval");

            case Handoff:
                if (hasReport)
                {
                    return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report Handoff");
                }

                TurnState.ClearTurnFiles(repo, _fs);
                TurnState.TryDelete(repo.RunMarkerPath, _fs);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: {step.Kind}");

            case Done:
                if (hasReport)
                {
                    return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report Done");
                }

                TurnState.ClearTurnFiles(repo, _fs);
                TurnState.TryDelete(repo.RunMarkerPath, _fs);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: {step.Kind}");

            case AwaitAccept or AwaitAcceptFix:
                TurnState.ClearTurnFiles(repo, _fs);
                TurnState.TryDelete(repo.RunMarkerPath, _fs);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: {step.Kind}");

            default:
                TurnState.ClearTurnFiles(repo, _fs);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: {step.Kind}");
        }
    }

    private (string? Output, string Decision) StopWhileWriting(NormalizedHookEvent ev, WorkflowRepo repo, string? rolesRoot, WriteDocument write)
    {
        var docName = write.Document.ToWireName();
        var shown = Path.GetRelativePath(repo.Root, write.Path).Replace('\\', '/');
        switch (_submitter.SubmitDocument(repo, rolesRoot, write.Document, ev.SessionId))
        {
            case SubmitUnchanged:
                TurnState.ClearTurnFiles(repo, _fs);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: waiting, {docName} unchanged");

            case SubmitRefused refused:
                if (StopCounterStore.EndsWithQuestion(ev.LastAssistantMessage))
                {
                    StopCounterStore.Reset(repo, $"write-{docName}");
                    return (_adapter.EmptyOutput(ev.Harness), $"allow: assistant asked a question while writing {docName}");
                }

                var blocks = StopCounterStore.Next(repo, $"write-{docName}", ev.ContinuationActive);
                if (blocks > MaxStopBlocks)
                {
                    return GaveUp(ev, blocks, $"the {docName}");
                }

                var list = string.Join("\n", refused.Problems.Select(p => $"- line {p.Line}: {p.Message}"));
                return (_adapter.BlockStopOutput(ev.Harness, $"The {docName} at {shown} cannot be committed yet:\n{list}\nFix the file, or if you are still interviewing, ask your next question. Then end your turn."), $"block {blocks}: {docName} has {refused.Problems.Count} problems");

            case SubmitCommitted committed:
                StopCounterStore.ResetAll(repo);
                return (_adapter.BlockStopOutput(ev.Harness, $"The {docName} draft is committed as {WorkflowEngine.Short(committed.Commit)}. Tell the person it is ready to review at {shown}, and that `{HookInstructions.SkillCommand("accept", ev.Harness)}` accepts it. Then end your turn."), $"committed {WorkflowEngine.Short(committed.Commit)}; next {committed.Next.Kind}");

            default:
                throw new InvalidOperationException("A document submission cannot be judged as a task.");
        }
    }

    private (string? Output, string Decision) GaveUp(NormalizedHookEvent ev, int blocks, string what) =>
        (blocks == MaxStopBlocks + 1
            ? _adapter.SystemMessageOutput(ev.Harness, $"hoshin: {what} still has problems after {MaxStopBlocks} tries; it stays uncommitted.")
            : _adapter.EmptyOutput(ev.Harness), $"gave up after {MaxStopBlocks} blocks");

    private (string? Output, string Decision) StopWhileReviewing(NormalizedHookEvent ev, WorkflowRepo repo, string? rolesRoot)
    {
        if (_advancer.ReadState(repo, rolesRoot, out _) is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "allow: no state");
        }

        var shown = Path.GetRelativePath(repo.Root, repo.DocumentStatus(Path.Combine("build", "review.md")).Path).Replace('\\', '/');
        switch (_submitter.SubmitReview(repo, rolesRoot, ev.SessionId))
        {
            case SubmitUnchanged:
                TurnState.ClearTurnFiles(repo, _fs);
                return (_adapter.EmptyOutput(ev.Harness), "allow: waiting, review unchanged");

            case SubmitRefused refused:
                var blocks = StopCounterStore.Next(repo, "review", ev.ContinuationActive);
                if (blocks > MaxStopBlocks)
                {
                    return GaveUp(ev, blocks, "the review");
                }

                var list = string.Join("\n", refused.Problems.Select(p => $"- line {p.Line}: {p.Message}"));
                return (_adapter.BlockStopOutput(ev.Harness, $"The review at {shown} cannot be committed yet:\n{list}\nFix the review document, then end your turn."), $"block {blocks}: review has {refused.Problems.Count} problems");

            case SubmitCommitted committed:
                StopCounterStore.ResetAll(repo);
                var commit = committed.Commit;
                var msg = committed.Next is Done { Success: false } stopped
                    ? $"The review is committed as {WorkflowEngine.Short(commit)}. {stopped.Outcome}. Report the findings to the engineer. Then end your turn."
                    : $"The review is committed as {WorkflowEngine.Short(commit)}. Report the findings to the engineer and let them know `{HookInstructions.SkillCommand("accept", ev.Harness)}` or `{HookInstructions.SkillCommand("done", ev.Harness)}` publishes once they approve. Then end your turn.";
                return (_adapter.BlockStopOutput(ev.Harness, msg), $"committed review {WorkflowEngine.Short(commit)}; next {committed.Next.Kind}");

            default:
                throw new InvalidOperationException("A review submission cannot be judged as a task.");
        }
    }

    private (string? Output, string Decision) StopWhileFixing(NormalizedHookEvent ev, WorkflowRepo repo, string? rolesRoot)
    {
        if (_advancer.ReadState(repo, rolesRoot, out _) is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "allow: no state");
        }

        switch (_submitter.SubmitFix(repo, rolesRoot, ev.SessionId))
        {
            case SubmitUnchanged:
                TurnState.ClearTurnFiles(repo, _fs);
                return (_adapter.EmptyOutput(ev.Harness), "allow: waiting, fix documents unchanged");

            case SubmitRefused refused:
                var blocks = StopCounterStore.Next(repo, "fix", ev.ContinuationActive);
                if (blocks > MaxStopBlocks)
                {
                    return GaveUp(ev, blocks, "the fix draft");
                }

                var list = string.Join("\n", refused.Problems.Select(p => $"- {p.File} line {p.Line}: {p.Message}"));
                return (_adapter.BlockStopOutput(ev.Harness, $"The light-track fix documents cannot be committed yet:\n{list}\nFix the issues above, or ask the engineer if clarification is needed. Then end your turn."), $"block {blocks}: fix has {refused.Problems.Count} problems");

            case SubmitCommitted committed:
                StopCounterStore.ResetAll(repo);
                return (_adapter.BlockStopOutput(ev.Harness, $"The light-track fix draft is committed as {WorkflowEngine.Short(committed.Commit)}. Tell the engineer it is ready for review and acceptance via `{HookInstructions.SkillCommand("accept", ev.Harness)}`. Then end your turn."), $"committed fix {WorkflowEngine.Short(committed.Commit)}; next {committed.Next.Kind}");

            default:
                throw new InvalidOperationException("A fix submission cannot be judged as a task.");
        }
    }
}
