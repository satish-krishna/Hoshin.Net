using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Cli.Commands;

/// <summary>GRAMMAR.md PreToolUse guard hook (always exits 0; prints deny JSON only when refusing).</summary>
public sealed class GuardCommand : IHoshinCommand
{
    private const string GitPrefix = @"\bgit\s+(?:(?:-C|-c)\s+\S+\s+|--?[\w-]+(?:=\S+)?\s+)*";

    private static readonly (Regex Pattern, string What)[] Protected =
    [
        (new Regex(@"(^|[\\/\s'""=])\.hoshin\.json\b", RegexOptions.Compiled), "the marker, .hoshin.json, which holds the hooks for every system"),
        (new Regex(@"(^|[\\/\s'""=])intent[\\/]hooks([\\/]|\b)", RegexOptions.Compiled), "the global hook scripts in intent/hooks/"),
        (new Regex(@"(^|[\\/\s'""=])intent[\\/][^\\/\s'""]+[\\/]hooks(\.json\b|[\\/])", RegexOptions.Compiled), "a system's hooks (intent/<system>/hooks.json or hooks/)"),
        (new Regex(@"(^|[\\/\s'""=])intent[\\/][^\\/\s'""]+[\\/][^\\/\s'""]+[\\/]change\.(jsonc|yml)\b", RegexOptions.Compiled), "a change's record, change.jsonc, which only the hoshin program writes"),
        (new Regex(@"[\\/]hoshin[\\/][^\s'""]*\.jsonl?\b", RegexOptions.Compiled), "the hoshin ledger, build records and check events in the git folder"),
    ];

    private static readonly Regex Writes = new(
        @"(^|[^<>&0-9])>{1,2}(?!&)|\btee\b|\bsed\b[^|;&\n]*\s-i|\bperl\b[^|;&\n]*\s-i|\b(rm|mv|cp|truncate|touch|install|ln)\s|\bgit\s+(?:-\S+\s+(?:\S+\s+)?)*(add|rm|mv|checkout|restore|commit|apply|stash)\b|writeFileSync|appendFileSync|\bOut-File\b|\bSet-Content\b",
        RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Why)[] Rules =
    [
        (new Regex(GitPrefix + @"push\b", RegexOptions.Compiled), "Pushing happens only in the change.done event, after its checks: run the hoshin done skill."),
        (new Regex(@"\bgh\s+pr\s+(create|merge|edit|close|reopen|ready|review|comment)\b", RegexOptions.Compiled), "Pull requests are opened only by the change.done event, after its checks: run the hoshin done skill."),
        (new Regex(@"\bgh\s+api\b[^|;&\n]*(-X|--method)\s*['""]?(POST|PUT|PATCH|DELETE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "GitHub is changed only by the change.done event: run the hoshin done skill."),
        (new Regex(@"\bgit\b[^|;&\n]*\s--no-verify\b|" + GitPrefix + @"(?:commit|merge|am)\b(?:\s+-[a-zA-Z]+)*\s+-[a-zA-Z]*n\b|core\.hooksPath", RegexOptions.Compiled), "A repository's own git hooks always run: commit without --no-verify, and fix what they report."),
        (new Regex(@"\bbaseline\s+(approve|retake)\b", RegexOptions.Compiled), "Only the engineer approves a check that already fails on the base, or retakes a baseline: show them the command and stop."),
    ];

    private static readonly HashSet<string> EditTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    public string Name => "guard";
    public string Usage => "guard  (a Claude Code PreToolUse hook: reads the hook input on standard input)";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        try
        {
            var raw = CliIo.ReadStdinUtf8();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                var tool = root.TryGetProperty("tool_name", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString()! : "";
                var input = root.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object ? ti : default;
                var why = Refusal(tool, input);
                if (why is not null)
                {
                    CliIo.PrintJson(new
                    {
                        hookSpecificOutput = new
                        {
                            hookEventName = "PreToolUse",
                            permissionDecision = "deny",
                            permissionDecisionReason = $"hoshin: {why}",
                        },
                    });
                }
            }
        }
        catch (Exception e)
        {
            CliIo.WriteStderrLine($"hoshin guard: {e.Message}");
        }

        return Task.FromResult(ExitCodes.Pass);
    }

    public static string? Refusal(string tool, JsonElement input)
    {
        if (EditTools.Contains(tool))
        {
            var path = GetString(input, "file_path") ?? GetString(input, "notebook_path") ?? "";
            foreach (var (pattern, what) in Protected)
            {
                if (pattern.IsMatch(path))
                {
                    return $"{path} is {what}. It is not edited during a build, a review or a fix; the setup skill changes hooks, through a pull request.";
                }
            }

            return null;
        }

        if (tool != "Bash")
        {
            return null;
        }

        var command = GetString(input, "command") ?? "";
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

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
