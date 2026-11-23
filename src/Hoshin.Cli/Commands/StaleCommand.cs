using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class StaleCommand(StaleChecker staleChecker) : IHoshinCommand
{
    private readonly StaleChecker _staleChecker = staleChecker ?? throw new ArgumentNullException(nameof(staleChecker));

    public string Name => "stale";
    public string Usage => "stale --json --system <s> [--repo <repo>]... [--master <path>] [--workspace <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var repos = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != "--repo")
            {
                rest.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new UsageException();
            }

            repos.Add(args[++i]);
        }

        var o = OptionParser.Parse(rest, values: ["system", "master", "workspace"], flags: ["json"]);
        var system = o.Get("system");
        if (!o.Has("json") || o.Positional.Count > 0 || system is null)
        {
            throw new UsageException();
        }

        var result = _staleChecker.Check(
            Directory.GetCurrentDirectory(),
            new StaleOptions(system, repos, o.Get("master"), o.Get("workspace")));
        CliIo.PrintJson(result);
        return Task.FromResult(ExitCodes.Pass);
    }
}
