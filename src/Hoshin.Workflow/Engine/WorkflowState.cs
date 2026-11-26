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
    bool IsLightTrack = false,
    string? SessionRepo = null,
    string? BuildInstructions = null,
    List<ReviewFindingSummary>? HighReviewFindings = null)
{
    public string EffectiveCodeHead => string.IsNullOrWhiteSpace(CodeHead) ? Head : CodeHead;

    public IReadOnlyList<WorkflowVerdict> VerdictsForTask(WorkflowTask task) =>
        VerdictsForTaskId(task.Id, task.Repo);

    public IReadOnlyList<WorkflowVerdict> VerdictsForTaskId(string taskId, string? repoName)
    {
        if (!string.IsNullOrWhiteSpace(repoName)
            && RepoLedgers is not null
            && RepoLedgers.TryGetValue(repoName, out var repoLedger))
        {
            var repoVerdicts = repoLedger.WorkflowVerdicts.Where(v => v.Task == taskId).ToList();
            if (repoVerdicts.Count > 0)
            {
                return repoVerdicts;
            }
        }

        return Ledger.WorkflowVerdicts.Where(v => v.Task == taskId).ToList();
    }

    public string RepositoryPathFor(WorkflowTask task) =>
        RepositoryPathForRepo(task.Repo);

    public string RepositoryPathForRepo(string? repoName) =>
        !string.IsNullOrWhiteSpace(repoName)
        && RepoPaths is not null
        && RepoPaths.TryGetValue(repoName, out var path)
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
