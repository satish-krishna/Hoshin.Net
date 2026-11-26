using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Hoshin.Tests.Acceptance.Harness;

/// <summary>One Claude Code session: a compliant model that does exactly what the hooks inject, and nothing else.</summary>
internal sealed class Session(AcceptanceWorkspace ws, string id, string cwd)
{
    private const int MaxHookRoundsPerTurn = 12;
    private static readonly Regex WriteRequest = new(@"Write (?:it|the review section) to `(?<rel>[^`]+)` in (?<root>.+?)(?: \(|, from|\.\s|\.$)", RegexOptions.Multiline);
    private static readonly Regex FixDocuments = new(@"`(?<rel>[^`\s]*?(?<file>intent|spec|plan)\.md)`");
    private static readonly Regex FixRoot = new(@" in (?<root>.+?): `");
    private static readonly Regex TaskHeading = new(@"^# Task (?<id>\S+?):", RegexOptions.Multiline);
    private static readonly Regex RepositoryLine = new(@"^Repository: (?<path>.+?)\s*$", RegexOptions.Multiline);
    private static readonly Regex AttemptLine = new(@"attempt (?<n>\d+) of \d+");
    private readonly List<string> _log = [];
    private readonly List<string> _told = [];

    public string Id => id;

    public string Cwd => cwd;

    public DocumentRef? LastDocument { get; private set; }

    /// <summary>Everything the hooks told this session (injected context and Stop block reasons), in order.</summary>
    public IReadOnlyList<string> Told => _told;

    /// <summary>The engineer types a slash command.</summary>
    public string? Prompt(string skill, string args = "") => Context(Hook("hook start", PromptPayload(skill, args)));

    /// <summary>The model invokes a skill on its own (not typed by the engineer), through the Skill tool.</summary>
    public string? ModelInvokedSkill(string skill, string args = "") => Context(Hook("hook start", JsonSerializer.Serialize(new
    {
        session_id = id,
        transcript_path = $"/tmp/{id}.jsonl",
        cwd,
        permission_mode = "default",
        hook_event_name = "PostToolUse",
        tool_name = "Skill",
        tool_use_id = "toolu_skill",
        tool_input = args.Length == 0 ? (object)new { skill = $"hoshin:{skill}" } : new { skill = $"hoshin:{skill}", args },
        tool_response = new { success = true, commandName = $"hoshin:{skill}" },
    })));

    /// <summary>Plays the rest of the turn: dispatch what is asked, write what is asked, then try to end the turn.</summary>
    public void Turn(string? context)
    {
        var pending = context;
        for (var round = 0; round < MaxHookRoundsPerTurn; round++)
        {
            while (pending is not null && pending.Contains(AcceptanceWorkspace.BeginMarker, StringComparison.Ordinal))
            {
                pending = Dispatch(pending);
            }

            if (pending is not null)
            {
                WriteWhatIsAsked(pending);
            }

            var stop = Hook("hook stop", StopPayload(stopHookActive: round > 0));
            pending = BlockReason(stop);
            if (pending is null)
            {
                return;
            }
        }

        throw Fail($"the session was never allowed to end its turn after {MaxHookRoundsPerTurn} Stop hooks");
    }

    /// <summary>The model ends its turn once; returns what the Stop hook said, or null when the turn may end.</summary>
    public string? EndTurnOnce(bool stopHookActive = false) => BlockReason(Hook("hook stop", StopPayload(stopHookActive)));

    public void AssertTurnEndsQuietly(string because)
    {
        var stop = Hook("hook stop", StopPayload(stopHookActive: false));
        Assert.True(BlockReason(stop) is null, Transcript($"{because}; Stop was blocked"));
    }

    private void WriteWhatIsAsked(string context)
    {
        if (WriteRequest.Match(context) is { Success: true } write)
        {
            var doc = new DocumentRef(write.Groups["root"].Value.Trim(), write.Groups["rel"].Value.Trim());
            ws.WriteDocument(doc, context);
            LastDocument = doc;
            return;
        }

        var fixDocs = FixDocuments.Matches(context).Select(m => m.Groups["rel"].Value).Distinct(StringComparer.Ordinal).ToList();
        if (fixDocs.Count == 3)
        {
            var root = FixRoot.Match(context) is { Success: true } r ? r.Groups["root"].Value.Trim() : cwd;
            var docs = fixDocs.Select(rel => new DocumentRef(root, rel)).ToList();
            ws.WriteFixDocuments(docs, context);
            LastDocument = docs.Last();
        }
    }

