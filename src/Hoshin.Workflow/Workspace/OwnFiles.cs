using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Workspace;

/// <summary>Hoshin's own files in a master repository (<c>intent/</c> and <c>.hoshin.json</c>).</summary>
public static class OwnFiles
{
    public static readonly string[] Paths = ["intent", MarkerConstants.FileName];

    public static List<string> ExcludingOwnFiles() => Paths.Select(p => $":(exclude){p}").ToList();

    public static bool HoldsMarker(string repoPath) => File.Exists(Path.Combine(repoPath, MarkerConstants.FileName));

    public static List<string> UncommittedFiles(IGitClient git, string repoPath) =>
        git.DirtyPaths(repoPath, HoldsMarker(repoPath) ? Paths : null);
}
