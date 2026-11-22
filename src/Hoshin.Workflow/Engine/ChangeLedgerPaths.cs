using System.Text.Json;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Resolves per-change workflow ledger paths (<c>.git/hoshin/changes/&lt;change&gt;/workflow-ledger.json</c>)
/// and performs one-time migration from the legacy repo-global <c>.git/hoshin/workflow-ledger.json</c> (T07 / E1).
/// </summary>
public static class ChangeLedgerPaths
{
    public static string ResolveChangeKey(string changeFolder)
    {
        var changePath = Path.Combine(changeFolder, "change.jsonc");
        if (File.Exists(changePath)
            && Jsonc.TryParse(File.ReadAllText(changePath), out var parsed, out _)
            && parsed is { ValueKind: JsonValueKind.Object } obj
            && Jsonc.String(obj, "change") is { Length: > 0 } declared)
        {
            return Sanitize(declared);
        }

        var trimmed = changeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmed);
        return Sanitize(string.IsNullOrWhiteSpace(folderName) ? "default" : folderName);
    }

    public static string LedgerPath(string hoshinDir, string changeKey) =>
        Path.Combine(hoshinDir, "changes", Sanitize(changeKey), "workflow-ledger.json");

    public static string LegacyLedgerPath(string hoshinDir) =>
        Path.Combine(hoshinDir, "workflow-ledger.json");

    public static void TryMigrateLegacyLedger(string hoshinDir, string targetLedgerPath, RunMarker? marker)
    {
        if (File.Exists(targetLedgerPath))
        {
            return;
        }

        var legacy = LegacyLedgerPath(hoshinDir);
        if (!File.Exists(legacy) || marker is null)
        {
            return;
        }

        var changeName = !string.IsNullOrWhiteSpace(marker.Change)
            ? Sanitize(marker.Change)
            : !string.IsNullOrWhiteSpace(marker.ChangeFolder)
                ? ResolveChangeKey(marker.ChangeFolder)
                : null;
        if (string.IsNullOrWhiteSpace(changeName))
        {
            return;
        }

        var destination = LedgerPath(hoshinDir, changeName);
        if (File.Exists(destination))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(legacy, destination, overwrite: false);
        }
        catch (IOException)
        {
            // Another process already moved the legacy ledger.
        }
    }

    private static string Sanitize(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '-' : c).ToArray();
        var cleaned = new string(chars).Trim('.');
        return cleaned.Length == 0 ? "default" : cleaned;
    }
}
