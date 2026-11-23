using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>Handles <c>hoshin hook start</c> (skill detection, human-only approval/acceptance enforcement, gate checks, and step dispatch).</summary>
internal sealed class StartHandler(
    IHookAdapter adapter,
    ITaskEvaluator tasks,
    IApprovalRecorder approvals,
    IDocumentCommitter documents,
    WorkflowAdvancer advancer,
    IFileSystem fileSystem,
    TimeProvider timeProvider)
{
    private readonly IHookAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly ITaskEvaluator _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    private readonly IApprovalRecorder _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
    private readonly IDocumentCommitter _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    private readonly WorkflowAdvancer _advancer = advancer ?? throw new ArgumentNullException(nameof(advancer));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public (string? Output, string Decision) Handle(NormalizedHookEvent ev, string? rolesRoot)
    {
        var repo = _advancer.ResolveRepo(ev);
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

        if (IsBlockedByOtherActiveBuild(repo, ev))
        {
            return (_adapter.EmptyOutput(ev.Harness), "different session owns active build run");
        }

        if (TryDrainAntigravityPendingContext(ev, repo) is { } drained)
        {
            return drained;
        }

        if (invocation is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "not ours");
        }

        return HandleSkillInvocation(ev, repo, invocation, rolesRoot);
    }

    private bool IsBlockedByOtherActiveBuild(WorkflowRepo repo, NormalizedHookEvent ev)
    {
        if (!_fs.FileExists(repo.RunMarkerPath))
        {
            return false;
        }

        var existingMarker = repo.ReadRunMarker();
        var existingStep = repo.ReadStep();
        return existingStep is { Kind: StepKind.Implement or StepKind.Retry or StepKind.NeedsApproval }
            && HookSessionScope.IsActiveNonStale(existingMarker, existingStep, _time.GetUtcNow())
            && !HookSessionScope.IsParentRunSession(existingMarker!, ev);
    }

    private (string? Output, string Decision)? TryDrainAntigravityPendingContext(NormalizedHookEvent ev, WorkflowRepo repo)
    {
        if (!ev.IsAntigravity || ev.EventName != "PreInvocation")
        {
            return null;
        }

        var pending = TurnState.TryDrainPendingContext(repo, _fs);
        return pending is null ? null : (_adapter.ContextOutput(ev, pending), "injected pending context");
    }

    private (string? Output, string Decision) HandleSkillInvocation(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        SkillInvocation invocation,
        string? rolesRoot)
    {
        var invoked = invocation.Skill;
        if (invoked is "accept" or "done" && invocation.Source != SkillInvocationSource.User)
        {
            var cmd = HookInstructions.SkillCommand("accept", ev.Harness);
            return EmitStartContext(
                ev,
                repo,
                $"Only the engineer can approve or accept a change. Ask the engineer to type `{cmd}`, then end your turn.",
                $"{invoked}: refused model-sourced invocation");
        }

        if (ev.IsAntigravity && ev.EventName == "PreInvocation")
        {
            var turnKey = $"{ev.SessionId}|{invoked}|{ev.LastUserInput?.Trim()}";
            if (TurnState.IsDuplicateAntigravityStart(repo, turnKey, _fs))
            {
                return (_adapter.EmptyOutput(ev.Harness), $"already started {invoked} in this turn");
            }
        }

        var state = _advancer.ReadState(repo, rolesRoot, out var error);
        if (state is null)
        {
            return EmitStartContext(ev, repo, error ?? "There is no change plan in this repository. Tell the engineer, then end your turn.", "no plan");
        }

        var gate = WorkflowEngine.Gate(state, invoked);
        if (gate is not null)
        {
            return EmitStartContext(ev, repo, gate + " Tell the person who asked, in one or two sentences, then end your turn.", $"{invoked}: refused, next is {WorkflowEngine.Next(state).Kind}");
        }

        StartRun(repo, ev);
        return DispatchSkill(ev, repo, state, invocation, rolesRoot);
    }

    private void StartRun(WorkflowRepo repo, NormalizedHookEvent ev)
    {
        _fs.CreateDirectory(repo.HoshinDir);
        repo.WriteRunMarker(new RunMarker(_time.GetUtcNow(), ev.SessionId, ev.HarnessName, repo.ChangeFolder));
        StopCounterStore.ResetAll(repo);
    }

    private (string? Output, string Decision) DispatchSkill(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        WorkflowState state,
        SkillInvocation invocation,
        string? rolesRoot)
    {
        var invoked = invocation.Skill;
        if (invoked == "fix")
        {
            var fixStep = new WriteFix(repo.ChangeFolder, WorkflowBriefs.Fix(state));
            _tasks.Issue(repo, state, fixStep, ev.SessionId);
            var fixArgs = ev.CommandArgs ?? invocation.Args ?? "";
            var desc = fixArgs.Trim().Length > 0 ? $"\n\nThe engineer's fix request: {fixArgs.Trim()}" : "";
            return EmitStartContext(ev, repo, HookInstructions.For(fixStep, ev.Harness, suffix: desc), "fix: Fix");
        }

        var prefix = ApplyHumanAcceptanceIfNeeded(ev, repo, state, invoked);
        var step = _advancer.Advance(repo, rolesRoot, ev.SessionId);

        if (invoked == "accept" && TryFormatPostAcceptMessage(ev, repo, step, prefix) is { } postAccept)
        {
            return postAccept;
        }

        var args = ev.CommandArgs ?? invocation.Args ?? "";
        var idea = invoked == "intent" && args.Trim().Length > 0
            ? $"\n\nThe product owner's idea, as they typed it: {args.Trim()}"
            : "";

        return EmitStartContext(ev, repo, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: prefix, suffix: idea), $"{invoked}: {step.Kind}");
    }

    private string ApplyHumanAcceptanceIfNeeded(NormalizedHookEvent ev, WorkflowRepo repo, WorkflowState state, string invoked)
    {
        if (invoked is not ("accept" or "done"))
        {
            return "";
        }

        switch (WorkflowEngine.Next(state))
        {
            case NeedsApproval:
                _approvals.Answer(repo, state, approved: true, $"approved by the engineer in {(ev.IsAntigravity ? "Antigravity" : "Claude Code")}");
                return "The engineer's approval is recorded.\n\n";
            case AwaitAccept a when invoked == "accept":
                var commit = _documents.AcceptDocument(repo, state, a.Document);
                return commit is null
                    ? "The acceptance could not be committed.\n\n"
                    : $"The {a.Document.ToWireName()} is accepted, committed as {WorkflowEngine.Short(commit)}.\n\n";
            default:
                return "";
        }
    }

    private (string? Output, string Decision)? TryFormatPostAcceptMessage(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        Step step,
        string prefix)
    {
        if (step is WriteDocument nextWrite)
        {
            return EmitStartContext(ev, repo, prefix + $"Tell the person the next step is {WorkflowEngine.Describe(step)}. Then end your turn.", $"accept: next {step.Kind} {nextWrite.Document.ToWireName()}");
        }

        if (step is Implement)
        {
            return EmitStartContext(ev, repo, prefix + $"Tell the person the plan is accepted and the next step is {WorkflowEngine.Describe(step)}. Then end your turn.", "accept: next Implement");
        }

        return null;
    }

    private (string? Output, string Decision) EmitStartContext(NormalizedHookEvent ev, WorkflowRepo repo, string text, string decision)
    {
        if (ev.IsAntigravity && ev.EventName == "PostToolUse")
        {
            TurnState.QueuePendingContext(repo, text, _fs);
            return (_adapter.EmptyOutput(ev.Harness), decision + " (queued for PreInvocation)");
        }

        return (_adapter.ContextOutput(ev, text), decision);
    }
}
