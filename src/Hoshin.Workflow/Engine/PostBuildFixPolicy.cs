using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Evaluates and commits post-build fix steps (<c>FIX-CHECK-*</c> and <c>FIX-R*</c>) in the target repository,
/// splitting any test file edits into a <c>test(&lt;scope&gt;)</c> commit before the <c>fix(&lt;scope&gt;)</c> commit
/// with valid <c>Generated-By: hoshin</c> trailers.
/// </summary>
internal static class PostBuildFixPolicy
{
    public const string SkippedRedSummary = "post-build fix verified by build.finish checks";

    public static bool IsFixTaskId(string taskId) =>
        !string.IsNullOrWhiteSpace(taskId) && taskId.StartsWith("FIX-", StringComparison.Ordinal);

    public static WorkflowVerdict EvaluateAndCommit(
        WorkflowRepo targetRepo,
        WorkflowState state,
        string taskId,
        int attempt,
        string startHead,
        IReadOnlyList<WorkingTreeChange> changed,
        IGitCommitter gitCommitter,
        DateTimeOffset now)
    {
        var plan = state.Plan;
        var testChanges = changed.Where(c => WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns)).ToList();
        var prodChanges = changed.Where(c => !WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns)).ToList();
        var subject = DescribeFix(taskId);
        const string trailers = "Generated-By: hoshin";

        string? testCommitSha = null;
        var headSha = startHead;

        if (testChanges.Count > 0)
        {
            var testPaths = testChanges.Select(c => c.Path).ToArray();
            var testCommit = gitCommitter.CommitPaths(targetRepo.Root, testPaths, [$"test({plan.Scope}): {subject}", trailers]);
            if (!testCommit.Ok || string.IsNullOrEmpty(testCommit.Sha))
            {
                gitCommitter.ResetMixed(targetRepo.Root, startHead);
                return new WorkflowVerdict(
                    taskId,
                    attempt,
                    startHead,
                    false,
                    SkippedRedSummary,
                    [$"git: test commit failed: {TaskCommitHelpers.Squash(testCommit.Error ?? testCommit.Output)}"],
                    now);
            }

            testCommitSha = testCommit.Sha;
            headSha = testCommit.Sha;
        }

        if (prodChanges.Count > 0)
        {
            var prodPaths = prodChanges.Select(c => c.Path).ToArray();
            var prodCommit = gitCommitter.CommitPaths(targetRepo.Root, prodPaths, [$"fix({plan.Scope}): {subject}", trailers]);
            if (!prodCommit.Ok || string.IsNullOrEmpty(prodCommit.Sha))
            {
                gitCommitter.ResetMixed(targetRepo.Root, startHead);
                return new WorkflowVerdict(
                    taskId,
                    attempt,
                    startHead,
                    false,
                    SkippedRedSummary,
                    [$"git: fix commit failed: {TaskCommitHelpers.Squash(prodCommit.Error ?? prodCommit.Output)}"],
                    now);
            }

            headSha = prodCommit.Sha;
        }

        return new WorkflowVerdict(taskId, attempt, headSha, true, SkippedRedSummary, [], now, testCommitSha);
    }

    private static string DescribeFix(string taskId) =>
        taskId.StartsWith("FIX-CHECK-", StringComparison.Ordinal)
            ? $"address build check failure ({taskId.ToLowerInvariant()})"
            : taskId.StartsWith("FIX-DECLINE-", StringComparison.Ordinal)
                ? $"address declined approval ({taskId.ToLowerInvariant()})"
                : $"address review findings ({taskId.ToLowerInvariant()})";
}
