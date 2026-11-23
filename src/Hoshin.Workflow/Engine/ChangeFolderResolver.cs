using System.Text.Json;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

/// <summary>Resolves the active change folder for a repository (explicit path, active <c>run.json</c>, <c>.hoshin/</c>, or <c>intent/&lt;system&gt;/&lt;change&gt;/</c>).</summary>
public static class ChangeFolderResolver
{
    private static readonly string[] ChangeMarkerFiles = ["change.jsonc", "plan.md", "intent.md", "spec.md"];

    public static string Resolve(string root, string commonDir, string? explicitChangeFolder, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        if (!string.IsNullOrWhiteSpace(explicitChangeFolder))
        {
            return Path.IsPathRooted(explicitChangeFolder)
                ? explicitChangeFolder
                : Path.Combine(root, explicitChangeFolder);
        }

        var pinned = TryReadPinnedFolder(commonDir, fs);
        if (pinned is not null)
        {
            return pinned;
        }

        var dotHoshin = Path.Combine(root, ".hoshin");
        if (HasAnyDocument(dotHoshin, fs, includeSpec: false))
        {
            return dotHoshin;
        }

        return FindFirstIntentChange(root, fs) ?? dotHoshin;
    }

    private static string? TryReadPinnedFolder(string commonDir, IFileSystem fs)
    {
        var runPath = Path.Combine(commonDir, "hoshin", "run.json");
        if (!fs.FileExists(runPath))
        {
            return null;
        }

        try
        {
            var marker = JsonSerializer.Deserialize(fs.ReadAllText(runPath), WorkflowJsonContext.Default.RunMarker);
            if (!string.IsNullOrWhiteSpace(marker?.ChangeFolder) && fs.DirectoryExists(marker.ChangeFolder))
            {
                return marker.ChangeFolder;
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // Ignore corrupt or unreadable run marker.
        }

        return null;
    }

    private static string? FindFirstIntentChange(string root, IFileSystem fs)
    {
        var intentRoot = Path.Combine(root, "intent");
        if (!fs.DirectoryExists(intentRoot))
        {
            return null;
        }

        foreach (var sysDir in fs.EnumerateDirectories(intentRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            if (Path.GetFileName(sysDir).Equals("hooks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = FindFirstChangeInSystem(sysDir, fs);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindFirstChangeInSystem(string sysDir, IFileSystem fs)
    {
        foreach (var chDir in fs.EnumerateDirectories(sysDir).OrderByDescending(d => d, StringComparer.Ordinal))
        {
            var chName = Path.GetFileName(chDir);
            if (chName is "context" or "hooks")
            {
                continue;
            }

            if (HasAnyDocument(chDir, fs, includeSpec: true))
            {
                return chDir;
            }
        }

        return null;
    }

    private static bool HasAnyDocument(string folder, IFileSystem fs, bool includeSpec)
    {
        var count = includeSpec ? ChangeMarkerFiles.Length : ChangeMarkerFiles.Length - 1;
        for (var i = 0; i < count; i++)
        {
            if (fs.FileExists(Path.Combine(folder, ChangeMarkerFiles[i])))
            {
                return true;
            }
        }

        return false;
    }
}
