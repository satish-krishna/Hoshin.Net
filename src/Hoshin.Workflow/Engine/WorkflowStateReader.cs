using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Builds pure <see cref="WorkflowState"/> snapshots from <see cref="ChangeStateService"/> (documents with upstream pins,
/// readiness gates, and progress) when running in a Hoshin workspace, with a fallback for standalone <c>.hoshin/</c> repos.
/// </summary>
public sealed class WorkflowStateReader(
    PlanReader planReader,
    ChangeStateService changeState,
    ITaskRepoResolver taskRepoResolver,
    IWorkspaceLocator workspaceLocator,
    IFileSystem fileSystem)
{
    private readonly PlanReader _planReader = planReader ?? throw new ArgumentNullException(nameof(planReader));
    private readonly ChangeStateService _changeState = changeState ?? throw new ArgumentNullException(nameof(changeState));
    private readonly ITaskRepoResolver _taskRepos = taskRepoResolver ?? throw new ArgumentNullException(nameof(taskRepoResolver));
    private readonly IWorkspaceLocator _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public WorkflowState? Read(WorkflowRepo repo, string? skillsRoot = null, string? sessionRepo = null)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var changeItem = TryComputeChangeState(repo);
        var plan = repo.ReadPlan(_planReader) ?? BuildFallbackPlan(repo, changeItem);
        if (plan is null)
        {
            return null;
        }

        var (intent, spec, planDoc) = changeItem is not null
            ? DocumentsFromChangeState(repo.ChangeFolder, changeItem.Documents)
            : (repo.DocumentStatus("intent.md"), repo.DocumentStatus("spec.md"), repo.DocumentStatus("plan.md"));

        var repoLedgers = _taskRepos.ReadRepoLedgers(repo, plan);
        var repoPaths = _taskRepos.ResolveRepoPaths(repo, plan);
        var isLightTrack = string.Equals(changeItem?.Track, "fix", StringComparison.Ordinal)
            || string.Equals(plan.WorkType, "fix", StringComparison.OrdinalIgnoreCase)
            || repo.ReadStep()?.Kind is StepKind.Fix or StepKind.AwaitAcceptFix;

        return new WorkflowState(
            repo.Root,
            repo.ChangeFolder,
            plan,
            repo.Head(),
            repo.ReadLedger(),
            skillsRoot,
            intent,
            spec,
            planDoc,
            repo.CodeHead(),
            changeItem,
            repoLedgers,
            repoPaths,
            isLightTrack,
            sessionRepo);
    }

    private ChangeStateItem? TryComputeChangeState(WorkflowRepo repo)
    {
        if (!_workspaceLocator.HasWorkspaceMarker(repo.Root) || !IsIntentLayout(repo) || !HasIntentDocument(repo))
        {
            return null;
        }

        var computed = _changeState.Compute(repo.Root, new StateOptions(Change: repo.ChangeKey, System: repo.SystemKey));
        return computed.Changes.FirstOrDefault(c => string.Equals(c.Change, repo.ChangeKey, StringComparison.Ordinal));
    }

    private bool HasIntentDocument(WorkflowRepo repo)
    {
        var intentPath = Path.Combine(repo.ChangeFolder, "intent.md");
        if (_fs.FileExists(intentPath))
        {
            return true;
        }

        var relative = Path.GetRelativePath(repo.Root, intentPath).Replace('\\', '/');
        return repo.GitQuery.ExistsAt(repo.Root, "HEAD", relative);
    }

    private static bool IsIntentLayout(WorkflowRepo repo) =>
        ChangeLedgerPaths.TryExtractIntentSystemAndChange(repo.ChangeFolder, out _, out _);

    private static WorkflowPlan? BuildFallbackPlan(WorkflowRepo repo, ChangeStateItem? changeItem)
    {
        if (changeItem is null)
        {
            return null;
        }

        return new WorkflowPlan(
            Change: changeItem.Change,
            Scope: repo.SystemKey,
            Base: "main",
            Tasks: [],
            Checks: [],
            HasPlan: false);
    }

    private static (WorkflowDocState Intent, WorkflowDocState Spec, WorkflowDocState Plan) DocumentsFromChangeState(
        string changeFolder,
        DocumentsState docs)
    {
        var intent = new WorkflowDocState(
            Path.Combine(changeFolder, "intent.md"),
            docs.Intent.Exists,
            docs.Intent.InHead,
            docs.Intent.Uncommitted,
            docs.Intent.WorkingStatus,
            docs.Intent.HeadStatus);
        var spec = new WorkflowDocState(
            Path.Combine(changeFolder, "spec.md"),
            docs.Spec.Exists,
            docs.Spec.InHead,
            docs.Spec.Uncommitted,
            docs.Spec.WorkingStatus,
            docs.Spec.HeadStatus,
            docs.Spec.Pin);
        var plan = new WorkflowDocState(
            Path.Combine(changeFolder, "plan.md"),
            docs.Plan.Exists,
            docs.Plan.InHead,
            docs.Plan.Uncommitted,
            docs.Plan.WorkingStatus,
            docs.Plan.HeadStatus,
            docs.Plan.Pin);
        return (intent, spec, plan);
    }
}
