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
        if (ProtectedPaths.IsProtectedDirectoryPath(full))
        {
            return "The change's documents in .hoshin/, .agents/, .claude-plugin/, and .git/ are not edited during a build.";
        }

        if (ProtectedPaths.IsInsideChangeFolder(full, step.ChangeFolder ?? repo.ChangeFolder))
        {
            return "The change folder's documents (intent, spec, plan, change.jsonc) are not edited during a build.";
        }

        // Red-phase enforcement: during Implement/Retry, deny production file edits until at least one test file
        // has been created or modified in the working tree (post-build fix steps may touch tests and/or code).
        if (step is { Kind: StepKind.Implement or StepKind.Retry, Task: not null, IsFix: false })
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

    private static bool HasModifiedTestFileInWorkingTree(WorkflowRepo repo, CurrentStep step) =>
        WorkingTreeSnapshot.GetChanges(repo)
            .Any(c => WorkflowRepo.IsTestPath(c.Path, step.TestPatterns, step.TaskTestFiles));
}
