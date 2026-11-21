using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class HooksCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "hooks";
    public string Usage => "hooks --json --system <system> [--event <event>] [--master p] [--workspace p]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["system", "event", "master", "workspace"], flags: ["json"]);
        var system = o.Get("system");
        var ev = o.Get("event");
        if (!o.Has("json") || system is null || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        if (ev is not null && !HookConfigService.Events.Contains(ev))
        {
            throw new UsageException();
        }

        var r = services.Resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(o.Get("master"), o.Get("workspace")));
        if (r.Master is null)
        {
            throw new CouldNotRunException(string.Join('\n', r.Problems));
        }

        if (!MarkerReader.ReadMarker(Path.Combine(r.Master, MarkerConstants.FileName)).Systems.ContainsKey(system))
        {
            throw new CouldNotRunException($"the marker lists no system {system}");
        }

        var config = services.HookConfig.ReadFromBaseBranch(r.Master, system);
        var list = config.Hooks.Where(h => ev is null || h.Event == ev).ToList();
        CliIo.PrintJson(new
        {
            revision = config.Revision,
            gateRetries = config.GateRetries,
            hooks = list,
            problems = config.Problems,
            warnings = HookConfigService.Deprecations(list),
        });
        return Task.FromResult(config.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
