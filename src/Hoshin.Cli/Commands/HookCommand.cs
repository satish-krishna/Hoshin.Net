using System.Diagnostics;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Commands;

/// <summary>
/// Dual-harness lifecycle hook entry point (<c>hoshin hook &lt;start|result|subagent-stop|stop|log&gt; [--roles &lt;dir&gt;]</c>).
/// Always exits <c>0</c> and reports any internal failure to <c>stderr</c> so hooks never crash an active session.
/// </summary>
public sealed class HookCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "hook";
    public string Usage => "hook start|result|subagent-stop|stop [--roles <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (args.Count == 0)
            {
                CliIo.WriteStderrLine($"hoshin hook: missing subcommand (expected start, result, subagent-stop, or stop)");
                return Task.FromResult(ExitCodes.Pass);
            }

            var sub = args[0];
            var o = OptionParser.Parse(args.Skip(1).ToList(), values: ["roles"]);
            var rolesRoot = o.Get("roles") ?? DefaultRolesDir();

            var ev = services.HookAdapter.ReadFromStdin();
            var repo = WorkflowRepo.Find(ev.Cwd ?? Directory.GetCurrentDirectory());

            var (output, decision) = sub switch
            {
                "start" => services.HookOrchestrator.HandleStart(ev, rolesRoot),
                "result" => services.HookOrchestrator.HandleResult(ev, rolesRoot),
                "subagent-stop" => services.HookOrchestrator.HandleSubagentStop(ev, rolesRoot),
                "stop" => services.HookOrchestrator.HandleStop(ev, rolesRoot),
                "log" => (services.HookAdapter.EmptyOutput(ev.Harness), "logged"),
                _ => (services.HookAdapter.EmptyOutput(ev.Harness), $"unknown subcommand {sub}"),
            };

            HarnessAdapter.WriteStdoutUtf8(output);
            HarnessAdapter.AppendTrace(repo, $"hook {sub}", ev, output, decision, clock);
        }
        catch (Exception e)
        {
            CliIo.WriteStderrLine($"hoshin hook: {e.Message}");
        }

        return Task.FromResult(ExitCodes.Pass);
    }

    private static string? DefaultRolesDir()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "skills"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "skills")),
        })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
