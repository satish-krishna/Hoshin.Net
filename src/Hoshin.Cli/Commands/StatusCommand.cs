using Hoshin.Workflow.Common;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class StatusCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "status";
    public string Usage => "status --json --change <change> [--system s] [--master p] [--workspace p]";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "system", "master", "workspace"], flags: ["json"]);
        var change = o.Get("change");
        if (!o.Has("json") || change is null || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var r = services.Resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(o.Get("master"), o.Get("workspace")));
        if (r.Master is null || r.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', r.Problems));
        }

        var found = services.ChangeLocator.FindChange(r.Master, r.Workspace, change, o.Get("system"));
        var folder = Path.Combine(found.Master, "intent", found.System, change);
        var single = services.Apps.IsSingleRepo(found.Master);
        var apps = single ? services.Apps.AppFolders(found.Master, found.System) : new Dictionary<string, string>();
        var ledgers = services.Ledger.ReadAll(services.Ledger.LedgerFolder(found.Master, found.System, change));

        var repos = ledgers.Select(l =>
        {
            var head = single && apps.ContainsKey(l.Repo)
                ? services.Apps.CurrentAppHead(found.Master, found.Master, found.System, l.Repo)
                : services.Git.CommitOf(l.Path, "HEAD");
            var runs = l.Runs.Select(run =>
            {
                var now = run.Event == "doc.accept"
                    ? services.Git.Out(found.Master, ["log", "-1", "--format=%H", "--", Path.Combine(folder, run.Subject ?? "")])
                    : head;
                var dict = new Dictionary<string, object?>
                {
                    ["event"] = run.Event,
                    ["repo"] = run.Repo,
                    ["key"] = run.Key,
                    ["subject"] = run.Subject,
                    ["outcome"] = run.Outcome,
                    ["at"] = run.At,
                    ["hooks"] = run.Hooks,
                    ["problems"] = run.Problems,
                    ["failed"] = run.Failed,
                };
                if (run.Details is not null)
                {
                    dict["details"] = run.Details;
                }

                dict["current"] = run.Key == now;
                return dict;
            }).ToList();

            return new { repo = l.Repo, path = l.Path, head, runs };
        }).ToList();

        CliIo.PrintJson(new { system = found.System, change, repos });
        return Task.FromResult(ExitCodes.Pass);
    }
}
