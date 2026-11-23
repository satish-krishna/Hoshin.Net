using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Protects harness configuration (<c>.claude/settings*.json</c>, <c>.claude/hooks/</c>), <c>.git/hooks/</c>,
/// and <c>&lt;git-common-dir&gt;/hoshin</c> workflow state against agent tampering (T03 / S4, S5).
/// </summary>
public static partial class GuardTamperingProtection
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    [GeneratedRegex(@"(^|[\\/\s'""=])\.claude[\\/](settings(?:\.local)?\.json\b|hooks(?:[\\/]|\b))", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeConfigRegex();

    [GeneratedRegex(@"(^|[\\/\s'""=])\.git[\\/]hooks(?:[\\/]|\b)", RegexOptions.IgnoreCase)]
    private static partial Regex GitHooksRegex();

    [GeneratedRegex(@"Generated-By:\s*hoshin\b", RegexOptions.IgnoreCase)]
    private static partial Regex BenignTrailerRegex();

    [GeneratedRegex(@"(?:^|[\\/\s'""=])\.git\b|\brun\.json\b|\bstep\.json\b|\bworkflow-ledger\b|--git-common-dir\b|(?:^|[\\/\s'""=])\.?hoshin(?:[\\/\s'""=]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex HoshinStateTokenRegex();

    public static string? CheckEditPath(string path, WorkflowRepo? repo)
    {
        if (ClaudeConfigRegex().IsMatch(path))
        {
            return $"{path} is harness configuration (.claude/settings* or .claude/hooks/) and cannot be modified during a hoshin run.";
        }

        if (GitHooksRegex().IsMatch(path))
        {
            return $"{path} is under .git/hooks/; a repository's git hooks cannot be edited by the agent.";
        }

        if (repo is not null && IsInsideDirectory(path, repo.Root, repo.HoshinDir))
        {
            return $"{path} is inside hoshin workflow state ({repo.HoshinDir}) and cannot be modified by the agent.";
        }

        return null;
    }

    public static string? CheckShellCommand(string command, Regex writesRegex, WorkflowRepo? repo)
    {
        if (!writesRegex.IsMatch(command))
        {
            return null;
        }

        if (ClaudeConfigRegex().IsMatch(command))
        {
            return "This command would modify harness configuration (.claude/settings* or .claude/hooks/), which is protected during a hoshin run.";
        }

        if (GitHooksRegex().IsMatch(command))
        {
            return "This command would modify .git/hooks/, which is protected during a hoshin run.";
        }

        if (repo is not null && ReferencesHoshinDir(command, repo.HoshinDir))
        {
            return $"This command would modify hoshin workflow state under {repo.HoshinDir}, which only the hoshin program may write.";
        }

        var scrubbed = BenignTrailerRegex().Replace(command, "");
        if (HoshinStateTokenRegex().IsMatch(scrubbed))
        {
            return "This command combines a write operation with protected git/hoshin state tokens (.git, hoshin, run.json, step.json, workflow-ledger, or --git-common-dir). Do not alter hoshin workflow state.";
        }

        return null;
    }

    public static string? DetectMissingRunMarkerTampering(WorkflowRepo repo, WorkflowStateReader stateReader, string? rolesRoot)
    {
        if (File.Exists(repo.RunMarkerPath) || !File.Exists(repo.LedgerPath))
        {
            return null;
        }

        RepoLedger ledger;
        try
        {
            ledger = repo.ReadLedger();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or CouldNotRunException)
        {
            return $"Hoshin run state tampering detected: run.json is missing while ledger exists and could not be read ({e.Message}). Stop and inform the engineer.";
        }

        var hasRecords = ledger.WorkflowIssued.Count > 0
            || ledger.WorkflowVerdicts.Count > 0
            || ledger.WorkflowChecks.Count > 0
            || ledger.WorkflowReviews.Count > 0
            || ledger.WorkflowApprovalRecords.Count > 0
            || ledger.ActiveSubagent is not null;
        if (!hasRecords)
        {
            return null;
        }

        try
        {
            var state = stateReader.Read(repo, rolesRoot);
            if (state is null)
            {
                return null;
            }

            var next = WorkflowEngine.Next(state);
            if (next is Done)
            {
                return null;
            }

            return $"Hoshin run state tampering detected: .git/hoshin/run.json is missing while the ledger has an unfinished run (next step: {next.Kind}). Do not delete or alter .git/hoshin/ files; inform the engineer.";
        }
        catch (CouldNotRunException)
        {
            return null;
        }
    }

    private static bool IsInsideDirectory(string candidatePath, string repoRoot, string protectedDir)
    {
        try
        {
            var fullCandidate = Path.GetFullPath(candidatePath, repoRoot);
            var fullProtected = Path.GetFullPath(protectedDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(fullCandidate, fullProtected, PathComparison)
                || fullCandidate.StartsWith(fullProtected + Path.DirectorySeparatorChar, PathComparison)
                || fullCandidate.StartsWith(fullProtected + Path.AltDirectorySeparatorChar, PathComparison);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ReferencesHoshinDir(string command, string hoshinDir)
    {
        var native = Path.GetFullPath(hoshinDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var forward = native.Replace('\\', '/');
        return command.Contains(native, PathComparison) || command.Contains(forward, PathComparison);
    }
}
