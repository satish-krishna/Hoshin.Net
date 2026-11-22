using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class RedCommand(HoshinServices services) : IHoshinCommand
{
    private static readonly Regex TaskRegex = new(@"^T\d+$", RegexOptions.Compiled);

    public string Name => "red";
    public string Usage => "red record|show --json --change c --repo r --task T<n> [--system s] [--master p] [--workspace p]";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "repo", "task", "system", "master", "workspace"], flags: ["json"]);
        var task = o.Get("task");
        if (o.Positional.Count != 1 || o.Positional[0] is not ("record" or "show") ||
            !o.Has("json") || o.Get("change") is null || o.Get("repo") is null || task is null || !TaskRegex.IsMatch(task))
        {
            throw new UsageException();
        }

        var ctx = services.EventContext.Build(
            Directory.GetCurrentDirectory(),
            new EventContextArgs("build.start", o.Get("change")!, o.Get("system"), o.Get("repo")!, new ResolveOptions(o.Get("master"), o.Get("workspace")), new Dictionary<string, string>()),
            services.BaselineTaker.RecoverInterrupted);

        if (o.Positional[0] == "show")
        {
            var verdict = services.RedVerdict.Verdict(ctx, task);
            CliIo.PrintJson(new
            {
                task,
                observed = verdict.Observed,
                reason = verdict.Reason,
                record = verdict.Record,
                problems = Array.Empty<string>(),
            });
            return ExitCodes.Pass;
        }

        var res = await services.RedRecorder.RecordAsync(ctx, task).ConfigureAwait(false);
        CliIo.PrintJson(new
        {
            task,
            commit = res.Record?.Commit,
            outcome = res.Record?.Outcome,
            exitCode = res.Record?.ExitCode,
            output = res.Record?.Output ?? "",
            problems = res.Problems,
        });

        if (res.Record is null || res.Record.Outcome is "passed" or "could-not-compile")
        {
            return ExitCodes.Problems;
        }

        return res.Record.Outcome == "failed" ? ExitCodes.Pass : ExitCodes.CouldNotRun;
    }
}
