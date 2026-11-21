using Hoshin.Workflow.Baseline;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Events.Steps;

/// <summary><c>build.finish</c>: verifies all tasks logged and commit rules, then judges checks against baseline.</summary>
public sealed class BuildFinishStep(
    IGitClient? git = null,
    ProgressService? progress = null,
    AppService? apps = null,
    EventContextFactory? contextFactory = null,
    BaselineTaker? baselineTaker = null,
    BaselineApprovals? approvals = null) : IEventStep
{
    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly ProgressService _progress = progress ?? ProgressService.Default;
    private readonly AppService _apps = apps ?? new AppService(git);
    private readonly EventContextFactory _contextFactory = contextFactory ?? new EventContextFactory(git);
    private readonly BaselineTaker _baselineTaker = baselineTaker ?? new BaselineTaker(git);
    private readonly BaselineApprovals _approvals = approvals ?? BaselineApprovals.Default;

    public string EventName => "build.finish";
    public bool ReusePassed => true;

    public Task<List<string>> CheckAsync(EventContext ctx)
    {
        var stuck = _baselineTaker.RecoverInterrupted(ctx.Root);
        if (stuck is not null)
        {
            return Task.FromResult(new List<string> { stuck });
        }

        ctx.Branch = _git.CurrentBranch(ctx.RepoPath);
        ctx.Head = _contextFactory.HeadOf(ctx);

        var missing = _progress.UnloggedTasks(ctx.Folder, ctx.Repo);
        if (missing is null)
        {
            return Task.FromResult(new List<string> { $"{ctx.Change} has no plan that reads cleanly" });
        }

        if (missing.Count > 0)
        {
            return Task.FromResult(new List<string> { $"{ctx.Repo} still has tasks without a build log entry: {string.Join(", ", missing)}" });
        }

        var branch = ChangeRecordStore.BranchOf(ctx.Folder);
        if (ctx.Branch != branch)
        {
            return Task.FromResult(new List<string> { $"{ctx.Repo} is on {ctx.Branch ?? "a detached HEAD"}, not on the change's branch {branch}" });
        }

        if (ctx.Base is null)
        {
            return Task.FromResult(new List<string> { $"{ctx.Repo} has no base branch on this machine" });
        }

        var broken = ScopeRules.CommitRuleProblems(
            _git.CommitsBeyondBase(ctx.Root, "HEAD", ctx.Base),
            ScopeRules.ChangeScope(ctx.Master, ctx.System, ctx.Folder));
        if (broken.Count > 0)
        {
            return Task.FromResult(new List<string> { $"commits on {branch} break the commit rules (references/workspace.md, section 8): {string.Join("; ", broken)}" });
        }

        return Task.FromResult(new List<string>());
    }

    public Dictionary<string, object?>? Idle(EventContext ctx)
    {
        if (!_apps.IsSingleRepo(ctx.Master) || ctx.Repo == "master" || ctx.Base is null)
        {
            return null;
        }

        return _apps.TouchedApps(ctx.Root, ctx.System, ctx.Master, ctx.Base, "HEAD").Contains(ctx.Repo)
            ? null
            : new Dictionary<string, object?> { ["untouched"] = true };
    }

    public async Task<StepResult> PrepareAsync(EventContext ctx, HookConfig config)
    {
        var taken = await _baselineTaker.TakeAsync(ctx, config).ConfigureAwait(false);
        if (taken.Problems.Count > 0)
        {
            var failed = taken.Fault is null ? null : new FailedHookInfo(taken.Fault.Id, "stop", taken.Fault.Output);
            return new StepResult(taken.Problems, Failed: failed, Outcome: taken.Outcome == "could-not-run" ? "could-not-run" : "failed");
        }

        if (taken.Entry is null || taken.Base is null)
        {
            return StepResult.Empty;
        }

        var baseCommit = taken.Base;
        var onBase = taken.Entry.Hooks.ToDictionary(h => h.Id, StringComparer.Ordinal);
        var approved = BaselineApprovals.ApprovedHooks(_approvals.LedgerOf(ctx), baseCommit);
        var fixedIds = new List<string>();
        var stillFailing = new List<Dictionary<string, object?>>();
        var approvedUsed = new List<string>();
        var summary = new Dictionary<string, object?>
        {
            ["base"] = baseCommit,
            ["reused"] = taken.Reused,
            ["fixed"] = fixedIds,
            ["stillFailing"] = stillFailing,
            ["approved"] = approvedUsed,
        };

        HookVerdict Judge(HookDefinition hook, HookRecord record)
        {
            var hasBefore = onBase.TryGetValue(hook.Id, out var before);
            var wasRed = hasBefore && before!.Outcome == "failed";
            if (record.Outcome == "passed")
            {
                if (wasRed)
                {
                    fixedIds.Add(hook.Id);
                }

                return new HookVerdict(true);
            }

            if (!hasBefore || !wasRed)
            {
                return new HookVerdict(false);
            }

            if (before!.Ids is null)
            {
                if (!approved.Contains(hook.Id))
                {
                    return new HookVerdict(false, _approvals.ApprovalProblem(ctx, baseCommit, hook.Id), "stop");
                }

                approvedUsed.Add(hook.Id);
                return new HookVerdict(true);
            }

            var now = record.Ids;
            if (now is null)
            {
                return new HookVerdict(
                    false,
                    $"check {hook.Id} failed on the base saying what failed ({string.Join(", ", before.Ids)}), and now fails without saying, so it could not be compared with the base");
            }

            var added = now.Where(id => !before.Ids.Contains(id)).ToList();
            if (added.Count > 0)
            {
                return new HookVerdict(
                    false,
                    $"check {hook.Id} has new failures since the base ({baseCommit[..Math.Min(7, baseCommit.Length)]}): {string.Join(", ", added)}");
            }

            stillFailing.Add(new Dictionary<string, object?> { ["id"] = hook.Id, ["ids"] = now });
            return new HookVerdict(true);
        }

        return new StepResult([], new Dictionary<string, object?> { ["baseline"] = summary }, Judge: Judge);
    }
}
