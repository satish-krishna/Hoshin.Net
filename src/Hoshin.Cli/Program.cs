using Hoshin.Cli.Commands;
using Hoshin.Workflow.Common;

namespace Hoshin.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new HoshinServices();
        IHoshinCommand[] commands =
        [
            new CheckCommand(services),
            new ReadCommand(services),
            new FixStateCommand(services),
            new StateCommand(services),
            new ListCommand(services),
            new StaleCommand(services),
            new NamingCommand(),
            new BranchNameCommand(),
            new ScopeCommand(),
            new ResolveCommand(services),
            new InstructionsCommand(services),
            new MigrateCommand(services),
            new RunCommand(services),
            new HooksCommand(services),
            new StatusCommand(services),
            new ChecksCommand(services),
            new ChangeCommand(services),
            new CommitCommand(services),
            new GateCommand(services),
            new GuardCommand(),
            new HookCommand(services),
            new BaselineCommand(services),
            new RedCommand(services),
        ];

        var byName = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
        string UsageText() => "usage: " + string.Join("\n       ", commands.Select(c => $"dotnet hoshin.dll {c.Usage}"));

        try
        {
            if (args.Length == 0 || !byName.TryGetValue(args[0], out var cmd))
            {
                throw new UsageException();
            }

            return await cmd.RunAsync(args[1..]).ConfigureAwait(false);
        }
        catch (UsageException)
        {
            CliIo.WriteStderrLine(UsageText());
            return ExitCodes.CouldNotRun;
        }
        catch (CouldNotRunException e)
        {
            CliIo.WriteStderrLine(e.Message);
            return ExitCodes.CouldNotRun;
        }
        catch (Exception e)
        {
            CliIo.WriteStderrLine($"internal error: {e}");
            return ExitCodes.CouldNotRun;
        }
    }
}
