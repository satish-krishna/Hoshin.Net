using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class NamingCommand : IHoshinCommand
{
    public string Name => "naming";
    public string Usage => "naming --json <marker> <system> [--type t --scope s --change c --title t]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count < 3 || args[0] != "--json")
        {
            throw new UsageException();
        }

        var markerPath = args[1];
        var system = args[2];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 3; i < args.Count; i += 2)
        {
            var rawKey = args[i];
            if (!rawKey.StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Count)
            {
                throw new UsageException();
            }

            var key = rawKey[2..];
            if (key is not ("type" or "scope" or "change" or "title"))
            {
                throw new UsageException();
            }

            values[key] = args[i + 1];
        }

        var read = NamingService.ReadNaming(markerPath, system);
        if (!read.Declared || read.Setting is null)
        {
            CliIo.PrintJson(new { declared = false, setting = (object?)null, problems = Array.Empty<string>() });
            return Task.FromResult(ExitCodes.Pass);
        }

        var problems = NamingService.CheckNaming(read.Setting);
        var settingObj = new Dictionary<string, string>();
        if (read.Setting.Branch is not null)
        {
            settingObj["branch"] = read.Setting.Branch;
        }

        if (read.Setting.PullRequestTitle is not null)
        {
            settingObj["pullRequestTitle"] = read.Setting.PullRequestTitle;
        }

        var output = new Dictionary<string, object?>
        {
            ["declared"] = true,
            ["setting"] = settingObj,
            ["problems"] = problems,
        };

        if (problems.Count == 0 &&
            values.TryGetValue("type", out var type) &&
            values.TryGetValue("scope", out var scope) &&
            values.TryGetValue("change", out var change))
        {
            values.TryGetValue("title", out var title);
            output["names"] = NamingService.RenderNaming(read.Setting, new NamingValues(type, scope, change, title));
        }

        CliIo.PrintJson(output);
        return Task.FromResult(problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}

public sealed class BranchNameCommand : IHoshinCommand
{
    public string Name => "branch-name";
    public string Usage => "branch-name --json <name>";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || args[0] != "--json")
        {
            throw new UsageException();
        }

        var problems = NamingService.CheckBranchName(args[1]);
        CliIo.PrintJson(new { problems });
        return Task.FromResult(problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}

public sealed class ScopeCommand : IHoshinCommand
{
    public string Name => "scope";
    public string Usage => "scope --json <value>";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 2 || args[0] != "--json")
        {
            throw new UsageException();
        }

        var problems = NamingService.CheckScope(args[1]);
        CliIo.PrintJson(new { problems });
        return Task.FromResult(problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems);
    }
}
