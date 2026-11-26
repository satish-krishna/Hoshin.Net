using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Manages the cached detached Red-check worktree (<c>.git/hoshin/worktrees/red-check</c>) via <see cref="IGitWorktrees"/> and overlays task test files for Red verification.</summary>
public static class RedWorktree
{
    public static RedClassification ObserveRed(
        WorkflowRepo repo,
        IGitWorktrees gitWorktrees,
        IAllowListedCommandRunner testRunner,
        string startHead,
        string[] testCmd,
        IReadOnlyList<WorkingTreeChange> testChanges,
        IReadOnlyList<string> prodPaths,
        IReadOnlyDictionary<string, string>? taskEnv,
        TimeSpan taskTimeout,
        string[]? compileCmd = null,
        IReadOnlyList<string>? extraLinkDirs = null)
    {
        var wtDir = repo.RedWorktreePath;
        WorktreeDependencyLinks.RemoveLeftoverLinks(wtDir, extraLinkDirs);
        var prepared = gitWorktrees.EnsureDetachedWorktree(repo.Root, wtDir, startHead);
        if (!prepared.Ok)
        {
            var err = TaskCommitHelpers.Squash(string.IsNullOrWhiteSpace(prepared.Stderr) ? prepared.Stdout : prepared.Stderr);
            return new RedClassification(
                RedKind.CouldNotRun,
                false,
                $"not observed: could not prepare detached worktree at {WorkflowEngine.Short(startHead)}: {err}");
        }

        using var links = WorktreeDependencyLinks.Link(repo.Root, wtDir, extraLinkDirs);
        try
        {
            OverlayTestFiles(repo.Root, wtDir, testChanges);
            if (compileCmd is { Length: > 0 })
            {
                var compileRun = testRunner.Run(wtDir, compileCmd, taskTimeout, taskEnv);
                if (!compileRun.Ok)
                {
                    return ClassifyCompileFailure(compileRun, prodPaths);
                }
            }

            var redRun = testRunner.Run(wtDir, testCmd, taskTimeout, taskEnv);
            return RedClassifier.Classify(redRun, prodPaths);
        }
        finally
        {
            links.Dispose();
            gitWorktrees.CleanDetachedWorktree(wtDir, startHead);
        }
    }

    private static RedClassification ClassifyCompileFailure(AllowListedCommandResult compileRun, IReadOnlyList<string> prodPaths)
    {
        var classified = RedClassifier.Classify(compileRun, prodPaths);
        if (classified.Kind == RedKind.MissingSymbol)
        {
            return classified;
        }

        if (!classified.IsValidRed)
        {
            return classified;
        }

        return new RedClassification(
            RedKind.SyntaxError,
            false,
            $"not observed: compile failed in Red worktree: {TaskCommitHelpers.Squash(compileRun.Tail(10))}");
    }

    public static void OverlayTestFiles(string repoRoot, string wtDir, IReadOnlyList<WorkingTreeChange> testChanges)
    {
        foreach (var change in testChanges)
        {
            var src = Path.Combine(repoRoot, change.Path.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(wtDir, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (change.Deleted || !File.Exists(src))
            {
                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
        }
    }
}
