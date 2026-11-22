namespace Hoshin.Workflow.Engine;

/// <summary>
/// Authoritative library-side enforcement that git <c>HEAD</c> has not moved via commits outside the workflow (T04 / S3).
/// The PreToolUse shell regex guard is a speed bump; this check is the real enforcement.
/// </summary>
public static class WorkflowHeadGuard
{
    public const string UnexpectedHeadMoveReason = "git: HEAD moved outside the workflow";

    public static bool HasUnexpectedHeadMove(WorkflowLedger ledger, string taskId, string currentHead)
    {
        var issued = ledger.Issued.FirstOrDefault(x => x.Task == taskId);
        if (issued is null || string.IsNullOrEmpty(issued.StartHead))
        {
            return false;
        }

        if (string.Equals(currentHead, issued.StartHead, StringComparison.Ordinal))
        {
            return false;
        }

        return !ledger.Verdicts.Any(v => v.Passed && string.Equals(v.Head, currentHead, StringComparison.Ordinal));
    }
}
