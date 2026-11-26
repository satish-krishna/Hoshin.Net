using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>Bridges workflow runner steps to System A's <see cref="EventRunner"/> lifecycle events in Hoshin workspaces.</summary>
public interface IWorkflowEventBridge
{
    bool IsWorkspaceRepo(WorkflowRepo repo);

    EventOutcome? TryFire(
        WorkflowRepo repo,
        string eventName,
        Dictionary<string, string>? extra = null,
        string? repoOverride = null);
}

/// <summary>No-op bridge for isolated unit tests that run without System A workspace event execution.</summary>
public sealed class NullWorkflowEventBridge : IWorkflowEventBridge
{
    public bool IsWorkspaceRepo(WorkflowRepo repo) => false;

    public EventOutcome? TryFire(
        WorkflowRepo repo,
        string eventName,
        Dictionary<string, string>? extra = null,
        string? repoOverride = null) => null;
}

/// <summary>Fires <c>build.start</c>, <c>task.done</c>, <c>build.finish</c>, <c>review.finish</c>, and <c>change.done</c> via <see cref="EventRunner"/>.</summary>
public sealed class WorkflowEventBridge(
    EventContextFactory contextFactory,
    EventRunner eventRunner,
    IWorkspaceLocator workspaceLocator,
    IFileSystem fileSystem) : IWorkflowEventBridge
{
    private readonly EventContextFactory _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly EventRunner _eventRunner = eventRunner ?? throw new ArgumentNullException(nameof(eventRunner));
    private readonly IWorkspaceLocator _workspaceLocator = workspaceLocator ?? throw new ArgumentNullException(nameof(workspaceLocator));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public bool IsWorkspaceRepo(WorkflowRepo repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (!_workspaceLocator.HasWorkspaceMarker(repo.Root) || !IsIntentLayout(repo))
        {
            return false;
        }

        var intentPath = Path.Combine(repo.ChangeFolder, "intent.md");
        if (_fs.FileExists(intentPath))
        {
            return true;
        }

        var relative = Path.GetRelativePath(repo.Root, intentPath).Replace('\\', '/');
        return repo.GitQuery.ExistsAt(repo.Root, "HEAD", relative);
    }

    public EventOutcome? TryFire(
        WorkflowRepo repo,
        string eventName,
        Dictionary<string, string>? extra = null,
        string? repoOverride = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        if (!IsWorkspaceRepo(repo))
        {
            return null;
        }

        var args = new EventContextArgs(
            eventName,
            repo.ChangeKey,
            repo.SystemKey,
            repoOverride ?? repo.RepoKey,
            new ResolveOptions(null, null),
            extra ?? new Dictionary<string, string>(StringComparer.Ordinal));
        var ctx = _contextFactory.Build(repo.Root, args);
        return _eventRunner.RunAsync(ctx).GetAwaiter().GetResult();
    }

    public static string FormatOutcome(EventOutcome outcome)
    {
        var parts = new List<string>();
        foreach (var problem in outcome.Problems)
        {
            if (!string.IsNullOrWhiteSpace(problem))
            {
                parts.Add(problem);
            }
        }

        foreach (var hook in outcome.Hooks)
        {
            var status = $"{hook.Id} ({hook.When}): {hook.Outcome}";
            parts.Add(string.IsNullOrWhiteSpace(hook.Output) ? status : $"{status}\n{hook.Output}");
        }

        if (outcome.Failed is { Output.Length: > 0 } failed && !parts.Any(p => p.Contains(failed.Output, StringComparison.Ordinal)))
        {
            parts.Add(failed.Output);
        }

        return string.Join("\n", parts);
    }

    private static bool IsIntentLayout(WorkflowRepo repo) =>
        ChangeLedgerPaths.TryExtractIntentSystemAndChange(repo.ChangeFolder, out _, out _);
}
