using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Pure snapshot of where a workflow run stands, computed from <see cref="ChangeStateService"/>, git, JSONC change documents,
/// and the unified <see cref="RepoLedger"/> in <c>.git/hoshin/</c>. Holds nothing owned by any specific harness.
/// </summary>
public sealed record WorkflowState(
    string RepoRoot,
    string ChangeFolder,
    WorkflowPlan Plan,
    string Head,
    RepoLedger Ledger,
    string? SkillsRoot,
    WorkflowDocState Intent,
    WorkflowDocState Spec,
    WorkflowDocState PlanDoc,
    string? CodeHead = null,
    ChangeStateItem? ChangeState = null,
    Dictionary<string, RepoLedger>? RepoLedgers = null,
    Dictionary<string, string>? RepoPaths = null,
    bool IsLightTrack = false)
{
    public string EffectiveCodeHead => string.IsNullOrWhiteSpace(CodeHead) ? Head : CodeHead;

    public IReadOnlyList<WorkflowVerdict> VerdictsForTask(WorkflowTask task)
    {
        if (!string.IsNullOrWhiteSpace(task.Repo)
            && RepoLedgers is not null
            && RepoLedgers.TryGetValue(task.Repo, out var repoLedger))
        {
            var repoVerdicts = repoLedger.WorkflowVerdicts.Where(v => v.Task == task.Id).ToList();
            if (repoVerdicts.Count > 0)
            {
                return repoVerdicts;
            }
        }

        return Ledger.WorkflowVerdicts.Where(v => v.Task == task.Id).ToList();
    }

    public string RepositoryPathFor(WorkflowTask task) =>
        !string.IsNullOrWhiteSpace(task.Repo)
        && RepoPaths is not null
        && RepoPaths.TryGetValue(task.Repo, out var path)
            ? path
            : RepoRoot;

    public WorkflowDocState Document(WorkflowDocument document) => document switch
    {
        WorkflowDocument.Intent => Intent,
        WorkflowDocument.Spec => Spec,
        WorkflowDocument.Plan => PlanDoc,
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown workflow document."),
    };
}
