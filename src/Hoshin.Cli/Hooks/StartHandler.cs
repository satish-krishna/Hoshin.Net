using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>Handles <c>hoshin hook start</c> (skill detection, human-only approval/acceptance/rejection/reopen enforcement, gate checks, and step dispatch).</summary>
internal sealed class StartHandler(
    IHookAdapter adapter,
    IHookRunResolver runResolver,
    IApprovalRecorder approvals,
    IDocumentCommitter documents,
    IChangeStarter changeStarter,
    WorkflowAdvancer advancer,
    IFileSystem fileSystem,
    TimeProvider timeProvider)
{
    private readonly IHookAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IHookRunResolver _runResolver = runResolver ?? throw new ArgumentNullException(nameof(runResolver));
    private readonly IApprovalRecorder _approvals = approvals ?? throw new ArgumentNullException(nameof(approvals));
    private readonly IDocumentCommitter _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    private readonly StartChangeResolver _changes = new(changeStarter, runResolver, fileSystem, timeProvider);
    private readonly WorkflowAdvancer _advancer = advancer ?? throw new ArgumentNullException(nameof(advancer));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public (string? Output, string Decision) Handle(NormalizedHookEvent ev, string? rolesRoot)
    {
        var resolved = _runResolver.Resolve(ev);
        if (resolved is null)
        {
            return (_adapter.EmptyOutput(ev.Harness), "no repo");
        }

        var repo = resolved.WorkflowRepo;

        var invocation = SkillInvocationDetector.Detect(ev);
        if (invocation is { Skill: "setup" or "context" })
        {
            // H2: setup/context never take over the run, never touch run.json/step.json/stop-blocks, and work without a change plan.
            return (_adapter.EmptyOutput(ev.Harness), $"{invocation.Skill}: allowed");
        }

        if (TryGetOtherActiveRunOwner(repo, ev, out var ownerMarker, out var ownerStep))
        {
            return invocation is null
                ? (_adapter.EmptyOutput(ev.Harness), "different session owns active run")
                : EmitStartContext(ev, repo, FormatOtherSessionOwnsRun(ownerMarker!, ownerStep!), $"blocked: session {ownerMarker!.SessionId} owns active {ownerStep!.Kind}");
        }

        if (TryDrainAntigravityPendingContext(ev, repo) is { } drained)
        {
            return drained;
        }

        return invocation is null
            ? (_adapter.EmptyOutput(ev.Harness), "not ours")
            : HandleSkillInvocation(ev, repo, resolved.ProductRepoName, invocation, rolesRoot);
    }

    private bool TryGetOtherActiveRunOwner(WorkflowRepo repo, NormalizedHookEvent ev, out RunMarker? marker, out CurrentStep? step)
    {
        marker = null;
        step = null;
        if (!_fs.FileExists(repo.RunMarkerPath))
        {
            return false;
        }

        marker = repo.ReadRunMarker();
        step = repo.ReadStep();
        return step is not null
            && WorkflowEngine.HasOwner(step.Kind)
            && HookSessionScope.IsActiveNonStale(marker, step, _time.GetUtcNow())
            && !HookSessionScope.IsParentRunSession(marker!, ev);
    }

    private static string FormatOtherSessionOwnsRun(RunMarker marker, CurrentStep step)
    {
        var owner = string.IsNullOrWhiteSpace(marker.SessionId) ? "another session" : $"session `{marker.SessionId}`";
        var stepDesc = string.IsNullOrWhiteSpace(step.Task) ? step.Kind.ToString() : $"{step.Kind} ({step.Task})";
        return $"Another session ({owner}) currently owns the active `{stepDesc}` run for this change. Wait for that session to finish before running a workflow command here. Tell the engineer, then end your turn.";
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
        string? sessionRepo,
        SkillInvocation invocation,
        string? rolesRoot)
    {
        var invoked = invocation.Skill;
        var rawArgs = (ev.CommandArgs ?? invocation.Args ?? "").Trim();
        var isFixAccept = IsFixAcceptCommand(invoked, rawArgs);

        if (RequiresHumanInvocation(invoked, isFixAccept) && invocation.Source != SkillInvocationSource.User)
        {
            var cmd = FormatHumanOnlyCommand(invoked, isFixAccept, ev.Harness);
            return EmitStartContext(
                ev,
                repo,
                $"Only the engineer can approve, accept, reopen, or reject a change. Ask the engineer to type `{cmd}`, then end your turn.",
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

        if (_changes.TryResolveOrStart(ev, ref repo, invocation, isFixAccept, EmitStartContext, out var earlyResponse))
        {
            return earlyResponse;
        }

        var buildInstructions = ExtractBuildInstructions(invoked, rawArgs);
        var state = _advancer.ReadState(repo, rolesRoot, out var error, sessionRepo, buildInstructions);
        if (state is null)
        {
            return EmitStartContext(ev, repo, error ?? "There is no change plan in this repository. Tell the engineer, then end your turn.", "no plan");
        }

        var gate = isFixAccept ? WorkflowEngine.GateFixAccept(state) : WorkflowEngine.Gate(state, invoked);
        if (gate is not null)
        {
            return EmitStartContext(ev, repo, gate + " Tell the person who asked, in one or two sentences, then end your turn.", $"{invoked}: refused, next is {WorkflowEngine.Next(state).Kind}");
        }

        StartRun(repo, ev);
        return DispatchSkill(ev, repo, state, sessionRepo, invocation, isFixAccept, rolesRoot, buildInstructions);
    }

    private static bool RequiresHumanInvocation(string invoked, bool isFixAccept) =>
        invoked is "accept" or "done" or "reject" or "reopen" || isFixAccept;

    private static string FormatHumanOnlyCommand(string invoked, bool isFixAccept, HookHarness harness) =>
        isFixAccept
            ? $"{HookInstructions.SkillCommand("fix", harness)} --accept"
            : HookInstructions.SkillCommand(invoked is "reject" or "reopen" ? invoked : "accept", harness);

    private static string? ExtractBuildInstructions(string invoked, string rawArgs) =>
        invoked == "build" && rawArgs.Length > 0 ? rawArgs : null;

    private static bool IsFixAcceptCommand(string invoked, string rawArgs) =>
        invoked == "fix" && rawArgs.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(t => string.Equals(t, "--accept", StringComparison.OrdinalIgnoreCase));

    private void StartRun(WorkflowRepo repo, NormalizedHookEvent ev)
    {
        _fs.CreateDirectory(repo.HoshinDir);
        repo.WriteRunMarker(new RunMarker(_time.GetUtcNow(), ev.SessionId, ev.HarnessName, repo.ChangeFolder, repo.ChangeKey));
        StopCounterStore.ResetAll(repo);
    }

    private (string? Output, string Decision) DispatchSkill(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        WorkflowState state,
        string? sessionRepo,
        SkillInvocation invocation,
        bool isFixAccept,
        string? rolesRoot,
        string? buildInstructions)
    {
        var invoked = invocation.Skill;
        var args = (ev.CommandArgs ?? invocation.Args ?? "").Trim();

        if (invoked == "reject")
        {
            return HandleReject(ev, repo, state, rolesRoot);
        }

        if (invoked == "reopen")
        {
            return HandleReopen(ev, repo, state, args, rolesRoot);
        }

        var disagreement = TurnState.FormatNewProgressDisagreement(repo, state, _fs);
        var prefix = disagreement + ApplyHumanAcceptanceIfNeeded(ev, repo, state, invoked, isFixAccept);
        if ((invoked == "accept" || isFixAccept) && TryFormatPostAcceptMessage(ev, repo, rolesRoot, prefix) is { } postAccept)
        {
            return postAccept;
        }

        var step = _advancer.Advance(repo, rolesRoot, ev.SessionId, isFixAccept ? null : invoked, sessionRepo, buildInstructions);
        ClearRunIfOwnedOrDone(repo, ev, step);

        var suffix = BuildPromptSuffix(invoked, args, step);
        return EmitStartContext(ev, repo, HookInstructions.For(step, ev.Harness, repo.ReadStep()?.DispatchNonce, prefix: prefix, suffix: suffix), $"{invoked}: {step.Kind}");
    }

    private (string? Output, string Decision) HandleReject(NormalizedHookEvent ev, WorkflowRepo repo, WorkflowState state, string? rolesRoot)
    {
        var commit = _documents.RejectIntent(repo, state);
        if (commit is null)
        {
            return EmitStartContext(ev, repo, "The rejection could not be committed. Tell the engineer, then end your turn.", "reject: commit failed");
        }

        var step = _advancer.Advance(repo, rolesRoot, ev.SessionId);
        ClearRunIfOwnedOrDone(repo, ev, step);

        return EmitStartContext(
            ev,
            repo,
            $"The intent is rejected, committed as {WorkflowEngine.Short(commit)}. Tell the engineer the change is closed, then end your turn.",
            $"reject: committed {WorkflowEngine.Short(commit)}");
    }

    private (string? Output, string Decision) HandleReopen(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        WorkflowState state,
        string args,
        string? rolesRoot)
    {
        var target = ResolveReopenDocument(state, args);
        if (target is null)
        {
            return EmitStartContext(ev, repo, "Specify an accepted document to reopen (`intent`, `spec`, or `plan`), then end your turn.", "reopen: no accepted target");
        }

        var commit = _documents.ReopenDocument(repo, state, target.Value);
        if (commit is null)
        {
            return EmitStartContext(ev, repo, $"Could not reopen {target.Value.ToWireName()}. Tell the engineer, then end your turn.", "reopen: commit failed");
        }

        var step = _advancer.Advance(repo, rolesRoot, ev.SessionId);
        return EmitStartContext(
            ev,
            repo,
            $"The {target.Value.ToWireName()} is reopened (`status: draft`), committed as {WorkflowEngine.Short(commit)}. Next step: {WorkflowEngine.Describe(step)}. Tell the engineer, then end your turn.",
            $"reopen: {target.Value.ToWireName()} {WorkflowEngine.Short(commit)}");
    }

    private static WorkflowDocument? ResolveReopenDocument(WorkflowState state, string args)
    {
        var token = StartChangeResolver.StripLeadingFlags(args).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        WorkflowDocument? explicitDoc = token switch
        {
            "intent" or "intent.md" => WorkflowDocument.Intent,
            "spec" or "spec.md" => WorkflowDocument.Spec,
            "plan" or "plan.md" => WorkflowDocument.Plan,
            _ => null,
        };
        if (explicitDoc is { } doc)
        {
            var d = state.Document(doc);
            return d.InHead && d.HeadStatus == "accepted" ? doc : null;
        }

        foreach (var candidate in new[] { WorkflowDocument.Plan, WorkflowDocument.Spec, WorkflowDocument.Intent })
        {
            var d = state.Document(candidate);
            if (d.InHead && d.HeadStatus == "accepted")
            {
                return candidate;
            }
        }

        return null;
    }

    private static string BuildPromptSuffix(string invoked, string args, Step step)
    {
        if (args.Length == 0)
        {
            return "";
        }

        if (invoked == "intent")
        {
            return $"\n\nThe product owner's idea, as they typed it: {args}";
        }

        if (invoked == "fix" && step is WriteFix)
        {
            return $"\n\nThe engineer's fix request: {args}";
        }

        return "";
    }

    private string ApplyHumanAcceptanceIfNeeded(NormalizedHookEvent ev, WorkflowRepo repo, WorkflowState state, string invoked, bool isFixAccept)
    {
        if (invoked is not ("accept" or "done") && !isFixAccept)
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
            case AwaitAcceptFix when invoked == "accept" || isFixAccept:
                var commits = _documents.AcceptFixDocuments(repo, state);
                return commits.Count == 0
                    ? "The light-track fix acceptance could not be committed.\n\n"
                    : $"The light-track fix documents are accepted ({string.Join(", ", commits.Select(WorkflowEngine.Short))}).\n\n";
            default:
                return "";
        }
    }

    private (string? Output, string Decision)? TryFormatPostAcceptMessage(
        NormalizedHookEvent ev,
        WorkflowRepo repo,
        string? rolesRoot,
        string prefix)
    {
        var updatedState = _advancer.ReadState(repo, rolesRoot, out _);
        if (updatedState is null)
        {
            return null;
        }

        var nextStep = WorkflowEngine.Next(updatedState);
        if (nextStep is WriteDocument nextWrite)
        {
            ClearRunIfOwned(repo, ev);
            return EmitStartContext(ev, repo, prefix + $"Tell the person the next step is {WorkflowEngine.Describe(nextStep)}. Then end your turn.", $"accept: next {nextStep.Kind} {nextWrite.Document.ToWireName()}");
        }

        if (nextStep is Implement)
        {
            ClearRunIfOwned(repo, ev);
            return EmitStartContext(ev, repo, prefix + $"Tell the person the plan is accepted and the next step is {WorkflowEngine.Describe(nextStep)}. Then end your turn.", "accept: next Implement");
        }

        return null;
    }

    private void ClearRunIfOwnedOrDone(WorkflowRepo repo, NormalizedHookEvent ev, Step step)
    {
        if (step is Done)
        {
            TurnState.ClearTurnFiles(repo, _fs);
            TurnState.TryDelete(repo.RunMarkerPath, _fs);
        }
        else if (step is Handoff)
        {
            ClearRunIfOwned(repo, ev);
        }
    }

    private void ClearRunIfOwned(WorkflowRepo repo, NormalizedHookEvent ev)
    {
        if (_fs.FileExists(repo.RunMarkerPath)
            && repo.ReadRunMarker() is { } marker
            && HookSessionScope.IsParentRunSession(marker, ev))
        {
            TurnState.ClearTurnFiles(repo, _fs);
            TurnState.TryDelete(repo.RunMarkerPath, _fs);
        }
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
