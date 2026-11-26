using System.Text.Json;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Engine;

/// <summary>Unified ledger coordinates matching System A's <c>&lt;git-common-dir&gt;/hoshin/&lt;system&gt;/&lt;change&gt;/&lt;repo&gt;.json</c>.</summary>
public sealed record WorkflowLedgerCoordinates(string System, string Change, string Repo);

/// <summary>
/// Resolves unified per-system/change/repo ledger coordinates and performs one-time migration from legacy
/// T07 per-change (<c>.git/hoshin/changes/&lt;change&gt;/workflow-ledger.json</c>) and repo-global
/// (<c>.git/hoshin/workflow-ledger.json</c>) files into <see cref="RepoLedger"/>.
/// </summary>
public static class ChangeLedgerPaths
{
    private static readonly JsonSerializerOptions LegacyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static WorkflowLedgerCoordinates ResolveCoordinates(string root, string changeFolder, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var (system, change) = ResolveSystemAndChange(root, changeFolder, fs);
        var repo = ResolveRepoName(root, system, fs);
        return new WorkflowLedgerCoordinates(Sanitize(system), Sanitize(change), Sanitize(repo));
    }

    public static string ResolveChangeKey(string changeFolder, IFileSystem fs)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var changePath = Path.Combine(changeFolder, "change.jsonc");
        if (fs.FileExists(changePath))
        {
            var raw = fs.ReadAllText(changePath);
            if (Jsonc.TryParse(raw, out var parsed, out _)
                && parsed is { ValueKind: JsonValueKind.Object } obj
                && Jsonc.String(obj, "change") is { Length: > 0 } declared)
            {
                return Sanitize(declared);
            }
        }

