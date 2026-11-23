using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>Resolves which open change a <c>hook start</c> invocation targets, or starts a new one via <see cref="IChangeStarter"/>.</summary>
internal sealed class StartChangeResolver(
    IChangeStarter changeStarter,
    WorkflowAdvancer advancer,
    IFileSystem fileSystem,
    TimeProvider timeProvider)
{
    private readonly IChangeStarter _changeStarter = changeStarter ?? throw new ArgumentNullException(nameof(changeStarter));
    private readonly WorkflowAdvancer _advancer = advancer ?? throw new ArgumentNullException(nameof(advancer));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public bool TryResolveOrStart(
        NormalizedHookEvent ev,
        ref WorkflowRepo repo,
        SkillInvocation invocation,
        bool isFixAccept,
        Func<NormalizedHookEvent, WorkflowRepo, string, string, (string? Output, string Decision)> emit,
        out (string? Output, string Decision) earlyResponse)
    {
        earlyResponse = default;
        var invoked = invocation.Skill;
        var rawArgs = (ev.CommandArgs ?? invocation.Args ?? "").Trim();
        var matchArgs = StripLeadingFlags(rawArgs);

        var openChanges = _changeStarter.ListOpenChanges(repo);
        if (TryMatchOpenChange(openChanges, matchArgs) is { } matched)
        {
            repo = _advancer.ResolveRepoAt(matched.MasterRoot, matched.ChangeFolder) ?? repo;
            return false;
        }

        if (HasPinnedActiveRun(repo))
        {
            return false;
        }

        if (!isFixAccept && ShouldStartNewChange(repo, invoked, matchArgs, openChanges.Count))
        {
            return TryStartNewChange(ev, ref repo, invoked, matchArgs, emit, out earlyResponse);
        }

        if (openChanges.Count > 1)
        {
            var names = string.Join(", ", openChanges.Select(c => c.Change));
            earlyResponse = emit(
                ev,
                repo,
                $"Multiple open changes exist in this workspace: {names}. Ask the engineer which change they want to work on, then end your turn.",
                $"{invoked}: ambiguous ({openChanges.Count} open changes)");
            return true;
        }

        if (openChanges.Count == 1)
        {
            repo = _advancer.ResolveRepoAt(openChanges[0].MasterRoot, openChanges[0].ChangeFolder) ?? repo;
        }

        return false;
    }

    public static string StripLeadingFlags(string rawArgs)
    {
        var tokens = rawArgs.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var rest = tokens.SkipWhile(t => t.StartsWith('-')).ToArray();
        return string.Join(' ', rest);
    }

    private bool HasPinnedActiveRun(WorkflowRepo repo) =>
        _fs.FileExists(repo.RunMarkerPath)
        && HookSessionScope.IsActiveNonStale(repo.ReadRunMarker(), repo.ReadStep(), _time.GetUtcNow())
        && !string.IsNullOrWhiteSpace(repo.ReadRunMarker()?.ChangeFolder);

    private bool ShouldStartNewChange(WorkflowRepo repo, string invoked, string matchArgs, int openCount)
    {
        if (invoked is not ("intent" or "fix") || !_changeStarter.HasWorkspaceMarker(repo))
        {
            return false;
        }

        return openCount == 0 || matchArgs.Length > 0;
    }

    private bool TryStartNewChange(
        NormalizedHookEvent ev,
        ref WorkflowRepo repo,
        string invoked,
        string rawArgs,
        Func<NormalizedHookEvent, WorkflowRepo, string, string, (string? Output, string Decision)> emit,
        out (string? Output, string Decision) earlyResponse)
    {
        earlyResponse = default;
        var started = _changeStarter.StartFromHook(repo.Root, invoked, rawArgs);
        if (!started.Ok)
        {
            earlyResponse = emit(
                ev,
                repo,
                $"Could not start a new change: {started.Error}. Tell the engineer, then end your turn.",
                $"{invoked}: change.start failed");
            return true;
        }

        repo = _advancer.ResolveRepoAt(started.CopyRoot!, started.ChangeFolder) ?? repo;
        return false;
    }

    private static OpenChangeOption? TryMatchOpenChange(IReadOnlyList<OpenChangeOption> openChanges, string rawArgs)
    {
        if (string.IsNullOrWhiteSpace(rawArgs) || openChanges.Count == 0)
        {
            return null;
        }

        var token = rawArgs.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        var exact = openChanges.Where(c => string.Equals(c.Change, token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var bySlug = openChanges.Where(c => c.Change.EndsWith("-" + token, StringComparison.OrdinalIgnoreCase)).ToList();
        return bySlug.Count == 1 ? bySlug[0] : null;
    }
}
