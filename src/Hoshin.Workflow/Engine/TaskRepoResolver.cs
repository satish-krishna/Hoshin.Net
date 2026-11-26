using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>Resolves each plan task's target repository checkout and per-repository ledger for single-repo and multi-repo workspaces.</summary>
public interface ITaskRepoResolver
{
    WorkflowRepo Resolve(WorkflowRepo masterRepo, WorkflowTask task);

    WorkflowRepo ResolveByName(WorkflowRepo masterRepo, string repoName);

    Dictionary<string, string> ResolveRepoPaths(WorkflowRepo masterRepo, WorkflowPlan plan);

    Dictionary<string, RepoLedger> ReadRepoLedgers(WorkflowRepo masterRepo, WorkflowPlan plan);
}

/// <summary>Identity resolver for isolated unit tests that run all tasks inside one fake repository.</summary>
public sealed class IdentityTaskRepoResolver : ITaskRepoResolver
{
    public WorkflowRepo Resolve(WorkflowRepo masterRepo, WorkflowTask task) => masterRepo;

    public WorkflowRepo ResolveByName(WorkflowRepo masterRepo, string repoName) => masterRepo;

    public Dictionary<string, string> ResolveRepoPaths(WorkflowRepo masterRepo, WorkflowPlan plan) =>
        new(StringComparer.Ordinal);

    public Dictionary<string, RepoLedger> ReadRepoLedgers(WorkflowRepo masterRepo, WorkflowPlan plan) =>
        new(StringComparer.Ordinal);
}

/// <summary>Resolves multi-repo product checkouts and single-repo app ledgers through <see cref="IWorkspaceLocator"/> and <see cref="AppService"/>.</summary>
public sealed class TaskRepoResolver(
    IWorkspaceLocator workspaceLocator,
    AppService apps,
    IGitWorktrees gitWorktrees,
    IFileSystem fileSystem,
    LedgerStore ledgerStore) : ITaskRepoResolver
{
    private readonly IWorkspaceLocator _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    private readonly IGitWorktrees _gitWorktrees = gitWorktrees ?? throw new ArgumentNullException(nameof(gitWorktrees));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly LedgerStore _ledgerStore = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));

    public WorkflowRepo Resolve(WorkflowRepo masterRepo, WorkflowTask task)
    {
        ArgumentNullException.ThrowIfNull(masterRepo);
        ArgumentNullException.ThrowIfNull(task);
        return string.IsNullOrWhiteSpace(task.Repo)
            ? masterRepo
            : ResolveByName(masterRepo, task.Repo);
    }

    public WorkflowRepo ResolveByName(WorkflowRepo masterRepo, string repoName)
    {
        ArgumentNullException.ThrowIfNull(masterRepo);
        if (string.IsNullOrWhiteSpace(repoName) || string.Equals(repoName, masterRepo.RepoKey, StringComparison.Ordinal))
        {
            return masterRepo;
        }

        var resolution = _workspaceLocator.TryResolve(masterRepo.Root);
        if (resolution?.Master is null || resolution.Workspace is null)
        {
            return masterRepo;
        }

        if (_apps.IsSingleRepo(resolution.Master))
        {
            var common = _gitWorktrees.GitDirs(masterRepo.Root)?.Common ?? Path.GetDirectoryName(masterRepo.HoshinDir)!;
            return masterRepo.ForTarget(masterRepo.Root, common, repoName);
        }

        var targetPath = repoName == "master"
            ? masterRepo.Root
            : _apps.RepoPathOf(resolution.Master, resolution.Workspace, masterRepo.SystemKey, repoName);
        if (!_fs.DirectoryExists(targetPath))
        {
            return masterRepo;
        }

        var targetDirs = _gitWorktrees.GitDirs(targetPath);
        if (targetDirs is null)
        {
            return masterRepo;
        }

        return masterRepo.ForTarget(targetPath, targetDirs.Common, repoName);
    }

    public Dictionary<string, string> ResolveRepoPaths(WorkflowRepo masterRepo, WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(masterRepo);
        ArgumentNullException.ThrowIfNull(plan);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var repoName in DistinctTaskRepos(plan))
        {
            paths[repoName] = ResolveByName(masterRepo, repoName).Root;
        }

        return paths;
    }

    public Dictionary<string, RepoLedger> ReadRepoLedgers(WorkflowRepo masterRepo, WorkflowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(masterRepo);
        ArgumentNullException.ThrowIfNull(plan);

        var ledgers = new Dictionary<string, RepoLedger>(StringComparer.Ordinal);
        foreach (var repoName in DistinctTaskRepos(plan))
        {
            var existing = _ledgerStore.Read(masterRepo.LedgerFolder, repoName);
            if (existing is not null)
            {
                ledgers[repoName] = existing;
            }
        }

        return ledgers;
    }

    private static IEnumerable<string> DistinctTaskRepos(WorkflowPlan plan) =>
        plan.Tasks
            .Select(t => t.Repo)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.Ordinal);
}