        var trimmed = changeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmed);
        return Sanitize(string.IsNullOrWhiteSpace(folderName) ? "default" : folderName);
    }

    public static string LegacyPerChangePath(string hoshinDir, string changeKey) =>
        Path.Combine(hoshinDir, "changes", Sanitize(changeKey), "workflow-ledger.json");

    public static string LegacyGlobalPath(string hoshinDir) =>
        Path.Combine(hoshinDir, "workflow-ledger.json");

    public static RepoLedger? TryMigrateLegacyLedger(
        string hoshinDir,
        string root,
        string changeFolder,
        WorkflowLedgerCoordinates coords,
        RunMarker? marker,
        IFileSystem fs,
        LedgerStore ledgerStore)
    {
        var ledgerDir = Path.Combine(hoshinDir, coords.System, coords.Change);
        var targetFile = Path.Combine(ledgerDir, $"{coords.Repo}.json");
        if (fs.FileExists(targetFile))
        {
            return null;
        }

        var sourcePath = FindLegacySource(hoshinDir, changeFolder, coords.Change, marker, fs);
        if (sourcePath is null)
        {
            return null;
        }

        var migrated = ConvertLegacyFile(sourcePath, coords, root, fs);
        ledgerStore.Write(ledgerDir, migrated);
        try
        {
            fs.DeleteFile(sourcePath);
        }
        catch (IOException)
        {
            // Best-effort cleanup if another process already removed the legacy file.
        }

        return migrated;
    }

    private static string? FindLegacySource(
        string hoshinDir,
        string changeFolder,
        string changeName,
        RunMarker? marker,
        IFileSystem fs)
    {
        var candidates = new List<string> { LegacyPerChangePath(hoshinDir, changeName) };
        var declaredKey = ResolveChangeKey(changeFolder, fs);
        if (!string.Equals(declaredKey, changeName, StringComparison.Ordinal))
        {
            candidates.Add(LegacyPerChangePath(hoshinDir, declaredKey));
        }

        foreach (var candidate in candidates)
        {
            if (fs.FileExists(candidate))
            {
                return candidate;
            }
        }

        var legacyGlobal = LegacyGlobalPath(hoshinDir);
        if (!fs.FileExists(legacyGlobal) || marker is null)
        {
            return null;
        }

        var markerChange = !string.IsNullOrWhiteSpace(marker.Change)
            ? Sanitize(marker.Change)
            : !string.IsNullOrWhiteSpace(marker.ChangeFolder)
                ? ResolveChangeKey(marker.ChangeFolder, fs)
                : null;
        return string.Equals(markerChange, changeName, StringComparison.Ordinal)
            || string.Equals(markerChange, declaredKey, StringComparison.Ordinal)
            ? legacyGlobal
            : null;
    }

    private static RepoLedger ConvertLegacyFile(
        string sourcePath,
        WorkflowLedgerCoordinates coords,
        string root,
        IFileSystem fs)
    {
        LegacyWorkflowLedger? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<LegacyWorkflowLedger>(fs.ReadAllText(sourcePath), LegacyJsonOptions);
        }
        catch (JsonException e)
        {
            throw new CouldNotRunException($"{sourcePath} is not valid JSON: {e.Message.Split('\n')[0]}");
        }

        if (parsed is null)
        {
            throw new CouldNotRunException($"{sourcePath} is not valid JSON: file contains null");
        }

        return LedgerStore.Empty(coords.System, coords.Change, coords.Repo, root) with
        {
            Issued = NonEmpty(parsed.Issued),
            Verdicts = NonEmpty(parsed.Verdicts),
            Baselines = NonEmpty(parsed.Baselines),
            Checks = NonEmpty(parsed.Checks),
            Reviews = NonEmpty(parsed.Reviews),
            WorkflowApprovals = NonEmpty(parsed.Approvals),
            Published = NonEmpty(parsed.Published),
            ActiveSubagent = parsed.ActiveSubagent,
        };
    }

    public static bool TryExtractIntentSystemAndChange(string changeFolder, out string system, out string change)
    {
        system = "";
        change = "";
        if (string.IsNullOrWhiteSpace(changeFolder))
        {
            return false;
        }

        var parts = changeFolder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 3; i >= 0; i--)
        {
            if (parts[i].Equals("intent", StringComparison.OrdinalIgnoreCase)
                && !parts[i + 1].Equals("hooks", StringComparison.OrdinalIgnoreCase)
                && !parts[i + 2].Equals("context", StringComparison.OrdinalIgnoreCase)
                && !parts[i + 2].Equals("hooks", StringComparison.OrdinalIgnoreCase))
            {
                system = parts[i + 1];
                change = parts[i + 2];
                return true;
            }
        }

        return false;
    }

    private static (string System, string Change) ResolveSystemAndChange(string root, string changeFolder, IFileSystem fs)
    {
        if (TryExtractIntentSystemAndChange(changeFolder, out var system, out var change))
        {
            return (system, change);
        }

        var relative = Path.GetRelativePath(root, changeFolder).Replace('\\', '/').Trim('/');
        if (TryExtractIntentSystemAndChange(relative, out system, out change))
        {
            return (system, change);
        }

        return ("default", ResolveChangeKey(changeFolder, fs));
    }

    private static string ResolveRepoName(string root, string system, IFileSystem fs)
    {
        if (TryReadSystemObject(root, system, fs) is not { } sysObj)
        {
            return "master";
        }

        if (sysObj.TryGetProperty("apps", out var appsObj) && appsObj.ValueKind == JsonValueKind.Object)
        {
            var appNames = appsObj.EnumerateObject().Select(p => p.Name).ToList();
            if (appNames.Count == 1)
            {
                return appNames[0];
            }
        }

        return MatchMultiRepoName(root, sysObj) ?? "master";
    }

    private static JsonElement? TryReadSystemObject(string root, string system, IFileSystem fs)
    {
        var markerPath = Path.Combine(root, ".hoshin.json");
        if (!fs.FileExists(markerPath) || !Jsonc.TryParse(fs.ReadAllText(markerPath), out var parsed, out _))
        {
            return null;
        }

        if (parsed is not { ValueKind: JsonValueKind.Object } markerObj
            || !markerObj.TryGetProperty("systems", out var systemsObj)
            || systemsObj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return systemsObj.TryGetProperty(system, out var sysObj) && sysObj.ValueKind == JsonValueKind.Object
            ? sysObj
            : null;
    }

    private static string? MatchMultiRepoName(string root, JsonElement sysObj)
    {
        if (!sysObj.TryGetProperty("repos", out var reposEl) || reposEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var repoFolderName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        foreach (var item in reposEl.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), repoFolderName, StringComparison.Ordinal))
            {
                return repoFolderName;
            }
        }

        return null;
    }

    private static List<T>? NonEmpty<T>(List<T>? list) => list is { Count: > 0 } ? list : null;

    private static string Sanitize(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '-' : c).ToArray();
        var cleaned = new string(chars).Trim('.');
        return cleaned.Length == 0 ? "default" : cleaned;
    }

    private sealed class LegacyWorkflowLedger
    {
        public List<WorkflowIssued>? Issued { get; set; }
        public List<WorkflowVerdict>? Verdicts { get; set; }
        public List<WorkflowBaselineRun>? Baselines { get; set; }
        public List<WorkflowCheckRun>? Checks { get; set; }
        public List<WorkflowReviewRun>? Reviews { get; set; }
        public List<WorkflowApproval>? Approvals { get; set; }
        public List<WorkflowPublished>? Published { get; set; }
        public ActiveSubagent? ActiveSubagent { get; set; }
    }
}
