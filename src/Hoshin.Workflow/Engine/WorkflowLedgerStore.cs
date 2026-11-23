using System.Text.Json;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

/// <summary>Atomic, file-locked storage for <see cref="WorkflowLedger"/>, <see cref="CurrentStep"/>, and <see cref="RunMarker"/>.</summary>
public static class WorkflowLedgerStore
{
    public static WorkflowLedger ReadLedger(WorkflowRepo repo, IFileSystem fs)
    {
        ChangeLedgerPaths.TryMigrateLegacyLedger(repo.HoshinDir, repo.LedgerPath, ReadRunMarker(repo, fs));
        return fs.FileExists(repo.LedgerPath)
            ? JsonSerializer.Deserialize(fs.ReadAllText(repo.LedgerPath), WorkflowJsonContext.Default.WorkflowLedger) ?? new()
            : new();
    }

    public static void WriteLedger(WorkflowRepo repo, IFileSystem fs, WorkflowLedger ledger) =>
        fs.WriteAllTextAtomic(repo.LedgerPath, JsonSerializer.Serialize(ledger, WorkflowJsonContext.Default.WorkflowLedger));

    public static CurrentStep? ReadStep(WorkflowRepo repo, IFileSystem fs) =>
        fs.FileExists(repo.StepPath)
            ? JsonSerializer.Deserialize(fs.ReadAllText(repo.StepPath), WorkflowJsonContext.Default.CurrentStep)
            : null;

    public static void WriteStep(WorkflowRepo repo, IFileSystem fs, CurrentStep step) =>
        fs.WriteAllTextAtomic(repo.StepPath, JsonSerializer.Serialize(step, WorkflowJsonContext.Default.CurrentStep));

    public static RunMarker? ReadRunMarker(WorkflowRepo repo, IFileSystem fs) =>
        fs.FileExists(repo.RunMarkerPath)
            ? JsonSerializer.Deserialize(fs.ReadAllText(repo.RunMarkerPath), WorkflowJsonContext.Default.RunMarker)
            : null;

    public static void WriteRunMarker(WorkflowRepo repo, IFileSystem fs, RunMarker marker) =>
        fs.WriteAllTextAtomic(repo.RunMarkerPath, JsonSerializer.Serialize(marker, WorkflowJsonContext.Default.RunMarker));

    public static T WithLedger<T>(WorkflowRepo repo, IFileSystem fs, Func<WorkflowLedger, T> update)
    {
        fs.CreateDirectory(repo.HoshinDir);
        using var gate = FileLock.Acquire(Path.Combine(repo.HoshinDir, "workflow-ledger.lock"), TimeSpan.FromSeconds(5));
        var ledger = ReadLedger(repo, fs);
        var result = update(ledger);
        WriteLedger(repo, fs, ledger);
        return result;
    }

    public static FileStream AcquireRunLock(WorkflowRepo repo, TimeSpan? timeout = null) =>
        FileLock.Acquire(repo.RunLockPath, timeout ?? TimeSpan.FromMinutes(10));
}
