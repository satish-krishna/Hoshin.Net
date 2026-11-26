using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Manages per-turn auxiliary state files under <c>.git/hoshin/</c> (<c>last-start.txt</c>,
/// <c>pending-context.txt</c>, <c>pending-report.txt</c>) under the run lock.
/// </summary>
internal static class TurnState
{
    public static string LastStartPath(WorkflowRepo repo) => Path.Combine(repo.HoshinDir, "last-start.txt");

    public static string PendingReportPath(WorkflowRepo repo) => Path.Combine(repo.HoshinDir, "pending-report.txt");

    public static string ProgressDisagreementPath(WorkflowRepo repo) => Path.Combine(repo.HoshinDir, "progress-disagreement.txt");

    public static string FormatNewProgressDisagreement(WorkflowRepo repo, WorkflowState state, IFileSystem fs)
    {
        var notes = WorkflowProgress.DescribeDisagreements(state);
        if (notes.Count == 0)
        {
            return "";
        }

        var signature = string.Join("\n", notes);
        var path = ProgressDisagreementPath(repo);
        if (fs.FileExists(path) && string.Equals(fs.ReadAllText(path), signature, StringComparison.Ordinal))
        {
            return "";
        }

        fs.CreateDirectory(repo.HoshinDir);
        fs.WriteAllTextAtomic(path, signature);
        return "Note: local ledger and committed build logs disagree:\n- " + string.Join("\n- ", notes) + "\n\n";
    }

    public static bool IsDuplicateAntigravityStart(WorkflowRepo repo, string turnKey, IFileSystem fs)
    {
        var path = LastStartPath(repo);
        if (fs.FileExists(path) && fs.ReadAllText(path) == turnKey)
        {
            return true;
        }

        fs.CreateDirectory(repo.HoshinDir);
        fs.WriteAllTextAtomic(path, turnKey);
        return false;
    }

    public static void QueuePendingContext(WorkflowRepo repo, string text, IFileSystem fs)
    {
        fs.CreateDirectory(repo.HoshinDir);
        fs.WriteAllTextAtomic(repo.PendingContextPath, text);
    }

    public static string? TryDrainPendingContext(WorkflowRepo repo, IFileSystem fs)
    {
        if (!fs.FileExists(repo.PendingContextPath))
        {
            return null;
        }

        var pending = fs.ReadAllText(repo.PendingContextPath);
        TryDelete(repo.PendingContextPath, fs);
        return string.IsNullOrWhiteSpace(pending) ? null : pending;
    }

    public static void QueuePendingReport(WorkflowRepo repo, string report, IFileSystem fs)
    {
        using var gate = repo.AcquireRunLock();
        fs.CreateDirectory(repo.HoshinDir);
        File.AppendAllText(PendingReportPath(repo), report);
    }

    public static string DrainPendingReport(WorkflowRepo repo, IFileSystem fs)
    {
        var path = PendingReportPath(repo);
        if (!fs.FileExists(path))
        {
            return "";
        }

        using var gate = repo.AcquireRunLock();
        if (!fs.FileExists(path))
        {
            return "";
        }

        var text = fs.ReadAllText(path);
        TryDelete(path, fs);
        return text;
    }

    public static void ClearTurnFiles(WorkflowRepo repo, IFileSystem fs)
    {
        TryDelete(LastStartPath(repo), fs);
        TryDelete(repo.PendingContextPath, fs);
    }

    public static void TryDelete(string path, IFileSystem fs)
    {
        try
        {
            if (fs.FileExists(path))
            {
                fs.DeleteFile(path);
            }
        }
        catch (IOException)
        {
            // Ignore transient lock on cleanup.
        }
    }
}
