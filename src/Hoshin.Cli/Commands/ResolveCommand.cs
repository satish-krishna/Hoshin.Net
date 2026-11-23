using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ResolveCommand(IWorkspaceResolver resolver) : IHoshinCommand
{
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public string Name => "resolve";
    public string Usage => "resolve --json [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0] != "--json")
        {
            throw new UsageException();
        }

        string? master = null;
        string? workspace = null;
        for (var i = 1; i < args.Count; i += 2)
        {
            if (i + 1 >= args.Count)
            {
                throw new UsageException();
            }

            if (args[i] == "--master" && master is null)
            {
                master = args[i + 1];
            }
            else if (args[i] == "--workspace" && workspace is null)
            {
                workspace = args[i + 1];
            }
            else
            {
                throw new UsageException();
            }
        }

        var r = _resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(master, workspace));
        CliIo.PrintJson(new
        {
            workspace = r.Workspace,
            master = r.Master,
            masterIsCopy = r.MasterIsCopy,
            problems = r.Problems,
            layout = r.Layout.ToWireName(),
        });
        return Task.FromResult(r.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
