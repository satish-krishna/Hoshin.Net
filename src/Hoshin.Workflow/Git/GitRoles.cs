namespace Hoshin.Workflow.Git;

/// <summary>Raw local <c>git</c> command execution.</summary>
public interface IGitRunner
{
    GitResult Run(string cwd, IReadOnlyList<string> args, string? input = null);
    byte[] RunBytes(string cwd, IReadOnlyList<string> args, string? input = null);
    string? Out(string cwd, IReadOnlyList<string> args);
    string Must(string cwd, IReadOnlyList<string> args, string what);
}

/// <summary>Read-only queries over Git objects, trees, and commit history.</summary>
public interface IGitQuery
{
    string? CommitOf(string dir, string rev);
    string? ShowFile(string dir, string rev, string path);
    List<GitFileEntry> ListFiles(string dir, string rev, string path);
    bool ExistsAt(string dir, string rev, string path);
    int? CountCommits(string dir, string from, string to, IReadOnlyList<string> paths);
    List<TrailerCommit> CommitsWithTrailer(string dir, string from, string to, string key, IReadOnlyList<string>? paths = null);
    string? LastCommitTouching(string dir, string range, IReadOnlyList<string> pathspec);
    string? CodeHead(string dir, string excludeRelativePath);
    string? LastCommitForPath(string dir, string relativePath);
    string? RemoteUrl(string dir, string remote);
}

/// <summary>Working-tree status and diff queries.</summary>
public interface IGitStatus
{
    List<string> DirtyPaths(string dir, IReadOnlyList<string>? exclude = null);
    List<string> ChangedPaths(string dir, IReadOnlyList<string> paths);
    List<string> TrackedChanges(string dir);
    List<string> ChangedFilesBetween(string dir, string from, string to, IReadOnlyList<string> pathspec);
    HashSet<string> ReportedPaths(string top, IReadOnlyList<string> paths);
    bool DiffersFromHead(string top, string path);
    bool Differs(string top, IReadOnlyList<string> revs, string path);
    string PorcelainStatusZ(string dir);
}

/// <summary>Branch, base-ref, ancestry, and commit-range queries.</summary>
public interface IGitBranches
{
    string? CurrentBranch(string dir);
    bool HasLocalBranch(string dir, string name);
    bool HasRemoteBranch(string dir, string name);
    string? BaseBranch(string dir);
    string? BaseRevision(string dir, string baseBranch);
    List<string> BranchNames(string dir);
    bool IsAncestor(string dir, string ancestor, string descendant);
    string? MergeBase(string dir, string a, string b);
    List<GitCommitInfo> CommitsBeyondBase(string dir, string branch, string baseBranch);
    string? UpstreamRemote(string dir);
}

/// <summary>Repository root, git-dir, and linked worktree discovery and management.</summary>
public interface IGitWorktrees
{
    string? TopLevel(string dir);
    string GitPath(string dir, string path, bool common = false);
    GitDirsInfo? GitDirs(string dir);
    string MainCheckout(string dir);
    List<GitWorktree> Worktrees(string dir);
    string? CheckedOutIn(string dir, string branch);
    GitResult EnsureDetachedWorktree(string repoRoot, string worktreeDir, string rev);
    GitResult CleanDetachedWorktree(string worktreeDir, string rev);
}

/// <summary>Staging, committing, resetting, and pushing in a local Git repository.</summary>
public interface IGitCommitter
{
    GitCommitOutcome CommitPaths(string dir, IReadOnlyList<string> paths, IReadOnlyList<string> paragraphs);
    GitResult ResetMixed(string dir, string rev);
    GitResult Push(string dir, string remote, string refSpec);
}
