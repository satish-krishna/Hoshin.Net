using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class RunCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "run";
    public string Usage => "run <event> --json --change <change> [--system s] [--repo r]... [--document f] [--task T<n>] [--again] [--check] [--stream] [--master p] [--workspace p]";

    public async Task<int> RunAsync(IReadOnlyList<string> args)
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

            if (i + 1 >= args.Count)
            {
                throw new UsageException();
            }

            repos.Add(args[++i]);
        }

        var o = OptionParser.Parse(
            rest,
            values: ["change", "system", "document", "task", "master", "workspace"],
            flags: ["json", "again", "check", "stream"]);

        if (o.Positional.Count != 1 || !o.Has("json") || o.Get("change") is null)
        {
            throw new UsageException();
        }

        var eventName = o.Positional[0];
        if (!HookConfigService.Events.Contains(eventName) || eventName == "change.start")
        {
            throw new UsageException();
        }

        if (repos.Count > 1 && (!o.Has("check") || !HookConfigService.PerRepositoryEvents.Contains(eventName)))
        {
            throw new UsageException();
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o.Get("document") is { } doc)
        {
            extra["document"] = doc;
        }

        if (o.Get("task") is { } task)
        {
            extra["task"] = task;
        }

        var resolveOptions = new ResolveOptions(o.Get("master"), o.Get("workspace"));
        var change = o.Get("change")!;
        var system = o.Get("system");

        if (o.Has("check"))
        {
            IReadOnlyList<string?> targetRepos = repos.Count > 0 ? repos.Cast<string?>().ToList() : [null];
            return await CheckAllAsync(eventName, change, system, resolveOptions, extra, targetRepos).ConfigureAwait(false);
        }

        var ctx = services.EventContext.Build(
            Directory.GetCurrentDirectory(),
            new EventContextArgs(eventName, change, system, repos.FirstOrDefault(), resolveOptions, extra),
            services.BaselineTaker.RecoverInterrupted);

        Action<StreamProgressEvent>? progress = o.Has("stream")
            ? p => CliIo.WriteStdoutLine(JsonSerializer.Serialize(p, CliIo.IgnoreNullJsonOptions))
            : null;

        var outcome = await services.EventRunner.RunAsync(ctx, new EventRunOptions(o.Has("again"), false, progress)).ConfigureAwait(false);
        if (o.Has("stream"))
        {
            var streamed = new Dictionary<string, object?>
            {
                ["type"] = "outcome",
                ["event"] = outcome.Event,
                ["system"] = outcome.System,
                ["change"] = outcome.Change,
                ["repo"] = outcome.Repo,
                ["key"] = outcome.Key,
                ["subject"] = outcome.Subject,
                ["outcome"] = outcome.Outcome,
                ["reused"] = outcome.Reused,
                ["problems"] = outcome.Problems,
                ["hooks"] = outcome.Hooks,
                ["failed"] = outcome.Failed,
                ["details"] = outcome.Details,
            };
            CliIo.PrintJson(streamed);
        }
        else
        {
            CliIo.PrintJson(outcome);
        }

        return outcome.Outcome switch
        {
            "passed" => ExitCodes.Pass,
            "could-not-run" => ExitCodes.CouldNotRun,
            _ => ExitCodes.Problems,
        };
    }

    private async Task<int> CheckAllAsync(
        string eventName,
        string change,
        string? systemArg,
        ResolveOptions resolveOptions,
        Dictionary<string, string> extra,
        IReadOnlyList<string?> repos)
    {
        var results = new List<(string Repo, string Outcome, List<string> Problems)>();
        string? resolvedSystem = null;

        foreach (var repo in repos)
        {
            try
            {
                var ctx = services.EventContext.Build(
                    Directory.GetCurrentDirectory(),
                    new EventContextArgs(eventName, change, systemArg, repo, resolveOptions, extra),
                    services.BaselineTaker.RecoverInterrupted);
                resolvedSystem ??= ctx.System;
                var o = await services.EventRunner.RunAsync(ctx, new EventRunOptions(CheckOnly: true)).ConfigureAwait(false);
                results.Add((ctx.Repo, o.Outcome, o.Problems));
            }
            catch (CouldNotRunException e)
            {
                results.Add((repo ?? "", "could-not-run", [e.Message]));
            }
        }

        var outcome = results.Any(r => r.Outcome == "could-not-run")
            ? "could-not-run"
            : results.Any(r => r.Outcome != "passed")
                ? "refused"
                : "passed";

        CliIo.PrintJson(new
        {
            @event = eventName,
            system = resolvedSystem,
            change,
            check = true,
            outcome,
            problems = results.SelectMany(r => r.Problems).ToList(),
            refusing = results.Where(r => r.Outcome == "refused").Select(r => r.Repo).ToList(),
            repos = results.Select(r => new { repo = r.Repo, outcome = r.Outcome, problems = r.Problems }).ToList(),
        });

        return outcome switch
        {
            "passed" => ExitCodes.Pass,
            "could-not-run" => ExitCodes.CouldNotRun,
            _ => ExitCodes.Problems,
        };
    }
}
