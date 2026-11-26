using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;

namespace Hoshin.Cli.Commands;

/// <summary>
/// Dual-harness PreToolUse guard (Claude Code + Antigravity).
/// Enforces static protection rules always and session-aware, step-aware rules via fast-path
/// <c>.git/hoshin/run.json</c> + <c>step.json</c> (never recomputes <see cref="WorkflowState"/> and never locks out
/// unrelated sessions). Always exits <c>0</c> and logs errors to <c>stderr</c>.
/// </summary>
public sealed class GuardCommand(
    HarnessAdapter adapter,
    IHookRunResolver runResolver,
    IFileSystem fileSystem,
    TimeProvider timeProvider) : IHoshinCommand
{
    private readonly HarnessAdapter _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    private readonly IHookRunResolver _runResolver = runResolver ?? throw new ArgumentNullException(nameof(runResolver));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private const string GitPrefix = @"\bgit\s+(?:(?:(?:-C|-c|--git-dir|--work-tree|--namespace)(?:=\S+|\s+\S+)|--?[\w-]+(?:=\S+)?)\s+)*";

    private static readonly (Regex Pattern, string What)[] Protected =
    [
        (new Regex(@"(^|[\\/\s'""=>|])\.hoshin\.json\b", RegexOptions.Compiled), "the marker, .hoshin.json, which holds the hooks for every system"),
        (new Regex(@"(^|[\\/\s'""=>|])intent[\\/]hooks([\\/]|\b)", RegexOptions.Compiled), "the global hook scripts in intent/hooks/"),
        (new Regex(@"(^|[\\/\s'""=>|])intent[\\/][^\\/\s'""]+[\\/]hooks(\.json\b|[\\/])", RegexOptions.Compiled), "a system's hooks (intent/<system>/hooks.json or hooks/)"),
        (new Regex(@"(^|[\\/\s'""=>|])intent[\\/][^\\/\s'""]+[\\/][^\\/\s'""]+[\\/]change\.(jsonc|yml)\b", RegexOptions.Compiled), "a change's record, change.jsonc, which only the hoshin program writes"),
        (new Regex(@"[\\/]hoshin[\\/][^\s'""]*\.jsonl?\b", RegexOptions.Compiled), "the hoshin ledger, build records and check events in the git folder"),
    ];

    private static readonly Regex Writes = new(
        @"(^|[^<>&0-9])>{1,2}(?!&)|\btee\b|\bsed\b[^|;&\n]*\s-i|\bperl\b[^|;&\n]*\s-i|\b(rm|mv|cp|truncate|touch|install|ln)\s|\bgit\s+(?:-\S+\s+(?:\S+\s+)?)*(add|rm|mv|checkout|restore|commit|apply|stash)\b|writeFileSync|appendFileSync|\bOut-File\b|\bSet-Content\b|\bAdd-Content\b|\bRemove-Item\b|\bMove-Item\b|\bCopy-Item\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Why)[] Rules =
    [
        (new Regex(GitPrefix + @"push\b", RegexOptions.Compiled), "Pushing happens only in the change.done event, after its checks: run the hoshin done skill."),
        (new Regex(@"\bgit\b[^|;&\n]*(?:-c\s+['""]?alias\.|\bconfig\b[^|;&\n]*\balias\.)", RegexOptions.Compiled | RegexOptions.IgnoreCase), "Git aliases cannot be defined or invoked inline during a hoshin run."),
        (new Regex(@"\bgh\s+pr\s+(create|merge|edit|close|reopen|ready|review|comment)\b", RegexOptions.Compiled), "Pull requests are opened only by the change.done event, after its checks: run the hoshin done skill."),
        (new Regex(@"\bgh\s+api\b[^|;&\n]*(-X|--method)\s*['""]?(POST|PUT|PATCH|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "GitHub is changed only by the change.done event: run the hoshin done skill."),
        (new Regex(@"\bgit\b[^|;&\n]*\s--no-verify\b|" + GitPrefix + @"(?:commit|merge|am)\b[^|;&\n]*\s+-[a-zA-Z]*n\b|core\.hooksPath", RegexOptions.Compiled), "A repository's own git hooks always run: commit without --no-verify, and fix what they report."),
        (new Regex(@"\bbaseline\s+(approve|retake)\b", RegexOptions.Compiled), "Only the engineer approves a check that already fails on the base, or retakes a baseline: show them the command and stop."),
    ];

    private static readonly Regex ActiveRunGitMutation = new(
        GitPrefix + @"(add|commit|push|reset|checkout|restore|stash|rm|mv|rebase|cherry-pick|merge|am|apply)\b",
        RegexOptions.Compiled);

    private static readonly Regex ProtectedRunDirs = new(
        @"(^|[\\/\s'""=])(\.hoshin[\\/]|\.git[\\/]|\.agents[\\/]|\.claude-plugin[\\/])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

            var ev = _adapter.Parse(raw);
            var (repo, why) = Evaluate(ev);

            var output = why is null
                ? _adapter.AllowOutput(ev.Harness)
                : _adapter.DenyOutput(ev.Harness, why);
            HarnessAdapter.WriteStdoutUtf8(output);
            if (repo is not null)
            {
                _adapter.AppendTrace(repo, "guard", ev, output, why is null ? "allow" : $"deny: {why}", clock);
            }
        }
        catch (Exception e)
        {
            CliIo.WriteStderrLine($"hoshin guard: {e.Message}");
        }

        return Task.FromResult(ExitCodes.Pass);
    }

    public (WorkflowRepo? Repo, string? Reason) Evaluate(NormalizedHookEvent ev) =>
        Evaluate(ev, _runResolver, _fs, _time);

    public static (WorkflowRepo? Repo, string? Reason) Evaluate(
        NormalizedHookEvent ev,
        IHookRunResolver runResolver,
        IFileSystem fileSystem,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(runResolver);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // 1. Scope first (D2): guard applies only inside a repository/workspace with an active, non-stale hoshin run for this session.
        var resolved = runResolver.Resolve(ev);
        var repo = resolved?.WorkflowRepo;
        if (repo is null || !fileSystem.FileExists(repo.RunMarkerPath))
        {
            return (null, null);
        }

        if (!HookSessionScope.IsRunSession(repo, ev, timeProvider.GetUtcNow(), out _, out var step))
        {
            return (null, null);
        }

        // 2. Static rules apply to every tool call in the active run and are traced against repo.
        var staticReason = StaticRefusal(ev, repo);
        if (staticReason is not null)
        {
            return (repo, $"hoshin: {staticReason}");
        }

        if (step is null)
        {
            return (repo, null);
        }

        // 3. Fast-path step-aware rules from step.json without recomputing WorkflowState.
        var stepReason = step switch
        {
            { Kind: StepKind.WriteDocument or StepKind.AwaitAccept, Task: { } document } => CheckWhileWriting(ev, repo, step, [Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, $"{document}.md")], document),
            { Kind: StepKind.Review } => CheckWhileWriting(ev, repo, step, [Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "build", "review.md")], "review"),
            { Kind: StepKind.Fix or StepKind.AwaitAcceptFix } => CheckWhileWriting(
                ev,
                repo,
                step,
                [
                    Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "intent.md"),
                    Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "spec.md"),
                    Path.Combine(step.ChangeFolder ?? repo.ChangeFolder, "plan.md"),
                ],
                "fix"),
            { Kind: StepKind.Implement or StepKind.Retry } => CheckDuringBuildStep(ev, ResolveBuildTargetRepo(resolved!), step),
            _ => null,
        };

        return (repo, stepReason);
    }

    private static WorkflowRepo ResolveBuildTargetRepo(ResolvedHookRun resolved)
    {
        var repo = resolved.WorkflowRepo;
        if (string.IsNullOrWhiteSpace(resolved.SessionRepoRoot)
            || PathHelpers.PathsEqual(resolved.SessionRepoRoot, repo.Root))
        {
            return repo;
        }

        var commonDir = Path.GetDirectoryName(repo.HoshinDir) ?? repo.HoshinDir;
        return repo.ForTarget(resolved.SessionRepoRoot, commonDir, resolved.SessionRepoName ?? repo.RepoKey);
    }

    private static string? CheckDuringBuildStep(NormalizedHookEvent ev, WorkflowRepo repo, CurrentStep step)
    {
        if (HarnessTools.IsMcpWriteOrPushTool(ev.ToolName))
        {
            return "MCP file-write and push tools are not allowed during a build; edit only the working tree with the harness file tools and let hoshin commit and push.";
        }

        if (ev.IsShellTool)
        {
            var command = GuardShellNormalizer.Normalize(ev.CommandLine ?? "");
            if (ActiveRunGitMutation.IsMatch(command))
            {
                return "Edit only the working tree during a build; do not run git add, commit, reset, or push by hand—the workflow commits verified test and implementation steps for you.";
            }

            if (ProtectedRunDirs.IsMatch(command) && Writes.IsMatch(command))
            {
                return "The change's documents in .hoshin/, .agents/, .claude-plugin/, and .git/ are not edited during a build.";
            }

            foreach (var target in ShellWriteTargets.Extract(command))
            {
                var denied = GuardStepEvaluator.CheckBuildTargetPath(target, repo, step);
                if (denied is not null)
                {
                    return denied;
                }
            }

            return null;
        }

        if (!ev.IsFileWriteTool || string.IsNullOrEmpty(ev.FilePath))
        {
            return null;
        }

        return GuardStepEvaluator.CheckBuildTargetPath(ev.FilePath, repo, step);
    }

    private static string? CheckWhileWriting(NormalizedHookEvent ev, WorkflowRepo repo, CurrentStep step, IReadOnlyList<string> allowedFiles, string label)
    {
        if (HarnessTools.IsMcpWriteOrPushTool(ev.ToolName))
        {
            return $"While the {label} is written, MCP file-write and push tools are not allowed.";
        }

        if (ev.IsShellTool)
        {
            var command = GuardShellNormalizer.Normalize(ev.CommandLine ?? "");
            if (ActiveRunGitMutation.IsMatch(command) || (ProtectedRunDirs.IsMatch(command) && Writes.IsMatch(command)))
            {
                return $"While the {label} is written nothing is committed or moved by hand; it is committed for you when you end your turn.";
            }

            foreach (var target in ShellWriteTargets.Extract(command))
            {
                var denied = GuardStepEvaluator.CheckWriteTargetPath(target, repo, allowedFiles, label);
                if (denied is not null)
                {
                    return denied;
                }
            }

            return null;
        }

        if (!ev.IsFileWriteTool || string.IsNullOrEmpty(ev.FilePath))
        {
            return null;
        }

        return GuardStepEvaluator.CheckWriteTargetPath(ev.FilePath, repo, allowedFiles, label);
    }

    private static string? StaticRefusal(NormalizedHookEvent ev, WorkflowRepo? repo = null)
    {
        if (ev.IsFileWriteTool)
        {
            var path = ev.FilePath ?? "";
            var tamperEdit = GuardTamperingProtection.CheckEditPath(path, repo);
            if (tamperEdit is not null)
            {
                return tamperEdit;
            }

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

        var command = GuardShellNormalizer.Normalize(ev.CommandLine ?? "");
        var tamperShell = GuardTamperingProtection.CheckShellCommand(command, Writes, repo);
        if (tamperShell is not null)
        {
            return tamperShell;
        }

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
