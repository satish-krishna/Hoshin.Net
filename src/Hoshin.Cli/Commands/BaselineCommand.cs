using Hoshin.Workflow.Baseline;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class BaselineCommand(
    EventContextFactory eventContext,
    BaselineTaker baselineTaker,
    HookConfigService hookConfig,
    BaselineApprovals baselineApprovals) : IHoshinCommand
{
    private readonly EventContextFactory _eventContext = eventContext ?? throw new ArgumentNullException(nameof(eventContext));
    private readonly BaselineTaker _baselineTaker = baselineTaker ?? throw new ArgumentNullException(nameof(baselineTaker));
    private readonly HookConfigService _hookConfig = hookConfig ?? throw new ArgumentNullException(nameof(hookConfig));
    private readonly BaselineApprovals _baselineApprovals = baselineApprovals ?? throw new ArgumentNullException(nameof(baselineApprovals));

    public string Name => "baseline";
    public string Usage => "baseline show|retake|approve --json --change c --repo r [--hook id] [--system s] [--master p] [--workspace p]";

    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "repo", "hook", "system", "master", "workspace"], flags: ["json"]);
        if (o.Positional.Count != 1 || o.Positional[0] is not ("show" or "retake" or "approve") ||
            !o.Has("json") || o.Get("change") is null || o.Get("repo") is null)
        {
            throw new UsageException();
        }

        var verb = o.Positional[0];
        var hookId = o.Get("hook");
        if ((verb == "approve") != (hookId is not null))
        {
            throw new UsageException();
        }

        var ctx = _eventContext.Build(
            Directory.GetCurrentDirectory(),
            new EventContextArgs("build.finish", o.Get("change")!, o.Get("system"), o.Get("repo")!, new ResolveOptions(o.Get("master"), o.Get("workspace")), new Dictionary<string, string>()),
            _baselineTaker.RecoverInterrupted);
        ctx.CheckProblems = [];

        var config = _hookConfig.ReadFromBaseBranch(ctx.Master, ctx.System);
        if (config.Problems.Count > 0)
        {
            return PrintTaken(null, new TakenBaseline(null, null, false, "could-not-run", config.Problems.Select(p => $"the hook configuration on the base branch: {p}").ToList()));
        }

        if (verb == "approve")
        {
            var stored = _baselineTaker.Stored(ctx, config);
            var problems = stored.Problems.Count > 0 ? stored.Problems : _baselineApprovals.Approve(ctx, stored.Entry, hookId!);
            CliIo.PrintJson(new
            {
                approved = problems.Count == 0 && stored.Base is not null ? new { hook = hookId, @base = stored.Base } : null,
                problems,
            });
            return problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems;
        }

        var taken = verb == "retake"
            ? await _baselineTaker.TakeAsync(ctx, config, retake: true).ConfigureAwait(false)
            : _baselineTaker.Stored(ctx, config);
        return PrintTaken(ctx, taken, ctx.CheckProblems);
    }

    private int PrintTaken(EventContext? ctx, TakenBaseline taken, List<string>? extra = null)
    {
        var hooks = taken.Entry?.Hooks ?? [];
        var approved = ctx is not null && taken.Base is not null
            ? BaselineApprovals.ApprovedHooks(_baselineApprovals.LedgerOf(ctx), taken.Base)
            : [];
        var needed = taken.Entry is not null ? BaselineApprovals.NeedsApproval(taken.Entry, approved) : [];
        var allProblems = taken.Problems.Concat(extra ?? []).ToList();
        CliIo.PrintJson(new
        {
            @base = taken.Base,
            taken = taken.Entry is not null,
            reused = taken.Reused,
            hooks,
            needsApproval = needed,
            approved,
            problems = allProblems,
        });
        return taken.Outcome switch
        {
            "could-not-run" => ExitCodes.CouldNotRun,
            "passed" => ExitCodes.Pass,
            _ => ExitCodes.Problems,
        };
    }
}
