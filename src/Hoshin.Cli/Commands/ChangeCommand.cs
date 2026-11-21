using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ChangeCommand(HoshinServices services) : IHoshinCommand
{
    public string Name => "change";
    public string Usage => string.Join("\n       dotnet hoshin.dll ", [
        "change start --json --system s --change c --type t --scope s [--title t] [--branch name]",
        "change rename --json --change c --to name",
        "change remove --json --change c [--abandon]",
        "change show --json --change c [--refresh]",
    ]);

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new UsageException();
        }

        var rest = args.Skip(1).ToList();
        return args[0] switch
        {
            "start" => StartAsync(rest),
            "rename" => Task.FromResult(Rename(rest)),
            "remove" => Task.FromResult(Remove(rest)),
            "show" => Task.FromResult(Show(rest)),
            _ => throw new UsageException(),
        };
    }

    private (string Master, string Workspace) ResolvePlace(ParsedOptions o)
    {
        var r = services.Resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(o.Get("master"), o.Get("workspace")));
        if (r.Master is null || r.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', r.Problems));
        }

        return (r.Master, r.Workspace);
    }

    private async Task<int> StartAsync(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["system", "change", "type", "scope", "title", "branch", "master", "workspace"], flags: ["json"]);
        var system = o.Get("system");
        var change = o.Get("change");
        var type = o.Get("type");
        var scope = o.Get("scope");
        if (!o.Has("json") || system is null || change is null || type is null || scope is null || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var (resolvedMaster, workspace) = ResolvePlace(o);
        var master = services.Git.MainCheckout(resolvedMaster);
        if (!MarkerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName)).Systems.ContainsKey(system))
        {
            throw new CouldNotRunException($"the marker lists no system {system}");
        }

        var extra = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = type, ["scope"] = scope };
        if (o.Get("title") is { } title)
        {
            extra["title"] = title;
        }

        if (o.Get("branch") is { } branch)
        {
            extra["branch"] = branch;
        }

        var ctx = new EventContext
        {
            Event = "change.start",
            Workspace = workspace,
            Master = master,
            System = system,
            Change = change,
            Folder = Path.Combine(ChangeLocator.CopiesFolder(workspace), change, "intent", system, change),
            Repo = "master",
            RepoPath = master,
            Root = master,
            Branch = services.Git.CurrentBranch(master),
            Base = services.Git.BaseBranch(master),
            Head = services.Git.CommitOf(master, "HEAD"),
            Repos = services.ChangeLocator.SystemRepos(master, workspace, system),
            Extra = extra,
        };

        var outcome = await services.EventRunner.RunAsync(ctx).ConfigureAwait(false);
        CliIo.PrintJson(outcome);
        return outcome.Outcome switch
        {
            "passed" => ExitCodes.Pass,
            "could-not-run" => ExitCodes.CouldNotRun,
            _ => ExitCodes.Problems,
        };
    }

    private (string Workspace, string Master, string System, string Change, List<string> Repos, string Folder) Locate(ParsedOptions o)
    {
        var change = o.Get("change");
        if (!o.Has("json") || change is null || o.Positional.Count > 0)
        {
            throw new UsageException();
        }

        var (master, workspace) = ResolvePlace(o);
        var found = services.ChangeLocator.FindChange(master, workspace, change, o.Get("system"));
        var repos = services.ChangeLocator.SystemRepos(found.Master, workspace, found.System);
        var folder = Path.Combine(found.Master, "intent", found.System, change);
        return (workspace, found.Master, found.System, change, repos, folder);
    }

    private int Rename(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "system", "to", "master", "workspace"], flags: ["json"]);
        var to = o.Get("to");
        if (to is null)
        {
            throw new UsageException();
        }

        var c = Locate(o);
        var scope = ScopeRules.ChangeScope(c.Master, c.System, c.Folder);
        var res = services.ChangeRename.Rename(new RenameInput(c.Master, c.Workspace, c.System, c.Change, c.Repos, to, scope));
        CliIo.PrintJson(new { change = c.Change, from = res.From, to, renamed = res.Renamed, problems = res.Problems });
        return res.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems;
    }

    private int Remove(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "system", "master", "workspace"], flags: ["json", "abandon"]);
        var c = Locate(o);
        var copy = Path.Combine(ChangeLocator.CopiesFolder(c.Workspace), c.Change);
        var res = services.ChangeRemove.Remove(new RemoveInput(c.Master, c.Workspace, c.System, c.Change, c.Repos, copy, o.Has("abandon")));
        CliIo.PrintJson(new { change = c.Change, problems = res.Problems, removed = res.Removed, kept = res.Kept });
        return res.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems;
    }

    private int Show(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "system", "master", "workspace"], flags: ["json", "refresh"]);
        var c = Locate(o);
        var record = ChangeRecordStore.Read(c.Folder);
        Dictionary<string, string?>? states = o.Has("refresh") && record is not null
            ? record.PullRequests.ToDictionary(p => p.Url, p => services.PullRequests.StateOf(c.Master, p.Url), StringComparer.Ordinal)
            : null;

        var single = services.Apps.IsSingleRepo(c.Master);
        var repos = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in c.Repos)
        {
            var path = services.Apps.RepoPathOf(c.Master, c.Workspace, c.System, name);
            var root = single ? c.Master : path;
            var checkedOut = Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));
            repos[name] = new
            {
                path,
                checkedOut,
                branch = checkedOut ? services.Git.CurrentBranch(root) : null,
                @base = checkedOut ? services.Git.BaseBranch(root) : null,
            };
        }

        var output = new Dictionary<string, object?>
        {
            ["change"] = c.Change,
            ["system"] = c.System,
            ["master"] = c.Master,
            ["branch"] = ChangeRecordStore.BranchOf(c.Folder),
            ["record"] = record,
            ["repos"] = repos,
        };
        if (states is not null)
        {
            output["states"] = states;
        }

        CliIo.PrintJson(output);
        return ExitCodes.Pass;
    }
}
