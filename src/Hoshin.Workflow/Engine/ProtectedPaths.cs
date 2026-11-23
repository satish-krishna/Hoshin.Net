using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Engine;

/// <summary>Single owner for repository paths protected during build steps, shared by <see cref="WorkflowRunner"/> and the PreToolUse guard.</summary>
public static class ProtectedPaths
{
    public static readonly string[] ProtectedDirectories = [".hoshin", ".git", ".agents", ".claude-plugin"];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>Returns true if a repository-relative path is protected during <c>Implement</c>/<c>Retry</c>.</summary>
    public static bool IsProtectedDuringBuild(string relativePath, string repoRoot, string changeFolder)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        if (norm.Equals(MarkerConstants.FileName, PathComparison))
        {
            return true;
        }

        foreach (var dir in ProtectedDirectories)
        {
            if (norm.Equals(dir, PathComparison) || norm.StartsWith(dir + "/", PathComparison))
            {
                return true;
            }
        }

        var changeRel = Path.GetRelativePath(repoRoot, changeFolder).Replace('\\', '/').Trim('/');
        return changeRel.Length > 0
            && !changeRel.StartsWith("..", StringComparison.Ordinal)
            && (norm.Equals(changeRel, PathComparison) || norm.StartsWith(changeRel + "/", PathComparison));
    }

    /// <summary>Returns true if a resolved full path lies inside any protected workflow directory (<c>.hoshin/</c>, <c>.git/</c>, <c>.agents/</c>, <c>.claude-plugin/</c>).</summary>
    public static bool IsProtectedDirectoryPath(string fullPath)
    {
        var sep = Path.DirectorySeparatorChar;
        foreach (var dir in ProtectedDirectories)
        {
            if (fullPath.Contains($"{sep}{dir}{sep}", PathComparison)
                || fullPath.EndsWith($"{sep}{dir}", PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns true if a resolved full path is the active change folder or lies inside it.</summary>
    public static bool IsInsideChangeFolder(string fullPath, string changeFolder)
    {
        var sep = Path.DirectorySeparatorChar;
        var normalizedFolder = Path.GetFullPath(changeFolder).TrimEnd(sep, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, normalizedFolder, PathComparison)
            || fullPath.StartsWith(normalizedFolder + sep, PathComparison);
    }
}
