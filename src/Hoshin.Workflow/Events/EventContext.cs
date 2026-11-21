using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Events;

public sealed class EventContext
{
    public required string Event { get; init; }
    public required string Workspace { get; init; }
    public required string Master { get; set; }
    public required string System { get; init; }
    public required string Change { get; init; }
    public required string Folder { get; set; }
    public required string Repo { get; init; }
    public required string RepoPath { get; set; }
    public required string Root { get; init; }
    public string? Branch { get; set; }
    public string? Base { get; init; }
    public string? Head { get; set; }
    public required List<string> Repos { get; init; }
    public required Dictionary<string, string> Extra { get; init; }
    public List<string>? CheckProblems { get; set; }

    public EventContext CloneFor(string eventName, string repo, string repoPath, string root, string? branch, string? baseBranch, string? head) => new()
    {
        Event = eventName,
        Workspace = Workspace,
        Master = Master,
        System = System,
        Change = Change,
        Folder = Folder,
        Repo = repo,
        RepoPath = repoPath,
        Root = root,
        Branch = branch,
        Base = baseBranch,
        Head = head,
        Repos = Repos,
        Extra = new Dictionary<string, string>(StringComparer.Ordinal),
    };
}

public sealed record EventContextArgs(
    string Event,
    string Change,
    string? System,
    string? Repo,
    ResolveOptions ResolveOptions,
    Dictionary<string, string> Extra);

/// <summary>Builds <see cref="EventContext"/> and computes effective head & uncommitted files across layouts.</summary>
public sealed class EventContextFactory(
    IGitClient? git = null,
    IWorkspaceResolver? resolver = null,
    ChangeLocator? locator = null,
    AppService? apps = null)
{
    public static EventContextFactory Default { get; } = new();

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;
    private readonly IWorkspaceResolver _resolver = resolver ?? new WorkspaceResolver(git);
    private readonly ChangeLocator _locator = locator ?? new ChangeLocator(git);
    private readonly AppService _apps = apps ?? new AppService(git);

    public EventContext Build(string cwd, EventContextArgs args, Func<string, string?>? recoverInterrupted = null)
    {
        var resolution = _resolver.Resolve(cwd, args.ResolveOptions);
        if (resolution.Master is null || resolution.Workspace is null)
        {
            throw new CouldNotRunException(string.Join('\n', resolution.Problems));
        }

        var workspace = resolution.Workspace;
        if (recoverInterrupted is not null && _apps.IsSingleRepo(resolution.Master))
        {
            var copy = Path.Combine(ChangeLocator.CopiesFolder(workspace), args.Change);
            if (Directory.Exists(Path.Combine(copy, ".git")) || File.Exists(Path.Combine(copy, ".git")))
            {
                recoverInterrupted(copy);
            }
        }

        var found = _locator.FindChange(resolution.Master, workspace, args.Change, args.System);
        var repos = _locator.SystemRepos(found.Master, workspace, found.System);
        var single = _apps.IsSingleRepo(found.Master);

        var repo = "master";
        if (HookConfigService.PerRepositoryEvents.Contains(args.Event))
        {
            if (!string.IsNullOrEmpty(args.Repo))
            {
                repo = args.Repo;
            }
            else if (single)
            {
                var inferred = AppAt(found.Master, found.System, cwd)
                    ?? throw new CouldNotRunException($"{args.Event} runs in one repository: pass --repo <repo>, or run it from inside the repository");
                repo = inferred;
            }
            else
            {
                var top = _git.TopLevel(cwd);
                if (top is null || Path.GetDirectoryName(top) is not { } parent || !PathHelpers.PathsEqual(parent, workspace))
                {
                    throw new CouldNotRunException($"{args.Event} runs in one repository: pass --repo <repo>, or run it from inside the repository");
                }

                repo = Path.GetFileName(top);
            }

            if (repo != "master" && !repos.Contains(repo))
            {
                throw new CouldNotRunException($"{repo} is not {(single ? "an app" : "a repository")} of system {found.System} ({(repos.Count > 0 ? string.Join(", ", repos) : "none")})");
            }
        }

        var repoPath = repo == "master" ? found.Master : _apps.RepoPathOf(found.Master, workspace, found.System, repo);
        var root = single ? found.Master : repoPath;
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
        {
            throw new CouldNotRunException($"{repo} is not checked out in {workspace}");
        }

        var ctx = new EventContext
        {
            Event = args.Event,
            Workspace = workspace,
            Master = found.Master,
            System = found.System,
            Change = args.Change,
            Folder = Path.Combine(found.Master, "intent", found.System, args.Change),
            Repo = repo,
            RepoPath = repoPath,
            Root = root,
            Branch = _git.CurrentBranch(root),
            Base = _git.BaseBranch(root),
            Head = null,
            Repos = repos,
            Extra = args.Extra,
        };
        ctx.Head = HeadOf(ctx);
        return ctx;
    }

    public string? HeadOf(EventContext ctx)
    {
        if (ctx.Repo == "master" || !_apps.IsSingleRepo(ctx.Master))
        {
            return _git.CommitOf(ctx.RepoPath, "HEAD");
        }

        if (!_apps.AppFolders(ctx.Master, ctx.System).TryGetValue(ctx.Repo, out var folder) ||
            ctx.Base is null ||
            _git.CommitOf(ctx.Root, "HEAD") is null)
        {
            return null;
        }

        return _apps.AppHead(ctx.Root, "HEAD", folder, ctx.Base);
    }

    public List<string> UncommittedOf(EventContext ctx)
    {
        if (ctx.Repo == "master" || !_apps.IsSingleRepo(ctx.Master))
        {
            return OwnFiles.UncommittedFiles(_git, ctx.RepoPath);
        }

        return _apps.AppFolders(ctx.Master, ctx.System).TryGetValue(ctx.Repo, out var folder)
            ? _apps.UncommittedInApp(ctx.Root, folder)
            : OwnFiles.UncommittedFiles(_git, ctx.Root);
    }

    private string? AppAt(string master, string system, string cwd)
    {
        var here = PathHelpers.RealPath(cwd);
        foreach (var (name, folder) in _apps.AppFolders(master, system))
        {
            var appPath = PathHelpers.RealPath(folder == "." ? master : Path.Combine(master, folder));
            if (PathHelpers.PathsEqual(here, appPath) || PathHelpers.IsSubPathOf(here, appPath))
            {
                return name;
            }
        }

        return null;
    }
}
