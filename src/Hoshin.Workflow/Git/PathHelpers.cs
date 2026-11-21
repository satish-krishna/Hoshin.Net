namespace Hoshin.Workflow.Git;

/// <summary>Cross-platform canonical path helpers matching Node's <c>realpathSync.native</c> behavior on Windows and Unix.</summary>
public static class PathHelpers
{
    public static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            if (Directory.Exists(full))
            {
                var info = new DirectoryInfo(full);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                var resolved = target is not null ? target.FullName : full;
                return CanonicalTrim(resolved);
            }

            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                var resolved = target is not null ? target.FullName : full;
                return CanonicalTrim(resolved);
            }
        }
        catch
        {
            // Fall through to normalized full path.
        }

        return CanonicalTrim(full);
    }

    public static string RealishPath(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return RealPath(path);
        }

        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || parent == path)
        {
            return Path.GetFullPath(path);
        }

        return Path.Combine(RealishPath(parent), Path.GetFileName(path));
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(RealPath(a), RealPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static bool IsSubPathOf(string child, string parent)
    {
        var normChild = RealPath(child);
        var normParent = RealPath(parent);
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (normParent.EndsWith(Path.DirectorySeparatorChar))
        {
            return normChild.StartsWith(normParent, cmp);
        }

        return normChild.StartsWith(normParent + Path.DirectorySeparatorChar, cmp);
    }

    private static string CanonicalTrim(string full)
    {
        var root = Path.GetPathRoot(full);
        var trimmed = full.Length > (root?.Length ?? 0)
            ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : full;
        if (OperatingSystem.IsWindows() && trimmed.Length >= 2 && trimmed[1] == ':')
        {
            trimmed = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        }

        return trimmed;
    }
}
