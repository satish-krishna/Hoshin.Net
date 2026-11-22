using System.Globalization;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Manages per-scope consecutive stop-block counters under the run lock, resetting on new user turns
/// (<c>ContinuationActive == false</c>), and detects when an assistant reply ends with a question.
/// </summary>
internal static class StopCounterStore
{
    public static int Next(WorkflowRepo repo, string scope, bool continuationActive)
    {
        using var gate = repo.AcquireRunLock();
        Directory.CreateDirectory(repo.HoshinDir);
        var path = CounterPath(repo, scope);
        var next = 1;
        if (continuationActive &&
            File.Exists(path) &&
            int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var current))
        {
            next = current + 1;
        }

        File.WriteAllText(path, next.ToString(CultureInfo.InvariantCulture));
        return next;
    }

    public static void Reset(WorkflowRepo repo, string scope)
    {
        using var gate = repo.AcquireRunLock();
        var path = CounterPath(repo, scope);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void ResetAll(WorkflowRepo repo)
    {
        if (!Directory.Exists(repo.HoshinDir))
        {
            return;
        }

        using var gate = repo.AcquireRunLock();
        foreach (var file in Directory.GetFiles(repo.HoshinDir, "stop-blocks*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Ignore transient cleanup lock.
            }
        }
    }

    public static bool EndsWithQuestion(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var trimmed = message.AsSpan().TrimEnd();
        while (trimmed.Length > 0 && trimmed[^1] is '"' or '\'' or ')' or ']' or '*' or '_' or '`')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.Length > 0 && trimmed[^1] == '?';
    }

    private static string CounterPath(WorkflowRepo repo, string scope) =>
        Path.Combine(repo.HoshinDir, $"stop-blocks-{scope}");
}
