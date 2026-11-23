using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Pushes approved changes to the upstream/origin remote and opens or reuses a pull request via <see cref="IPullRequestClient"/>.</summary>
public sealed class Publisher(
    IPullRequestClient pullRequests,
    TimeProvider timeProvider) : IPublisher
{
    private readonly IPullRequestClient _pullRequests = pullRequests ?? throw new ArgumentNullException(nameof(pullRequests));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public WorkflowPublishResult Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = true)
    {
        var remote = ResolvePushRemote(repo);
        var pushEnv = AllowListedCommandRunner.CollectPushEnvironment();
        var push = repo.GitWithEnv(pushEnv, "push", remote, $"HEAD:{targetRef}");
        if (!push.Ok)
        {
            var error = TaskCommitHelpers.Squash(push.Tail(15));
            return new WorkflowPublishResult(null, string.IsNullOrWhiteSpace(error) ? $"git push to {remote} failed (exit {push.ExitCode})" : error);
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
        repo.WithLedger(l =>
        {
            l.Published.Add(published);
            return 0;
        });
        return new WorkflowPublishResult(published, null);
    }

    private static string ResolvePushRemote(WorkflowRepo repo)
    {
        var upstream = repo.Git("rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}");
        if (upstream.Ok)
        {
            var trimmed = upstream.Output.Trim();
            var slash = trimmed.IndexOf('/', StringComparison.Ordinal);
            if (slash > 0)
            {
                return trimmed[..slash];
            }
        }

        return "origin";
    }

    private bool ShouldOpenPullRequest(WorkflowRepo repo, string remote)
    {
        var urlRes = repo.Git("remote", "get-url", remote);
        var url = urlRes.Ok ? urlRes.Output.Trim() : "";
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
