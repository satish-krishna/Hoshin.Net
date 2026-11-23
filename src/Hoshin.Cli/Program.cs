using Hoshin.Cli.Commands;
using Hoshin.Workflow.Common;

namespace Hoshin.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new HoshinServices();
        (string Name, Func<IHoshinCommand> Factory)[] registry =
        [
            ("check", () => new CheckCommand(services.DocumentCheckService)),
            ("read", () => new ReadCommand(services.DocumentReader, services.MarkerReader, services.FileSystem)),
            ("fix-state", () => new FixStateCommand(services.FixState)),
            ("state", () => new StateCommand(services.ChangeState)),
            ("list", () => new ListCommand(services.ChangeList)),
            ("stale", () => new StaleCommand(services.StaleChecker)),
            ("naming", () => new NamingCommand()),
            ("branch-name", () => new BranchNameCommand()),
            ("scope", () => new ScopeCommand()),
            ("resolve", () => new ResolveCommand(services.Resolver)),
            ("instructions", () => new InstructionsCommand(services.Resolver, services.MarkerReader)),
            ("migrate", () => new MigrateCommand(services.DocumentMigrator, services.Migrator)),
            ("run", () => new RunCommand(services.EventContext, services.BaselineTaker, services.EventRunner)),
            ("hooks", () => new HooksCommand(services.Resolver, services.MarkerReader, services.HookConfig)),
            ("status", () => new StatusCommand(services.Resolver, services.ChangeLocator, services.Apps, services.Ledger, services.Git, services.Git)),
            ("checks", () => new ChecksCommand(services.Resolver, services.ChangeLocator, services.Git)),
            ("change", () => new ChangeCommand(services.Resolver, services.Git, services.Git, services.Git, services.MarkerReader, services.ChangeLocator, services.EventRunner, services.ScopeRules, services.ChangeRename, services.ChangeRemove, services.ChangeRecords, services.PullRequests, services.Apps)),
            ("commit", () => new CommitCommand(services.Committer)),
            ("gate", () => new GateCommand(services.Git, services.Git, services.Git, services.BuildStartStep, services.ChangeRecords, services.Progress, services.Apps, services.Ledger, services.HookConfig)),
            ("guard", () => new GuardCommand(services.HookAdapter, services.AllowListedRunner, services.FileSystem, services.TimeProvider)),
            ("hook", () => new HookCommand(services.HookAdapter, services.HookOrchestrator, services.AllowListedRunner, services.FileSystem)),
            ("baseline", () => new BaselineCommand(services.EventContext, services.BaselineTaker, services.HookConfig, services.BaselineApprovals)),
            ("red", () => new RedCommand(services.EventContext, services.BaselineTaker, services.RedVerdict, services.RedRecorder)),
        ];

        var byName = registry.ToDictionary(c => c.Name, c => c.Factory, StringComparer.Ordinal);
        string UsageText() => "usage: " + string.Join("\n       ", registry.Select(c => $"dotnet hoshin.dll {c.Factory().Usage}"));

        try
        {
            if (args.Length == 0 || !byName.TryGetValue(args[0], out var factory))
            {
                throw new UsageException();
            }

            return await factory().RunAsync(args[1..]).ConfigureAwait(false);
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
