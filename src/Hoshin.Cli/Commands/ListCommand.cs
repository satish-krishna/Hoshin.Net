using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ListCommand(ChangeListService changeList) : IHoshinCommand
{
    private readonly ChangeListService _changeList = changeList ?? throw new ArgumentNullException(nameof(changeList));

    public string Name => "list";
    public string Usage => "list --json [--system <s>] [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["system", "master", "workspace"], flags: ["json"]);
        if (!o.Has("json") || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var result = _changeList.List(
            Directory.GetCurrentDirectory(),
            new ResolveOptions(o.Get("master"), o.Get("workspace")),
            o.Get("system"));
        CliIo.PrintJson(result);
        return Task.FromResult(ExitCodes.Pass);
    }
}
