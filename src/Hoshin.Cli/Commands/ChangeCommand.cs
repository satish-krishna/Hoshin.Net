using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli.Commands;

public sealed class ChangeCommand(
    IWorkspaceResolver resolver,
    IGitWorktrees gitWorktrees,
    IGitBranches gitBranches,
    IGitQuery gitQuery,
    MarkerReader markerReader,
    ChangeLocator changeLocator,
    EventRunner eventRunner,
    ScopeRules scopeRules,
    ChangeRenameService changeRename,
    ChangeRemoveService changeRemove,
    ChangeRecordStore changeRecords,
    IPullRequestClient pullRequests,
    AppService apps) : IHoshinCommand
{
    private readonly IWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IGitBranches _gitBranches = gitBranches ?? throw new ArgumentNullException(nameof(gitBranches));
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly MarkerReader _markerReader = markerReader ?? throw new ArgumentNullException(nameof(markerReader));
    private readonly ChangeLocator _changeLocator = changeLocator ?? throw new ArgumentNullException(nameof(changeLocator));
    private readonly EventRunner _eventRunner = eventRunner ?? throw new ArgumentNullException(nameof(eventRunner));
    private readonly ScopeRules _scopeRules = scopeRules ?? throw new ArgumentNullException(nameof(scopeRules));
    private readonly ChangeRenameService _changeRename = changeRename ?? throw new ArgumentNullException(nameof(changeRename));
    private readonly ChangeRemoveService _changeRemove = changeRemove ?? throw new ArgumentNullException(nameof(changeRemove));
    private readonly ChangeRecordStore _changeRecords = changeRecords ?? throw new ArgumentNullException(nameof(changeRecords));
    private readonly IPullRequestClient _pullRequests = pullRequests ?? throw new ArgumentNullException(nameof(pullRequests));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));

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
        var r = _resolver.Resolve(Directory.GetCurrentDirectory(), new ResolveOptions(o.Get("master"), o.Get("workspace")));
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
        var master = _gitWorktrees.MainCheckout(resolvedMaster);
        if (!_markerReader.ReadMarker(Path.Combine(master, MarkerConstants.FileName)).Systems.ContainsKey(system))
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
            Branch = _gitBranches.CurrentBranch(master),
            Base = _gitBranches.BaseBranch(master),
            Head = _gitQuery.CommitOf(master, "HEAD"),
            Repos = _changeLocator.SystemRepos(master, workspace, system),
            Extra = extra,
        };

        var outcome = await _eventRunner.RunAsync(ctx).ConfigureAwait(false);
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
        var found = _changeLocator.FindChange(master, workspace, change, o.Get("system"));
        var repos = _changeLocator.SystemRepos(found.Master, workspace, found.System);
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
        var scope = _scopeRules.ChangeScope(c.Master, c.System, c.Folder);
        var res = _changeRename.Rename(new RenameInput(c.Master, c.Workspace, c.System, c.Change, c.Repos, to, scope));
        CliIo.PrintJson(new { change = c.Change, from = res.From, to, renamed = res.Renamed, problems = res.Problems });
        return res.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems;
    }

    private int Remove(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "system", "master", "workspace"], flags: ["json", "abandon"]);
        var c = Locate(o);
        var copy = Path.Combine(ChangeLocator.CopiesFolder(c.Workspace), c.Change);
        var res = _changeRemove.Remove(new RemoveInput(c.Master, c.Workspace, c.System, c.Change, c.Repos, copy, o.Has("abandon")));
        CliIo.PrintJson(new { change = c.Change, problems = res.Problems, removed = res.Removed, kept = res.Kept });
        return res.Problems.Count == 0 ? ExitCodes.Pass : ExitCodes.Problems;
    }

    private int Show(IReadOnlyList<string> args)
    {
        var o = OptionParser.Parse(args, values: ["change", "system", "master", "workspace"], flags: ["json", "refresh"]);
        var c = Locate(o);
        var record = _changeRecords.Read(c.Folder);
        Dictionary<string, string?>? states = o.Has("refresh") && record is not null
            ? record.PullRequests.ToDictionary(p => p.Url, p => _pullRequests.StateOf(c.Master, p.Url), StringComparer.Ordinal)
            : null;

        var single = _apps.IsSingleRepo(c.Master);
        var repos = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in c.Repos)
        {
            var path = _apps.RepoPathOf(c.Master, c.Workspace, c.System, name);
            var root = single ? c.Master : path;
            var checkedOut = Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));
            repos[name] = new
            {
                path,
                checkedOut,
                branch = checkedOut ? _gitBranches.CurrentBranch(root) : null,
                @base = checkedOut ? _gitBranches.BaseBranch(root) : null,
            };
        }

        var output = new Dictionary<string, object?>
        {
            ["change"] = c.Change,
            ["system"] = c.System,
            ["master"] = c.Master,
            ["branch"] = _changeRecords.BranchOf(c.Folder),
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
