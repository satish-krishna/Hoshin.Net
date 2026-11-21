using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class MigrateCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "migrate";
    public string Usage => "migrate --json [--write] [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["master", "workspace"], flags: ["json", "write"]);
        if (!o.Has("json") || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var result = services.Migrator.Migrate(
            Directory.GetCurrentDirectory(),
            new ResolveOptions(o.Get("master"), o.Get("workspace")),
            o.Has("write"));

        CliIo.PrintJson(new
        {
            workspace = result.Workspace,
            master = result.Master,
            layout = result.Layout.ToWireName(),
            write = result.Write,
            steps = result.Steps,
            kept = result.Kept,
            manual = result.Manual,
            problems = result.Problems,
        });
        return Task.FromResult(result.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
