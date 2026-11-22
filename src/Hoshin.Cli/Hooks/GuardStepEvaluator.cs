using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Evaluates step-specific target-path rules during active build (<c>Implement</c>/<c>Retry</c>) and authoring
/// (<c>WriteDocument</c>/<c>Review</c>/<c>Fix</c>) steps, covering both file-edit tools and shell redirect/<c>tee</c> targets (T06 / S7).
/// </summary>
public static class GuardStepEvaluator
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static string? CheckBuildTargetPath(string filePath, WorkflowRepo repo, CurrentStep step)
    {
        var full = Path.GetFullPath(filePath, repo.Root);
        var sep = Path.DirectorySeparatorChar;
        if (full.Contains($"{sep}.hoshin{sep}", PathComparison)
            || full.Contains($"{sep}.git{sep}", PathComparison)
            || full.Contains($"{sep}.agents{sep}", PathComparison)
            || full.Contains($"{sep}.claude-plugin{sep}", PathComparison))
        {
            return "The change's documents in .hoshin/, .agents/, .claude-plugin/, and .git/ are not edited during a build.";
        }

        var changeFolder = Path.GetFullPath(step.ChangeFolder ?? repo.ChangeFolder).TrimEnd(sep, Path.AltDirectorySeparatorChar);
        if (string.Equals(full, changeFolder, PathComparison)
            || full.StartsWith(changeFolder + sep, PathComparison))
        {
            return "The change folder's documents (intent, spec, plan, change.jsonc) are not edited during a build.";
        }

        // Red-phase enforcement: during Implement/Retry, deny production file edits until at least one test file
        // has been created or modified in the working tree (unless the task has no test, in which case test files are denied).
        if (step is { Kind: "Implement" or "Retry", Task: not null })
        {
            var rel = Path.GetRelativePath(repo.Root, full).Replace('\\', '/');
            var isTest = WorkflowRepo.IsTestPath(rel, step.TestPatterns, step.TaskTestFiles);
            if (!step.HasTest)
            {
                if (isTest)
                {
                    return "A task with no test must not change test files.";
                }

                return null;
            }

            if (!isTest && !HasModifiedTestFileInWorkingTree(repo, step))
            {
                return $"Write the failing test for {step.Task} first; production files may only change after a test file has been written.";
            }
        }

        return null;
    }

    public static string? CheckWriteTargetPath(string filePath, WorkflowRepo repo, IReadOnlyList<string> allowedFiles, string label)
    {
        var target = Path.GetFullPath(filePath, repo.Root);
        foreach (var allowed in allowedFiles)
        {
            if (string.Equals(target, Path.GetFullPath(allowed), PathComparison))
            {
                return null;
            }
        }

        var rels = string.Join(", ", allowedFiles.Select(a => Path.GetRelativePath(repo.Root, Path.GetFullPath(a)).Replace('\\', '/')));
        return $"While the {label} is written only {rels} may change.";
    }

    private static bool HasModifiedTestFileInWorkingTree(WorkflowRepo repo, CurrentStep step)
    {
        var status = repo.Git("status", "--porcelain", "-uall");
        if (!status.Ok || string.IsNullOrWhiteSpace(status.Output))
        {
            return false;
        }

        foreach (var raw in status.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
            {
                continue;
            }

            var pathPart = raw[3..].Trim().Trim('"');
            var arrow = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                pathPart = pathPart[(arrow + 4)..].Trim();
            }

            if (WorkflowRepo.IsTestPath(pathPart, step.TestPatterns, step.TaskTestFiles))
            {
                return true;
            }
        }

        return false;
    }
}
