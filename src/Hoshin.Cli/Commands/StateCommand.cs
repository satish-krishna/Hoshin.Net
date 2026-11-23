using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;

namespace Hoshin.Cli.Commands;

public sealed class StateCommand(ChangeStateService changeState) : IHoshinCommand
{
    private readonly ChangeStateService _changeState = changeState ?? throw new ArgumentNullException(nameof(changeState));

    public string Name => "state";
    public string Usage => "state --json [--system <s>] [--change <c> | --open] [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["system", "change", "master", "workspace"], flags: ["json", "open"]);
        if (!o.Has("json") || o.Positional.Count > 0 || (o.Has("open") && o.Get("change") is not null))
        {
            throw new UsageException();
        }

        var result = _changeState.Compute(
            Directory.GetCurrentDirectory(),
            new StateOptions(o.Get("master"), o.Get("workspace"), o.Get("system"), o.Get("change"), o.Has("open")));

        var wire = new
        {
            system = result.System,
            changes = result.Changes.Select(SerializeChange).ToList(),
            problems = result.Problems,
        };
        CliIo.PrintJson(wire);
        return Task.FromResult(ExitCodes.Pass);
    }

    private static Dictionary<string, object?> SerializeChange(ChangeStateItem c)
    {
        var dict = new Dictionary<string, object?>
        {
            ["change"] = c.Change,
            ["master"] = c.Master,
            ["folder"] = c.Folder,
            ["inCopy"] = c.InCopy,
            ["title"] = c.Title,
            ["track"] = c.Track,
            ["documents"] = c.Documents,
            ["scenarios"] = c.Scenarios,
            ["tasks"] = c.Tasks,
            ["schedule"] = c.Schedule,
            ["buildLogs"] = c.BuildLogs,
            ["review"] = c.Review,
            ["fix"] = c.Fix,
        };
        if (c.Apps is not null)
        {
            dict["apps"] = c.Apps;
        }

        dict["ready"] = c.Ready;
        dict["finished"] = c.Finished;
        return dict;
    }
}
