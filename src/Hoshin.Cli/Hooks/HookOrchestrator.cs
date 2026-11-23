using System.Diagnostics;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Dual-harness hook orchestrator for <c>hoshin hook &lt;start|result|subagent-stop|stop&gt;</c>.
/// Reads <see cref="WorkflowState"/>, delegates all ordering and gate logic to <see cref="WorkflowEngine"/>,
/// executes library-owned steps via <see cref="IWorkflowRunner"/>, and speaks both Claude Code and Antigravity hook protocols.
/// </summary>
public sealed class HookOrchestrator(
    IHookAdapter adapter,
    IWorkflowRunner runner,
    IAllowListedCommandRunner commandRunner,
    IFileSystem fileSystem,
    PlanReader planReader,
    TimeProvider timeProvider)
{
    private const int MaxStopBlocks = 3;

    private readonly IHookAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IWorkflowRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IAllowListedCommandRunner _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly PlanReader _planReader = planReader ?? throw new ArgumentNullException(nameof(planReader));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: fix in T43")]
    public (string? Output, string Decision) HandleStart(NormalizedHookEvent ev, string? rolesRoot)
    {
        var repo = ResolveRepo(ev);
        if (repo is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "no repo");
        }

        var invocation = SkillInvocationDetector.Detect(ev);
        if (invocation is { Skill: "setup" or "context" })
        {
            // H2: setup/context never take over the run, never touch run.json/step.json/stop-blocks, and work without a change plan.
            return (_adapter.EmptyOutput(ev.Harness), $"{invocation.Skill}: allowed");
        }

        if (_fs.FileExists(repo.RunMarkerPath))
        {
            var existingMarker = repo.ReadRunMarker();
            var existingStep = repo.ReadStep();
            if (existingStep is { Kind: "Implement" or "Retry" or "NeedsApproval" } &&
                HookSessionScope.IsActiveNonStale(existingMarker, existingStep, _time.GetUtcNow()) &&
                !HookSessionScope.IsParentRunSession(existingMarker!, ev))
            {
                return (_adapter.EmptyOutput(ev.Harness), "different session owns active build run");
            }
        }

        // Antigravity PostToolUse cannot inject context directly; PreInvocation drains queued context.
        if (ev.IsAntigravity && ev.EventName == "PreInvocation" && File.Exists(repo.PendingContextPath))
        {
            var pending = File.ReadAllText(repo.PendingContextPath);
            TryDelete(repo.PendingContextPath);
            if (!string.IsNullOrWhiteSpace(pending))
            {
                return (_adapter.ContextOutput(ev, pending), "injected pending context");
            }
        }

        if (invocation is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "not ours");
        }

        var invoked = invocation.Skill;
        var slashArgs = invocation.Args;

        // S1: Approval and document acceptance are strictly human-only (user-typed slash commands).
        if (invoked is "accept" or "done" && invocation.Source != SkillInvocationSource.User)
        {
            var cmd = HookInstructions.SkillCommand("accept", ev.Harness);
            return EmitStartContext(
                ev,
                repo,
                $"Only the engineer can approve or accept a change. Ask the engineer to type `{cmd}`, then end your turn.",
                $"{invoked}: refused model-sourced invocation");
        }

        // Antigravity PreInvocation fires before every model call in a turn with unchanged lastUserInput: deduplicate.
        if (ev.IsAntigravity && ev.EventName == "PreInvocation")
        {
            var turnKey = $"{ev.SessionId}|{invoked}|{ev.LastUserInput?.Trim()}";
            var lastStart = LastStartPath(repo);
            if (File.Exists(lastStart) && File.ReadAllText(lastStart) == turnKey)
            {
                return (_adapter.EmptyOutput(ev.Harness), $"already started {invoked} in this turn");
            }

            Directory.CreateDirectory(repo.HoshinDir);
            File.WriteAllText(lastStart, turnKey);
        }

        var state = ReadState(repo, rolesRoot, out var error);
        if (state is null)
        {
            return EmitStartContext(ev, repo, error ?? "There is no change plan in this repository. Tell the engineer, then end your turn.", "no plan");
        }

        var gate = WorkflowEngine.Gate(state, invoked);
        if (gate is not null)
        {
            return EmitStartContext(ev, repo, gate + " Tell the person who asked, in one or two sentences, then end your turn.", $"{invoked}: refused, next is {WorkflowEngine.Next(state).Kind}");
        }

        _fs.CreateDirectory(repo.HoshinDir);
        repo.WriteRunMarker(new RunMarker(_time.GetUtcNow(), ev.SessionId, ev.HarnessName, repo.ChangeFolder));
        StopCounterStore.ResetAll(repo);

        var prefix = "";
        if (invoked == "fix")
        {
            var fixStep = new WriteFix(repo.ChangeFolder, WorkflowBriefs.Fix(state));
            _runner.Issue(repo, state, fixStep, ev.SessionId);
            var fixArgs = ev.CommandArgs ?? slashArgs ?? "";
            var desc = fixArgs.Trim().Length > 0 ? $"\n\nThe engineer's fix request: {fixArgs.Trim()}" : "";
            return EmitStartContext(ev, repo, HookInstructions.For(fixStep, ev.Harness, suffix: desc), "fix: Fix");
        }

        if (invoked is "accept" or "done")
        {
            switch (WorkflowEngine.Next(state))
            {
                case NeedsApproval:
                    _runner.Answer(state.Repo(repo), state, approved: true, $"approved by the engineer in {(ev.IsAntigravity ? "Antigravity" : "Claude Code")}");
                    prefix = "The engineer's approval is recorded.\n\n";
                    break;
                case AwaitAccept a when invoked == "accept":
                    var commit = _runner.AcceptDocument(repo, state, a.Document);
                    prefix = commit is null
                        ? "The acceptance could not be committed.\n\n"
                        : $"The {a.Document} is accepted, committed as {WorkflowEngine.Short(commit)}.\n\n";
                    break;
            }
        }

        var step = Advance(repo, rolesRoot, ev.SessionId);
        if (invoked == "accept" && step is WriteDocument nextWrite)
        {
            return EmitStartContext(ev, repo, prefix + $"Tell the person the next step is {WorkflowEngine.Describe(step)}. Then end your turn.", $"accept: next {step.Kind} {nextWrite.Document}");
        }

        if (invoked == "accept" && step is Implement)
        {
            return EmitStartContext(ev, repo, prefix + $"Tell the person the plan is accepted and the next step is {WorkflowEngine.Describe(step)}. Then end your turn.", "accept: next Implement");
        }

        var args = ev.CommandArgs ?? slashArgs ?? "";
        var idea = invoked == "intent" && args.Trim().Length > 0
            ? $"\n\nThe product owner's idea, as they typed it: {args.Trim()}"
            : "";

        return EmitStartContext(ev, repo, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: prefix, suffix: idea), $"{invoked}: {step.Kind}");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: fix in T43")]
    public (string? Output, string Decision) HandleResult(NormalizedHookEvent ev, string? rolesRoot)
    {
        var repo = ResolveRepo(ev);
        if (repo is null || !File.Exists(repo.RunMarkerPath))
        {
            return (_adapter.EmptyOutput(ev.Harness), "no run");
        }

        if (!HookSessionScope.IsRunSession(repo, ev, _time.GetUtcNow(), out var marker, out var current) ||
            !HookSessionScope.IsParentRunSession(marker!, ev))
        {
            return (_adapter.EmptyOutput(ev.Harness), "not the run session");
        }

        if (current is not { Kind: "Implement" or "Retry", Task: not null })
        {
            return (_adapter.EmptyOutput(ev.Harness), $"step is {current?.Kind ?? "none"}");
        }

        if (ev.IsAntigravity && ev.IsTaskManageTool)
        {
            return (_adapter.EmptyOutput(ev.Harness), $"observed {ev.ToolName}");
        }

        if (!string.IsNullOrEmpty(current.DispatchNonce) &&
            (string.IsNullOrEmpty(ev.SubagentPrompt) || !ev.SubagentPrompt.Contains(current.DispatchNonce, StringComparison.Ordinal)))
        {
            return (_adapter.EmptyOutput(ev.Harness), "subagent prompt missing dispatch nonce");
        }

        if (ev.IsAntigravity)
        {
            var agyId = ev.LaunchedAgentId ?? $"agy-{current.Task}-{current.Attempt}";
            if (!_runner.TryRegisterActiveSubagent(repo, current.Task, current.Attempt, agyId, out var existing))
            {
                QueuePendingContext(repo, $"A subagent ({existing}) is already building {current.Task}. Do not start another subagent and do not edit or run anything yourself. End your turn and wait for it.");
                return (_adapter.EmptyOutput(ev.Harness), $"duplicate launch {agyId} while {existing} runs");
            }

            QueuePendingContext(repo, $"The subagent for {current.Task} is running in the background ({agyId}). Do not start another subagent and do not edit or run anything yourself. End your turn now (call no more tools) and wait for its completion message.");
            return (_adapter.EmptyOutput(ev.Harness), $"async launched {agyId}");
        }

        if (ev.IsAsyncLaunch)
        {
            var id = ev.LaunchedAgentId ?? $"agent-{current.Task}-{current.Attempt}";
            if (!_runner.TryRegisterActiveSubagent(repo, current.Task, current.Attempt, id, out var existing))
            {
                return (_adapter.ContextOutput(ev, $"A subagent ({existing}) is already building {current.Task}. Do not start another subagent and do not edit or run anything yourself. End your turn and wait for it."), $"duplicate launch {id} while {existing} runs");
            }

            return (_adapter.ContextOutput(ev, $"The subagent for {current.Task} is running in the background ({id}). Do not start another subagent and do not edit or run anything yourself. End your turn; the next step arrives when it finishes."), $"async launched {id}");
        }

        // Foreground subagent completed: judge attempt immediately and hand out next step.
        var (verdict, step) = JudgeAndAdvance(repo, rolesRoot, current, ev.SessionId);
        return (_adapter.ContextOutput(ev, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: ReportVerdict(current, verdict))), $"verdict {(verdict.Passed ? "passed" : "failed")}; next {step.Kind}");
    }

    public (string? Output, string Decision) HandleSubagentStop(NormalizedHookEvent ev, string? rolesRoot)
    {
        var repo = ResolveRepo(ev);
        if (repo is null || !File.Exists(repo.RunMarkerPath))
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

        var (verdict, step) = JudgeAndAdvance(repo, rolesRoot, current, repo.ReadRunMarker()?.SessionId);
        QueuePendingReport(repo, ReportVerdict(current, verdict));
        return (_adapter.EmptyOutput(ev.Harness), $"verdict {(verdict.Passed ? "passed" : "failed")}; next {step.Kind}");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: fix in T43")]
    public (string? Output, string Decision) HandleStop(NormalizedHookEvent ev, string? rolesRoot)
    {
        var repo = ResolveRepo(ev);
        if (repo is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "no run");
        }

        if (!File.Exists(repo.RunMarkerPath))
        {
            var tamper = GuardTamperingProtection.DetectMissingRunMarkerTampering(repo, _planReader, rolesRoot);
            if (tamper is not null)
            {
                return (_adapter.BlockStopOutput(ev.Harness, tamper), "block: run.json tampering detected");
            }

            return (_adapter.EmptyOutput(ev.Harness), "no run");
        }

        // Only the run's owning parent session participates in Stop orchestration (unrelated sessions and subagents stay silent).
        if (!HookSessionScope.IsRunSession(repo, ev, _time.GetUtcNow(), out var marker, out var current) ||
            !HookSessionScope.IsParentRunSession(marker!, ev))
        {
            return (_adapter.EmptyOutput(ev.Harness), "allow: not the run parent session");
        }

        var active = repo.ReadLedger().ActiveSubagent;
        var trackedAgentId = current?.AgentId ?? active?.AgentId;

        if (ev.IsAntigravity && ev.FullyIdle == false)
        {
            return (_adapter.EmptyOutput(ev.Harness), $"allow: waiting for background task ({trackedAgentId ?? "unknown"}, fullyIdle=false)");
        }

        if (!ev.IsAntigravity && trackedAgentId is not null && ev.RunningBackgroundTaskIds.Contains(trackedAgentId))
        {
            return (_adapter.EmptyOutput(ev.Harness), $"allow: waiting for {trackedAgentId}");
        }

        var verdictReport = DrainPendingReport(repo);
        var hasReport = verdictReport.Length > 0;
        if (current is { Task: not null } && trackedAgentId is not null &&
            !repo.ReadLedger().Verdicts.Any(v => v.Task == current.Task && v.Attempt == current.Attempt))
        {
            var (verdict, _) = JudgeAndAdvance(repo, rolesRoot, current, ev.SessionId);
            verdictReport += ReportVerdict(current, verdict);
            hasReport = true;
        }

        if (current?.Kind == "Review")
        {
            if (hasReport)
            {
                var reviewState = ReadState(repo, rolesRoot, out _);
                if (reviewState is not null && WorkflowEngine.Next(reviewState) is ReviewDocument rv)
                {
                    return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(rv, ev.Harness, prefix: verdictReport)), "continue: report Review");
                }
            }

            return StopWhileReviewing(ev, repo, rolesRoot);
        }

        if (current?.Kind == "Fix")
        {
            return StopWhileFixing(ev, repo, rolesRoot);
        }

        var step = Advance(repo, rolesRoot, ev.SessionId);
        if (step is WriteDocument write)
        {
            return StopWhileWriting(ev, repo, rolesRoot, write);
        }

        switch (step)
        {
            case Implement or Retry:
                var blocks = StopCounterStore.Next(repo, "build", ev.ContinuationActive);
                if (blocks > MaxStopBlocks)
                {
                    ClearTurnFiles(repo);
                    TryDelete(repo.RunMarkerPath);
                    return (_adapter.SystemMessageOutput(ev.Harness, $"hoshin: parked after {MaxStopBlocks} stops without taking the step ({step.Kind})."), $"parked after {blocks - 1} blocks");
                }

                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: verdictReport)), $"block {blocks}: {step.Kind}");

            case ReviewDocument when hasReport:
                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report Review");

            case NeedsApproval when hasReport:
                return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report NeedsApproval");

            case Done:
                if (hasReport)
                {
                    return (_adapter.BlockStopOutput(ev.Harness, HookInstructions.For(step, ev.Harness, prefix: verdictReport)), "continue: report Done");
                }

                ClearTurnFiles(repo);
                TryDelete(repo.RunMarkerPath);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: {step.Kind}");

            default:
                ClearTurnFiles(repo);
                return (_adapter.EmptyOutput(ev.Harness), $"allow: {step.Kind}");
        }
    }

    private (string? Output, string Decision) StopWhileWriting(NormalizedHookEvent ev, WorkflowRepo repo, string? rolesRoot, WriteDocument write)
    {
        var state = ReadState(repo, rolesRoot, out _)!;
        var doc = state.Document(write.Document);
        var shown = Path.GetRelativePath(repo.Root, write.Path).Replace('\\', '/');

        if (!doc.Exists || (!doc.Uncommitted && doc.InHead))
        {
            ClearTurnFiles(repo);
            return (_adapter.EmptyOutput(ev.Harness), $"allow: waiting, {write.Document} unchanged");
        }

        var (problems, commit) = _runner.CommitDocument(repo, state, write.Document);
        if (commit is null)
        {
            if (StopCounterStore.EndsWithQuestion(ev.LastAssistantMessage))
            {
                StopCounterStore.Reset(repo, $"write-{write.Document}");
                return (_adapter.EmptyOutput(ev.Harness), $"allow: assistant asked a question while writing {write.Document}");
            }

            var blocks = StopCounterStore.Next(repo, $"write-{write.Document}", ev.ContinuationActive);
            if (blocks > MaxStopBlocks)
            {
                return (blocks == MaxStopBlocks + 1
                    ? _adapter.SystemMessageOutput(ev.Harness, $"hoshin: the {write.Document} still has problems after {MaxStopBlocks} tries; it stays uncommitted.")
                    : _adapter.EmptyOutput(ev.Harness), $"gave up after {MaxStopBlocks} blocks");
            }

            var list = string.Join("\n", problems.Select(p => $"- line {p.Line}: {p.Message}"));
            return (_adapter.BlockStopOutput(ev.Harness, $"The {write.Document} at {shown} cannot be committed yet:\n{list}\nFix the file, or if you are still interviewing, ask your next question. Then end your turn."), $"block {blocks}: {write.Document} has {problems.Count} problems");
        }

        StopCounterStore.ResetAll(repo);
        var next = Advance(repo, rolesRoot, ev.SessionId);
        return (_adapter.BlockStopOutput(ev.Harness, $"The {write.Document} draft is committed as {WorkflowEngine.Short(commit)}. Tell the person it is ready to review at {shown}, and that `{HookInstructions.SkillCommand("accept", ev.Harness)}` accepts it. Then end your turn."), $"committed {WorkflowEngine.Short(commit)}; next {next.Kind}");
    }

    private (string? Output, string Decision) StopWhileReviewing(NormalizedHookEvent ev, WorkflowRepo repo, string? rolesRoot)
    {
        var state = ReadState(repo, rolesRoot, out _);
        if (state is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "allow: no state");
        }

        var reviewDoc = repo.DocumentStatus(Path.Combine("build", "review.md"));
        var shown = Path.GetRelativePath(repo.Root, reviewDoc.Path).Replace('\\', '/');
        if (!reviewDoc.Exists || (!reviewDoc.Uncommitted && reviewDoc.InHead))
        {
            ClearTurnFiles(repo);
            return (_adapter.EmptyOutput(ev.Harness), "allow: waiting, review unchanged");
        }

        var (problems, commit) = _runner.CommitReview(repo, state);
        if (commit is null)
        {
            var blocks = StopCounterStore.Next(repo, "review", ev.ContinuationActive);
            if (blocks > MaxStopBlocks)
            {
                return (blocks == MaxStopBlocks + 1
                    ? _adapter.SystemMessageOutput(ev.Harness, $"hoshin: the review still has problems after {MaxStopBlocks} tries; it stays uncommitted.")
                    : _adapter.EmptyOutput(ev.Harness), $"gave up after {MaxStopBlocks} blocks");
            }

            var list = string.Join("\n", problems.Select(p => $"- line {p.Line}: {p.Message}"));
            return (_adapter.BlockStopOutput(ev.Harness, $"The review at {shown} cannot be committed yet:\n{list}\nFix the review document, then end your turn."), $"block {blocks}: review has {problems.Count} problems");
        }

        StopCounterStore.ResetAll(repo);
        var next = Advance(repo, rolesRoot, ev.SessionId);
        var msg = next is Done { Success: false } stopped
            ? $"The review is committed as {WorkflowEngine.Short(commit)}. {stopped.Outcome}. Report the findings to the engineer. Then end your turn."
            : $"The review is committed as {WorkflowEngine.Short(commit)}. Report the findings to the engineer and let them know `{HookInstructions.SkillCommand("accept", ev.Harness)}` or `{HookInstructions.SkillCommand("done", ev.Harness)}` publishes once they approve. Then end your turn.";
        return (_adapter.BlockStopOutput(ev.Harness, msg), $"committed review {WorkflowEngine.Short(commit)}; next {next.Kind}");
    }

    private (string? Output, string Decision) StopWhileFixing(NormalizedHookEvent ev, WorkflowRepo repo, string? rolesRoot)
    {
        var state = ReadState(repo, rolesRoot, out _);
        if (state is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "allow: no state");
        }

        var docs = new[] { state.Intent, state.Spec, state.PlanDoc };
        var anyDirty = docs.Any(d => d.Exists && (d.Uncommitted || !d.InHead));
        if (!anyDirty)
        {
            ClearTurnFiles(repo);
            return (_adapter.EmptyOutput(ev.Harness), "allow: waiting, fix documents unchanged");
        }

        var (problems, commit) = _runner.CommitFixDocuments(repo, state);
        if (commit is null)
        {
            var blocks = StopCounterStore.Next(repo, "fix", ev.ContinuationActive);
            if (blocks > MaxStopBlocks)
            {
                return (blocks == MaxStopBlocks + 1
                    ? _adapter.SystemMessageOutput(ev.Harness, $"hoshin: the fix draft still has problems after {MaxStopBlocks} tries; it stays uncommitted.")
                    : _adapter.EmptyOutput(ev.Harness), $"gave up after {MaxStopBlocks} blocks");
            }

            var list = string.Join("\n", problems.Select(p => $"- {p.File} line {p.Line}: {p.Message}"));
            return (_adapter.BlockStopOutput(ev.Harness, $"The light-track fix documents cannot be committed yet:\n{list}\nFix the issues above, or ask the engineer if clarification is needed. Then end your turn."), $"block {blocks}: fix has {problems.Count} problems");
        }

        StopCounterStore.ResetAll(repo);
        var next = Advance(repo, rolesRoot, ev.SessionId);
        return (_adapter.BlockStopOutput(ev.Harness, $"The light-track fix draft is committed as {WorkflowEngine.Short(commit)}. Tell the engineer it is ready for review and acceptance via `{HookInstructions.SkillCommand("accept", ev.Harness)}`. Then end your turn."), $"committed fix {WorkflowEngine.Short(commit)}; next {next.Kind}");
    }

    private (WorkflowVerdict Verdict, Step Next) JudgeAndAdvance(WorkflowRepo repo, string? rolesRoot, CurrentStep current, string? sessionId)
    {
        var state = ReadState(repo, rolesRoot, out _)!;
        var verdict = _runner.EvaluateAndCommitTask(repo, state, current.Task!, current.Attempt);
        StopCounterStore.ResetAll(repo);
        return (verdict, Advance(repo, rolesRoot, sessionId));
    }

    private Step Advance(WorkflowRepo repo, string? rolesRoot, string? sessionId)
    {
        while (true)
        {
            var state = ReadState(repo, rolesRoot, out var error);
            if (state is null)
            {
                return new Done(false, error ?? "no plan");
            }

            var step = WorkflowEngine.Next(state);
            switch (step)
            {
                case RunChecks:
                    _runner.RunChecks(repo, state);
                    continue;

                case Publish p:
                    var publish = _runner.Publish(repo, state, p.Ref);
                    if (!publish.Ok)
                    {
                        var detail = string.IsNullOrWhiteSpace(publish.Error) ? "" : $": {publish.Error}";
                        return new Done(false, $"could not push to {p.Ref}{detail}");
                    }

                    continue;

                default:
                    _runner.Issue(repo, state, step, sessionId);
                    return step;
            }
        }
    }

    private static string ReportVerdict(CurrentStep current, WorkflowVerdict verdict) => verdict.Passed
        ? $"{current.Task} attempt {current.Attempt} is accepted: test committed first ({verdict.Red}) and green at {WorkflowEngine.Short(verdict.Head)}.\n\n"
        : $"{current.Task} attempt {current.Attempt} is not accepted:\n- " + string.Join("\n- ", verdict.Reasons) + "\n\n";

    private (string? Output, string Decision) EmitStartContext(NormalizedHookEvent ev, WorkflowRepo repo, string text, string decision)
    {
        if (ev.IsAntigravity && ev.EventName == "PostToolUse")
        {
            QueuePendingContext(repo, text);
            return (_adapter.EmptyOutput(ev.Harness), decision + " (queued for PreInvocation)");
        }

        return (_adapter.ContextOutput(ev, text), decision);
    }

    private WorkflowRepo? ResolveRepo(NormalizedHookEvent ev) =>
        WorkflowRepo.Find(ev.Cwd ?? Directory.GetCurrentDirectory(), _commandRunner, _fs);

    private WorkflowState? ReadState(WorkflowRepo repo, string? rolesRoot, out string? error)
    {
        error = null;
        try
        {
            return WorkflowState.Read(repo, _planReader, rolesRoot);
        }
        catch (CouldNotRunException e)
        {
            error = e.Message;
            return null;
        }
    }

    private static void QueuePendingContext(WorkflowRepo repo, string text)
    {
        Directory.CreateDirectory(repo.HoshinDir);
        File.WriteAllText(repo.PendingContextPath, text);
    }

    private static void ClearTurnFiles(WorkflowRepo repo)
    {
        TryDelete(LastStartPath(repo));
        TryDelete(repo.PendingContextPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Ignore transient lock on cleanup.
        }
    }

    private static string LastStartPath(WorkflowRepo repo) => Path.Combine(repo.HoshinDir, "last-start.txt");

    private static string PendingReportPath(WorkflowRepo repo) => Path.Combine(repo.HoshinDir, "pending-report.txt");

    private static void QueuePendingReport(WorkflowRepo repo, string report)
    {
        using var gate = repo.AcquireRunLock();
        Directory.CreateDirectory(repo.HoshinDir);
        File.AppendAllText(PendingReportPath(repo), report);
    }

    private static string DrainPendingReport(WorkflowRepo repo)
    {
        var path = PendingReportPath(repo);
        if (!File.Exists(path))
        {
            return "";
        }

        using var gate = repo.AcquireRunLock();
        if (!File.Exists(path))
        {
            return "";
        }

        var text = File.ReadAllText(path);
        TryDelete(path);
        return text;
    }
}

internal static class WorkflowStateExtensions
{
    public static WorkflowRepo Repo(this WorkflowState _, WorkflowRepo repo) => repo;
}
