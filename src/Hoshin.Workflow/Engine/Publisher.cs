using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Pushes approved changes via <see cref="IGitCommitter"/> and opens or reuses a pull request via <see cref="IPullRequestClient"/>.</summary>
public sealed class Publisher(
    IGitBranches gitBranches,
    IGitQuery gitQuery,
    IGitCommitter gitCommitter,
    IPullRequestClient pullRequests,
    TimeProvider timeProvider) : IPublisher
{
    private readonly IGitBranches _gitBranches = gitBranches ?? throw new ArgumentNullException(nameof(gitBranches));
    private readonly IGitQuery _gitQuery = gitQuery ?? throw new ArgumentNullException(nameof(gitQuery));
    private readonly IGitCommitter _gitCommitter = gitCommitter ?? throw new ArgumentNullException(nameof(gitCommitter));
    private readonly IPullRequestClient _pullRequests = pullRequests ?? throw new ArgumentNullException(nameof(pullRequests));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public WorkflowPublishResult Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = true)
    {
        var remote = _gitBranches.UpstreamRemote(repo.Root) ?? "origin";
        var push = _gitCommitter.Push(repo.Root, remote, $"HEAD:{targetRef}");
        if (!push.Ok)
        {
            var rawErr = string.IsNullOrWhiteSpace(push.Stderr) ? push.Stdout : push.Stderr;
            var error = TaskCommitHelpers.Squash(rawErr);
            return new WorkflowPublishResult(null, string.IsNullOrWhiteSpace(error) ? $"git push to {remote} failed (exit {push.Status})" : error);
        }

        string? prUrl = null;
        if (openPullRequest && ShouldOpenPullRequest(repo, remote))
        {
            var (url, prError) = OpenOrReusePullRequest(repo, state, targetRef);
            if (prError is not null)
            {
                return new WorkflowPublishResult(null, prError);
            }

            prUrl = url;
        }

        var published = new WorkflowPublished(state.EffectiveCodeHead, targetRef, _time.GetUtcNow(), prUrl);
        repo.UpdateLedger(l => l with { Published = new List<WorkflowPublished>(l.WorkflowPublishedRecords) { published } });
        return new WorkflowPublishResult(published, null);
    }

    private bool ShouldOpenPullRequest(WorkflowRepo repo, string remote)
    {
        var url = _gitQuery.RemoteUrl(repo.Root, remote) ?? "";
        return _pullRequests.HostOf(url) is not null || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HOSHIN_GH"));
    }

    private (string? Url, string? Error) OpenOrReusePullRequest(WorkflowRepo repo, WorkflowState state, string targetRef)
    {
        var branch = targetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? targetRef["refs/heads/".Length..]
            : targetRef;
        try
        {
            var existing = _pullRequests.OpenPullRequest(repo.Root, branch);
            if (existing is not null)
            {
                return (existing.Url, null);
            }

            var created = _pullRequests.OpenNew(repo.Root, new OpenPullRequestInput(state.Plan.Base, branch, state.Plan.Change, $"Automated publish for {state.Plan.Change}."));
            if (created.PullRequest is not null)
            {
                return (created.PullRequest.Url, null);
            }

            return (null, $"opening pull request failed: {created.Error ?? "unknown error"}");
        }
        catch (CouldNotRunException e)
        {
            return (null, $"opening pull request failed: {e.Message}");
        }
    }
}
