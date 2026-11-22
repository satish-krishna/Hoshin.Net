using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Single owner for active-run session matching shared by <see cref="HookOrchestrator"/> and
/// <see cref="Commands.GuardCommand"/>. A missing session ID is never treated as in the run.
/// </summary>
internal static class HookSessionScope
{
    public static readonly TimeSpan StaleStepWindow = TimeSpan.FromHours(24);

    public static bool IsActiveNonStale(RunMarker? marker, CurrentStep? step, DateTimeOffset now)
    {
        if (marker is null)
        {
            return false;
        }

        var anchor = step?.At ?? marker.Started;
        return now - anchor <= StaleStepWindow;
    }

    public static bool IsParentRunSession(RunMarker marker, NormalizedHookEvent ev) =>
        !string.IsNullOrEmpty(marker.SessionId) &&
        !string.IsNullOrEmpty(ev.SessionId) &&
        string.Equals(marker.SessionId, ev.SessionId, StringComparison.Ordinal);

    public static bool IsRegisteredSubagent(WorkflowRepo repo, CurrentStep? step, NormalizedHookEvent ev)
    {
        if (step is not { Kind: "Implement" or "Retry" })
        {
            return false;
        }

        var registeredAgentId = step.AgentId ?? repo.ReadLedger().ActiveSubagent?.AgentId;
        if (string.IsNullOrEmpty(registeredAgentId))
        {
            return false;
        }

        return string.Equals(registeredAgentId, ev.SubagentCallerId, StringComparison.Ordinal)
            || string.Equals(registeredAgentId, ev.StoppedAgentId, StringComparison.Ordinal)
            || (ev.IsAntigravity && string.Equals(registeredAgentId, ev.SessionId, StringComparison.Ordinal));
    }

    public static bool IsRunSession(
        WorkflowRepo repo,
        NormalizedHookEvent ev,
        DateTimeOffset now,
        out RunMarker? marker,
        out CurrentStep? step)
    {
        marker = repo.ReadRunMarker();
        step = repo.ReadStep();
        if (!IsActiveNonStale(marker, step, now))
        {
            return false;
        }

        return IsParentRunSession(marker!, ev) || IsRegisteredSubagent(repo, step, ev);
    }
}