    /// <summary>Dispatches the subagent a context asks for, does its work, and returns what the result hook says next.</summary>
    public string? Dispatch(string context)
    {
        if (!ws.RejectionsExpected)
        {
            Assert.DoesNotContain("is not accepted", context, StringComparison.Ordinal);
        }

        var start = context.IndexOf(AcceptanceWorkspace.BeginMarker, StringComparison.Ordinal);
        var end = context.IndexOf(AcceptanceWorkspace.EndMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, Transcript("a dispatch must carry the subagent prompt between both markers"));
        var prompt = context[(start + AcceptanceWorkspace.BeginMarker.Length)..end].Trim('\r', '\n');
        var task = TaskHeading.Match(prompt) is { Success: true } t ? t.Groups["id"].Value : "?";
        var repo = RepositoryLine.Match(prompt) is { Success: true } r ? r.Groups["path"].Value : "";
        var attempt = AttemptLine.Match(context[..start]) is { Success: true } a ? int.Parse(a.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
        Assert.True(
            BuildsHere(repo),
            Transcript($"a session started in {Path.GetFileName(cwd)} must only build {Path.GetFileName(cwd)}'s tasks, but it dispatched {task} for `{repo}` (its CLAUDE.md, skills and hooks are not loaded here)"));

        ws.Dispatches.Add((id, repo, task, attempt, prompt));
        ws.DoTask(repo, task);
        ws.AfterTask?.Invoke(repo, task, attempt);
        var result = Context(Hook("hook result", JsonSerializer.Serialize(new
        {
            session_id = id,
            transcript_path = $"/tmp/{id}.jsonl",
            cwd,
            permission_mode = "default",
            hook_event_name = "PostToolUse",
            tool_name = "Agent",
            tool_use_id = attempt == 1 ? $"toolu_agent_{task}" : $"toolu_agent_{task}_{attempt}",
            tool_input = new { subagent_type = "general-purpose", description = $"Build {task}", prompt },
            tool_response = new { status = "completed", content = new[] { new { type = "text", text = $"Wrote the test first, then the change for {task}." } } },
        })));
        if (result is not null && !ws.RejectionsExpected)
        {
            Assert.True(!result.Contains("is not accepted", StringComparison.Ordinal), Transcript($"{task} done correctly by the subagent must be accepted"));
        }

        return result;
    }

    /// <summary>
    /// A session builds only the repository it was started in. In a single-repository workspace that repository's
    /// change is built in the change's copy of it, which is the same repository.
    /// </summary>
    private bool BuildsHere(string repository) =>
        AcceptanceWorkspace.PathsEqual(repository, cwd)
        || (ws.Spec.SingleRepo && AcceptanceWorkspace.IsUnder(repository, Path.Combine(ws.Root, ".hoshin", "copies")));

    public string PromptPayload(string skill, string args) => JsonSerializer.Serialize(new
    {
        session_id = id,
        transcript_path = $"/tmp/{id}.jsonl",
        cwd,
        permission_mode = "default",
        hook_event_name = "UserPromptExpansion",
        command_name = $"hoshin:{skill}",
        command_args = args,
        prompt = args.Length == 0 ? $"/hoshin:{skill}" : $"/hoshin:{skill} {args}",
    });

    public string StopPayload(bool stopHookActive) => JsonSerializer.Serialize(new
    {
        session_id = id,
        transcript_path = $"/tmp/{id}.jsonl",
        cwd,
        permission_mode = "default",
        hook_event_name = "Stop",
        stop_hook_active = stopHookActive,
        background_tasks = Array.Empty<object>(),
        last_assistant_message = "Done.",
    });

    public string ToolPayload(string tool, object input) => JsonSerializer.Serialize(new
    {
        session_id = id,
        transcript_path = $"/tmp/{id}.jsonl",
        cwd,
        permission_mode = "default",
        hook_event_name = "PreToolUse",
        tool_name = tool,
        tool_use_id = $"toolu_{tool}",
        tool_input = input,
    });

    public string Hook(string verb, string payload)
    {
        var stdout = ws.RunHook(cwd, verb, payload);
        _log.Add($"[{id} @ {Path.GetFileName(cwd)}] {verb} {Summarize(payload)}\n  -> {(stdout.Length == 0 ? "(silent)" : stdout)}");
        if (ToldBy(stdout) is { } told)
        {
            _told.Add(told);
        }

        return stdout;
    }

    public string? Context(string stdout)
    {
        if (stdout.Length == 0 || stdout == "{}")
        {
            return null;
        }

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        if (root.TryGetProperty("hookSpecificOutput", out var h) && h.TryGetProperty("additionalContext", out var c))
        {
            return c.GetString();
        }

        return root.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
    }

    private string? ToldBy(string stdout)
    {
        try
        {
            return Context(stdout);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? BlockReason(string stdout)
    {
        if (stdout.Length == 0 || stdout == "{}")
        {
            return null;
        }

        using var doc = JsonDocument.Parse(stdout);
        return doc.RootElement.TryGetProperty("decision", out var d) && d.GetString() == "block"
            ? doc.RootElement.GetProperty("reason").GetString()
            : null;
    }

    private static string Summarize(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        return r.GetProperty("hook_event_name").GetString() switch
        {
            "UserPromptExpansion" => r.GetProperty("prompt").GetString()!,
            "PostToolUse" => $"{r.GetProperty("tool_name").GetString()} result",
            "PreToolUse" => $"{r.GetProperty("tool_name").GetString()} {r.GetProperty("tool_input")}",
            "Stop" => $"Stop (stop_hook_active={r.GetProperty("stop_hook_active").GetBoolean()})",
            var other => other ?? "?",
        };
    }

    public string Transcript(string message) =>
        $"{message}\n\n--- hook transcript of session {id} ---\n{string.Join("\n", _log.TakeLast(16))}\n\n{ws.HookDecisions()}";

    public Exception Fail(string message) => new Xunit.Sdk.XunitException(Transcript(message));
}
