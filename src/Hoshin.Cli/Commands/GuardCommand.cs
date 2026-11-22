using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Commands;

/// <summary>
/// Dual-harness PreToolUse guard (Claude Code + Antigravity).
/// Enforces static protection rules always and session-aware, step-aware rules via fast-path
/// <c>.git/hoshin/run.json</c> + <c>step.json</c> (never recomputes <see cref="WorkflowState"/> and never locks out
/// unrelated sessions). Always exits <c>0</c> and logs errors to <c>stderr</c>.
/// </summary>
public sealed class GuardCommand : IHoshinCommand
{
    private const string GitPrefix = @"\bgit\s+(?:(?:-C|-c)\s+\S+\s+|--?[\w-]+(?:=\S+)?\s+)*";
    private static readonly TimeSpan StaleStepWindow = TimeSpan.FromHours(24);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly (Regex Pattern, string What)[] Protected =
    [
        (new Regex(@"(^|[\\/\s'""=])\.hoshin\.json\b", RegexOptions.Compiled), "the marker, .hoshin.json, which holds the hooks for every system"),
        (new Regex(@"(^|[\\/\s'""=])intent[\\/]hooks([\\/]|\b)", RegexOptions.Compiled), "the global hook scripts in intent/hooks/"),
        (new Regex(@"(^|[\\/\s'""=])intent[\\/][^\\/\s'""]+[\\/]hooks(\.json\b|[\\/])", RegexOptions.Compiled), "a system's hooks (intent/<system>/hooks.json or hooks/)"),
        (new Regex(@"(^|[\\/\s'""=])intent[\\/][^\\/\s'""]+[\\/][^\\/\s'""]+[\\/]change\.(jsonc|yml)\b", RegexOptions.Compiled), "a change's record, change.jsonc, which only the hoshin program writes"),
        (new Regex(@"[\\/]hoshin[\\/][^\s'""]*\.jsonl?\b", RegexOptions.Compiled), "the hoshin ledger, build records and check events in the git folder"),
    ];

