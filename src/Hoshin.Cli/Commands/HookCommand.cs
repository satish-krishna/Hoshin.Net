using System.Diagnostics;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Commands;

/// <summary>
/// Dual-harness lifecycle hook entry point (<c>hoshin hook &lt;start|result|subagent-stop|stop|log&gt; [--roles &lt;dir&gt;]</c>).
/// Always exits <c>0</c> and reports any internal failure to <c>stderr</c>, <c>hooks.jsonl</c>, and harness output so hooks never crash an active session.
/// </summary>
public sealed class HookCommand(
    HarnessAdapter hookAdapter,
    HookOrchestrator hookOrchestrator,
    IHookRunResolver runResolver) : IHoshinCommand
{
    private readonly HarnessAdapter _hookAdapter = hookAdapter ?? throw new ArgumentNullException(nameof(hookAdapter));
    private readonly HookOrchestrator _hookOrchestrator = hookOrchestrator ?? throw new ArgumentNullException(nameof(hookOrchestrator));
    private readonly IHookRunResolver _runResolver = runResolver ?? throw new ArgumentNullException(nameof(runResolver));

    public string Name => "hook";
    public string Usage => "hook start|result|subagent-stop|stop [--roles <path>]";

    public Task<int> RunAsync(IReadOnlyList<string> args) =>
        Task.FromResult(Execute(args, CliIo.ReadStdinUtf8, HarnessAdapter.WriteStdoutUtf8, CliIo.WriteStderrLine));

    public int Execute(
        IReadOnlyList<string> args,
        Func<string> readStdin,
        Action<string?> writeStdout,
        Action<string> writeStderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readStdin);
        ArgumentNullException.ThrowIfNull(writeStdout);
        ArgumentNullException.ThrowIfNull(writeStderr);

        var clock = Stopwatch.StartNew();
        string? sub = null;
        NormalizedHookEvent? ev = null;
        WorkflowRepo? repo = null;
        try
        {
            if (args.Count == 0)
            {
                writeStderr($"hoshin hook: missing subcommand (expected start, result, subagent-stop, or stop)");
                return ExitCodes.Pass;
            }

            sub = args[0];
            var o = OptionParser.Parse(args.Skip(1).ToList(), values: ["roles"]);
            var rolesRoot = o.Get("roles") ?? DefaultRolesDir();

            ev = _hookAdapter.Parse(readStdin().TrimStart('\uFEFF'));
            repo = _runResolver.Resolve(ev)?.WorkflowRepo;

            var (output, decision) = Dispatch(sub, ev, rolesRoot);

            writeStdout(output);
            _hookAdapter.AppendTrace(repo, $"hook {sub}", ev, output, decision, clock);
        }
        catch (Exception e)
        {
            var errorDetail = $"{e.GetType().Name}: {e.Message}";
            writeStderr($"hoshin hook: {errorDetail}");

            if (ev is not null && sub is not null)
            {
                var errorOutput = BuildCrashOutput(sub, ev, errorDetail);
                if (errorOutput is not null)
                {
                    writeStdout(errorOutput);
                }

                _hookAdapter.AppendTrace(repo, $"hook {sub}", ev, errorOutput, $"error: {errorDetail}", clock);
            }
        }

        return ExitCodes.Pass;
    }

    private (string? Output, string Decision) Dispatch(string sub, NormalizedHookEvent ev, string? rolesRoot) => sub switch
    {
        "start" => _hookOrchestrator.HandleStart(ev, rolesRoot),
        "result" => _hookOrchestrator.HandleResult(ev, rolesRoot),
        "subagent-stop" => _hookOrchestrator.HandleSubagentStop(ev, rolesRoot),
        "stop" => _hookOrchestrator.HandleStop(ev, rolesRoot),
        "log" => (_hookAdapter.EmptyOutput(ev.Harness), "logged"),
        _ => (_hookAdapter.EmptyOutput(ev.Harness), $"unknown subcommand {sub}"),
    };

    private string? BuildCrashOutput(string sub, NormalizedHookEvent ev, string errorDetail)
    {
        var message = $"hoshin hit an internal error in `{sub}`: `{errorDetail}`. Tell the engineer; don't retry.";
        return sub switch
        {
            "start" or "result" => _hookAdapter.ContextOutput(ev, message),
            "stop" => ev.IsAntigravity
                ? _hookAdapter.ContextOutput(ev, message)
                : _hookAdapter.SystemMessageOutput(ev.Harness, message),
            _ => null,
        };
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
