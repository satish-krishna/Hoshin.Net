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
        TimeSpan taskTimeout)
    {
        var wtDir = repo.RedWorktreePath;
        var prepared = gitWorktrees.EnsureDetachedWorktree(repo.Root, wtDir, startHead);
        if (!prepared.Ok)
        {
            var err = TaskCommitHelpers.Squash(string.IsNullOrWhiteSpace(prepared.Stderr) ? prepared.Stdout : prepared.Stderr);
            return new RedClassification(
                RedKind.CouldNotRun,
                false,
                $"not observed: could not prepare detached worktree at {WorkflowEngine.Short(startHead)}: {err}");
        }

        OverlayTestFiles(repo.Root, wtDir, testChanges);
        var redRun = testRunner.Run(wtDir, testCmd, taskTimeout, taskEnv);
        gitWorktrees.CleanDetachedWorktree(wtDir, startHead);
        return RedClassifier.Classify(redRun, prodPaths);
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