    private static readonly Regex Writes = new(
        @"(^|[^<>&0-9])>{1,2}(?!&)|\btee\b|\bsed\b[^|;&\n]*\s-i|\bperl\b[^|;&\n]*\s-i|\b(rm|mv|cp|truncate|touch|install|ln)\s|\bgit\s+(?:-\S+\s+(?:\S+\s+)?)*(add|rm|mv|checkout|restore|commit|apply|stash)\b|writeFileSync|appendFileSync|\bOut-File\b|\bSet-Content\b|\bAdd-Content\b|\bRemove-Item\b|\bMove-Item\b|\bCopy-Item\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Why)[] Rules =
    [
        (new Regex(GitPrefix + @"push\b", RegexOptions.Compiled), "Pushing happens only in the change.done event, after its checks: run the hoshin done skill."),
        (new Regex(@"\bgh\s+pr\s+(create|merge|edit|close|reopen|ready|review|comment)\b", RegexOptions.Compiled), "Pull requests are opened only by the change.done event, after its checks: run the hoshin done skill."),
        (new Regex(@"\bgh\s+api\b[^|;&\n]*(-X|--method)\s*['""]?(POST|PUT|PATCH|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "GitHub is changed only by the change.done event: run the hoshin done skill."),
        (new Regex(@"\bgit\b[^|;&\n]*\s--no-verify\b|" + GitPrefix + @"(?:commit|merge|am)\b(?:\s+-[a-zA-Z]+)*\s+-[a-zA-Z]*n\b|core\.hooksPath", RegexOptions.Compiled), "A repository's own git hooks always run: commit without --no-verify, and fix what they report."),
        (new Regex(@"\bbaseline\s+(approve|retake)\b", RegexOptions.Compiled), "Only the engineer approves a check that already fails on the base, or retakes a baseline: show them the command and stop."),
    ];

    private static readonly Regex ActiveRunGitMutation = new(
        GitPrefix + @"(add|commit|push|reset|checkout|restore|stash|rm|mv|rebase|cherry-pick|merge|am|apply)\b",
        RegexOptions.Compiled);

    private static readonly Regex ProtectedRunDirs = new(
        @"(^|[\\/\s'""=])(\.hoshin[\\/]|\.git[\\/]|\.agents[\\/]|\.claude-plugin[\\/])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> EditTools =
    [
        "Edit", "Write", "MultiEdit", "NotebookEdit",
        "write_file", "edit_file", "write_to_file", "replace_file_content", "multi_replace_file_content",
    ];

    public string Name => "guard";
    public string Usage => "guard  (a Claude Code or Antigravity PreToolUse hook: reads the hook input on standard input)";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var raw = CliIo.ReadStdinUtf8();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Task.FromResult(ExitCodes.Pass);
            }

            var adapter = HarnessAdapter.Default;
            var ev = adapter.Parse(raw);
            var (repo, why) = Evaluate(ev);

            var output = why is null
                ? adapter.AllowOutput(ev.Harness)
                : adapter.DenyOutput(ev.Harness, why);
            HarnessAdapter.WriteStdoutUtf8(output);
            HarnessAdapter.AppendTrace(repo, "guard", ev, output, why is null ? "allow" : $"deny: {why}", clock);
        }
        catch (Exception e)
        {
            CliIo.WriteStderrLine($"hoshin guard: {e.Message}");
        }

        return Task.FromResult(ExitCodes.Pass);
    }

    public static (WorkflowRepo? Repo, string? Reason) Evaluate(NormalizedHookEvent ev)
    {
        // 1. Static rules (apply even when no active run marker exists, preserving GRAMMAR.md / gate.test.mjs contract).
        var staticReason = StaticRefusal(ev);
        if (staticReason is not null)
        {
            return (null, $"hoshin: {staticReason}");
        }

        // 2. Fast-path step-aware & session-aware checks: read only run.json + step.json, never WorkflowState.
        if (string.IsNullOrWhiteSpace(ev.Cwd))
        {
            return (null, null);
        }

        var repo = WorkflowRepo.Find(ev.Cwd);
        if (repo is null || !File.Exists(repo.RunMarkerPath))
        {
            return (repo, null);
        }

        var marker = repo.ReadRunMarker();
        var step = repo.ReadStep();
        if (marker is null || step is null)
        {
            return (repo, null);
        }

        // Session isolation & staleness check: never lock out unrelated or newer non-workflow sessions.
        if (!IsSessionInActiveRun(ev, marker, step))
        {
            return (repo, null);
        }

        var stepReason = step switch
        {
            { Kind: "WriteDocument" or "AwaitAccept", Task: { } document } => CheckWhileWriting(ev, repo, step, [Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, $"{document}.md")], document),
            { Kind: "Review" } => CheckWhileWriting(ev, repo, step, [Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "build", "review.md")], "review"),
            { Kind: "Fix" } => CheckWhileWriting(
                ev,
                repo,
                step,
                [
                    Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "intent.md"),
                    Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "spec.md"),
                    Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "plan.md"),
                ],
                "fix"),
            _ => CheckDuringBuildStep(ev, repo, step),
        };

        return (repo, stepReason);
    }

    private static bool IsSessionInActiveRun(NormalizedHookEvent ev, RunMarker marker, CurrentStep step)
    {
        if (DateTimeOffset.UtcNow - step.At > StaleStepWindow)
        {
            return false;
        }

        // If neither the run nor the event recorded a session ID, apply to the active run.
        if (string.IsNullOrEmpty(marker.SessionId) || string.IsNullOrEmpty(ev.SessionId))
        {
            return true;
        }

        // Exact match with the owning workflow session.
        if (string.Equals(marker.SessionId, ev.SessionId, StringComparison.Ordinal))
        {
            return true;
        }

        // Active subagent launched for the current build task (e.g. Antigravity subagent has its own conversationId,
        // Claude Code subagent carries agent_id).
        if (step.Kind is "Implement" or "Retry" && (!string.IsNullOrEmpty(step.AgentId) || !string.IsNullOrEmpty(ev.SubagentCallerId)))
        {
            return true;
        }

        // Unrelated session: do not lock it out.
        return false;
    }

    private static string? CheckDuringBuildStep(NormalizedHookEvent ev, WorkflowRepo repo, CurrentStep step)
    {
        if (ev.IsShellTool)
        {
            var command = ev.CommandLine ?? "";
            if (ActiveRunGitMutation.IsMatch(command))
            {
                return "Edit only the working tree during a build; do not run git add, commit, reset, or push by hand—the workflow commits verified test and implementation steps for you.";
            }

            if (ProtectedRunDirs.IsMatch(command) && Writes.IsMatch(command))
            {
                return "The change's documents in .hoshin/, .agents/, .claude-plugin/, and .git/ are not edited during a build.";
            }

            return null;
        }

        if (!ev.IsFileWriteTool || string.IsNullOrEmpty(ev.FilePath))
        {
            return null;
        }

        var full = Path.GetFullPath(ev.FilePath, repo.Root);
        var sep = Path.DirectorySeparatorChar;
        if (full.Contains($"{sep}.hoshin{sep}", PathComparison)
            || full.Contains($"{sep}.git{sep}", PathComparison)
            || full.Contains($"{sep}.agents{sep}", PathComparison)
            || full.Contains($"{sep}.claude-plugin{sep}", PathComparison))
        {
            return "The change's documents in .hoshin/, .agents/, .claude-plugin/, and .git/ are not edited during a build.";
        }

        // Red-phase enforcement: during Implement/Retry, deny production file edits until at least one test file
        // has been created or modified in the working tree.
        if (step is { Kind: "Implement" or "Retry", Task: not null })
        {
            var rel = Path.GetRelativePath(repo.Root, full).Replace('\\', '/');
            if (!WorkflowRepo.IsTestPath(rel, step.TestPatterns, step.TaskTestFiles))
            {
                if (!HasModifiedTestFileInWorkingTree(repo, step))
                {
                    return $"Write the failing test for {step.Task} first; production files may only change after a test file has been written.";
                }
            }
        }

        return null;
    }

    private static bool HasModifiedTestFileInWorkingTree(WorkflowRepo repo, CurrentStep step)
    {
        var status = repo.Git("status", "--porcelain", "-uall");
        if (!status.Ok || string.IsNullOrWhiteSpace(status.Output))
        {
            return false;
        }

        foreach (var raw in status.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
            {
                continue;
            }

            var pathPart = raw[3..].Trim().Trim('"');
            var arrow = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                pathPart = pathPart[(arrow + 4)..].Trim();
            }

            if (WorkflowRepo.IsTestPath(pathPart, step.TestPatterns, step.TaskTestFiles))
            {
                return true;
            }
        }

        return false;
    }

    private static string? CheckWhileWriting(NormalizedHookEvent ev, WorkflowRepo repo, CurrentStep step, IReadOnlyList<string> allowedFiles, string label)
    {
        if (ev.IsShellTool)
        {
            var command = ev.CommandLine ?? "";
            if (ActiveRunGitMutation.IsMatch(command) || (ProtectedRunDirs.IsMatch(command) && Writes.IsMatch(command)))
            {
                return $"While the {label} is written nothing is committed or moved by hand; it is committed for you when you end your turn.";
            }

            return null;
        }

        if (!ev.IsFileWriteTool || string.IsNullOrEmpty(ev.FilePath))
        {
            return null;
        }

        var target = Path.GetFullPath(ev.FilePath, repo.Root);
        foreach (var allowed in allowedFiles)
        {
            if (string.Equals(target, Path.GetFullPath(allowed), PathComparison))
            {
                return null;
            }
        }

        var rels = string.Join(", ", allowedFiles.Select(a => Path.GetRelativePath(repo.Root, Path.GetFullPath(a)).Replace('\\', '/')));
        return $"While the {label} is written only {rels} may change.";
    }

    private static string? StaticRefusal(NormalizedHookEvent ev)
    {
        if (ev.ToolName is not null && EditTools.Contains(ev.ToolName))
        {
            var path = ev.FilePath ?? "";
            foreach (var (pattern, what) in Protected)
            {
                if (pattern.IsMatch(path))
                {
                    return $"{path} is {what}. It is not edited during a build, a review or a fix; the setup skill changes hooks, through a pull request.";
                }
            }

            return null;
        }

        if (!ev.IsShellTool)
        {
            return null;
        }

        var command = ev.CommandLine ?? "";
        foreach (var (pattern, why) in Rules)
        {
            if (pattern.IsMatch(command))
            {
                return why;
            }
        }

        foreach (var (pattern, what) in Protected)
        {
            if (pattern.IsMatch(command) && Writes.IsMatch(command))
            {
                return $"This command would change {what}. It is not changed during a build, a review or a fix; the setup skill changes hooks, through a pull request.";
            }
        }

        return null;
    }

    /// <summary>Preserved static helper for direct callers and legacy unit tests.</summary>
    public static string? Refusal(string tool, JsonElement input)
    {
        var ev = new NormalizedHookEvent(
            HookHarness.ClaudeCode,
            "PreToolUse",
            null,
            null,
            tool,
            null,
            GetString(input, "command"),
            GetString(input, "file_path") ?? GetString(input, "notebook_path"),
            null,
            null,
            null,
            false,
            false,
            null,
            null,
            null,
            [],
            null,
            false,
            null,
            input);
        return StaticRefusal(ev);
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
