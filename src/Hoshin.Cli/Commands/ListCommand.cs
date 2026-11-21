using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ListCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "list";
    public string Usage => "list --json [--system <s>] [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["system", "master", "workspace"], flags: ["json"]);
        if (!o.Has("json") || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var result = services.ChangeList.List(
            Directory.GetCurrentDirectory(),
            new ResolveOptions(o.Get("master"), o.Get("workspace")),
            o.Get("system"));
        CliIo.PrintJson(result);
        return Task.FromResult(ExitCodes.Pass);
    }
}
