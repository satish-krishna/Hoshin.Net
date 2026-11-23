namespace Hoshin.Workflow.Engine;

/// <summary>Manages the cached detached Red-check worktree (<c>.git/hoshin/worktrees/red-check</c>) and overlays task test files for Red verification.</summary>
public static class RedWorktree
{
    public static RedClassification ObserveRed(
        WorkflowRepo repo,
        IAllowListedCommandRunner runner,
        string startHead,
        string[] testCmd,
        IReadOnlyList<WorkingTreeChange> testChanges,
        IReadOnlyList<string> prodPaths,
        IReadOnlyDictionary<string, string>? taskEnv,
        TimeSpan taskTimeout)
    {
        var wtDir = repo.RedWorktreePath;
        var prepared = EnsureCachedWorktree(repo, runner, wtDir, startHead);
        if (!prepared.Ok)
        {
            return new RedClassification(
                RedKind.CouldNotRun,
                false,
                $"not observed: could not prepare detached worktree at {WorkflowEngine.Short(startHead)}: {TaskCommitHelpers.Squash(prepared.Tail(5))}");
        }

        OverlayTestFiles(repo.Root, wtDir, testChanges);
        var redRun = runner.Run(wtDir, testCmd, taskTimeout, taskEnv);
        WorkflowRepo.GitIn(runner, wtDir, "reset", "--hard", "--quiet", startHead);
        WorkflowRepo.GitIn(runner, wtDir, "clean", "-fd", "--quiet");
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

    public static AllowListedCommandResult EnsureCachedWorktree(
        WorkflowRepo repo,
        IAllowListedCommandRunner runner,
        string wtDir,
        string startHead)
    {
        var gitMarker = Path.Combine(wtDir, ".git");
        if (Directory.Exists(wtDir) && (File.Exists(gitMarker) || Directory.Exists(gitMarker)))
        {
            var checkout = WorkflowRepo.GitIn(runner, wtDir, "checkout", "--detach", "--force", startHead);
            if (checkout.Ok)
            {
                WorkflowRepo.GitIn(runner, wtDir, "clean", "-fd", "--quiet");
                return checkout;
            }

            repo.Git("worktree", "remove", "--force", wtDir);
            repo.Git("worktree", "prune");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(wtDir)!);
        if (Directory.Exists(wtDir))
        {
            Directory.Delete(wtDir, recursive: true);
        }

        return repo.Git("worktree", "add", "--detach", "--force", wtDir, startHead);
    }
}
