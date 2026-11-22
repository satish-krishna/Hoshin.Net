using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

public enum HookHarness
{
    ClaudeCode,
    Antigravity,
}

/// <summary>Harness-neutral normalized hook event produced from Claude Code or Antigravity stdin JSON.</summary>
public sealed record NormalizedHookEvent(
    HookHarness Harness,
    string EventName,
    string? SessionId,
    string? Cwd,
    string? ToolName,
    string? ToolCallId,
    string? CommandLine,
    string? FilePath,
    string? LastUserInput,
    string? CommandName,
    string? CommandArgs,
    bool IsSkillFile,
    bool IsAsyncLaunch,
    string? LaunchedAgentId,
    string? StoppedAgentId,
    string? SubagentCallerId,
    HashSet<string> RunningBackgroundTaskIds,
    bool? FullyIdle,
    bool ContinuationActive,
    string? ToolError,
    JsonElement RawInput)
{
    public bool IsAntigravity => Harness == HookHarness.Antigravity;

    public string HarnessName => IsAntigravity ? "antigravity" : "claude-code";

    public bool IsShellTool => ToolName is "Bash" or "run_command";

    public bool IsFileWriteTool => ToolName is "Edit" or "Write" or "MultiEdit" or "NotebookEdit"
        or "write_file" or "edit_file" or "write_to_file" or "replace_file_content" or "multi_replace_file_content";

    public bool IsSkillReadTool => ToolName is "view_file" or "read_file";

    public bool IsSubagentLaunchTool => ToolName is "Agent" or "Task" or "invoke_subagent";

    public bool IsTaskManageTool => ToolName is "manage_task" or "manage_subagents";
}

/// <summary>Normalizes Claude Code and Antigravity hook stdin payloads and formats harness-specific stdout responses.</summary>
public interface IHookAdapter
{
    NormalizedHookEvent Parse(string rawJson);

    string? EmptyOutput(HookHarness harness);

    string? AllowOutput(HookHarness harness);

    string DenyOutput(HookHarness harness, string reason);

    string ContextOutput(NormalizedHookEvent ev, string text);

    string BlockStopOutput(HookHarness harness, string reason);

    string? SystemMessageOutput(HookHarness harness, string text);
}

/// <summary>
/// Dual-harness adapter auto-detecting Claude Code (<c>snake_case</c>) and Google Antigravity (<c>camelCase</c>) payloads,
/// handling both CLI and IDE Antigravity tool names and reading/writing raw UTF-8 streams safely on Windows.
/// </summary>
public sealed class HarnessAdapter : IHookAdapter
{
    public static HarnessAdapter Default { get; } = new();

    public NormalizedHookEvent ReadFromStdin()
    {
        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var raw = reader.ReadToEnd().TrimStart('\uFEFF');
        return Parse(raw);
    }

    public NormalizedHookEvent Parse(string rawJson)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson);
        var root = doc.RootElement.Clone();
        var harness = DetectHarness(root);

        var eventName = Str(root, "hook_event_name") ?? Str(root, "hookEventName") ?? "";
        var sessionId = Str(root, "session_id") ?? Str(root, "conversationId");
        var cwd = Str(root, "cwd") ?? FirstWorkspacePath(root);
        var toolName = Str(root, "tool_name") ?? ToolCallStr(root, "name");
        var toolCallId = Str(root, "tool_use_id") ?? ToolCallStr(root, "id");
        var commandLine = ToolInputAny(root, "command", "CommandLine", "command_line");
        var filePath = ToolInputAny(root, "file_path", "notebook_path", "Path", "TargetFile", "AbsolutePath", "path", "target_file");
        var lastUserInput = Str(root, "lastUserInput") ?? Str(root, "prompt");
        var commandName = Str(root, "command_name") ?? ToolInputAny(root, "skill", "command", "name");
        var commandArgs = Str(root, "command_args") ?? ToolInputAny(root, "args");
        var isSkillFile = ToolInputBool(root, "IsSkillFile") || ToolInputBool(root, "isSkillFile");

        var isAsync = harness == HookHarness.Antigravity
            ? toolName is "invoke_subagent"
            : Response(root, "isAsync") is { ValueKind: JsonValueKind.True }
              || string.Equals(Response(root, "status")?.GetString(), "async_launched", StringComparison.Ordinal);

        var launchedAgentId = ResponseString(root, "conversationId")
            ?? ResponseString(root, "agentId")
            ?? ResponseString(root, "taskId")
            ?? (isAsync ? toolCallId : null);

        var stoppedAgentId = Str(root, "agent_id")
            ?? Str(root, "agentId")
            ?? Str(root, "taskId")
            ?? ToolInputAny(root, "TaskId", "taskId", "ConversationId");

        var subagentCallerId = Str(root, "agent_id");
        var runningTasks = ReadRunningTasks(root);
        var fullyIdle = Bool(root, "fullyIdle");
        var continuationActive = Flag(root, "continuationActive") || Flag(root, "stop_hook_active");
        var toolError = Str(root, "error");

        return new NormalizedHookEvent(
            harness,
            eventName,
            sessionId,
            cwd,
            toolName,
            toolCallId,
            commandLine,
            filePath,
            lastUserInput,
            commandName,
            commandArgs,
            isSkillFile,
            isAsync,
            launchedAgentId,
            stoppedAgentId,
            subagentCallerId,
            runningTasks,
            fullyIdle,
            continuationActive,
            toolError,
            root);
    }

    public string? EmptyOutput(HookHarness harness) => harness == HookHarness.Antigravity ? "{}" : null;

    public string? AllowOutput(HookHarness harness) => harness == HookHarness.Antigravity ? "{\"decision\":\"allow\"}" : null;

    public string DenyOutput(HookHarness harness, string reason) =>
        harness == HookHarness.Antigravity ? AgyDeny(reason) : ClaudeDeny(reason);

    public string ContextOutput(NormalizedHookEvent ev, string text)
    {
        var bounded = WorkflowBriefs.TruncateIfNeeded(text);
        return ev.IsAntigravity ? AgyInjectSystemMessage(bounded) : ClaudeContext(ev.EventName, bounded);
    }

    public string BlockStopOutput(HookHarness harness, string reason)
    {
        var bounded = WorkflowBriefs.TruncateIfNeeded(reason);
        return harness == HookHarness.Antigravity ? AgyContinue(bounded) : ClaudeBlock(bounded);
    }

    public string? SystemMessageOutput(HookHarness harness, string text) =>
        harness == HookHarness.Antigravity ? "{}" : ClaudeSystemMessage(text);

    public static void WriteStdoutUtf8(string? output)
    {
        if (output is null)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(output);
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes, 0, bytes.Length);
        stdout.Flush();
    }

    public static void AppendTrace(WorkflowRepo? repo, string command, NormalizedHookEvent ev, string? output, string decision, Stopwatch clock)
    {
        if (repo is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(repo.HoshinDir);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("at", DateTimeOffset.UtcNow);
                w.WriteString("harness", ev.HarnessName);
                w.WriteString("command", command);
                w.WriteString("event", ev.EventName);
                w.WriteString("sessionId", ev.SessionId);
                w.WriteString("tool", ev.ToolName);
                w.WriteNumber("ms", Math.Round(clock.Elapsed.TotalMilliseconds, 3));
                w.WriteString("decision", decision);
                w.WriteString("output", output is { Length: > 600 } ? output[..600] + "…" : output);
                w.WriteEndObject();
            }

            var line = Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    File.AppendAllText(repo.HooksLogPath, line);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(10);
                }
            }
        }
        catch
        {
            // Never fail a hook because trace logging hit an I/O issue.
        }
    }

    private static HookHarness DetectHarness(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return HookHarness.ClaudeCode;
        }

        if (input.TryGetProperty("hookEventName", out _)
            || input.TryGetProperty("conversationId", out _)
            || input.TryGetProperty("toolCall", out _)
            || input.TryGetProperty("workspacePaths", out _)
            || input.TryGetProperty("lastUserInput", out _)
            || input.TryGetProperty("invocationNum", out _)
            || input.TryGetProperty("executionNum", out _)
            || input.TryGetProperty("terminationReason", out _)
            || input.TryGetProperty("fullyIdle", out _)
            || input.TryGetProperty("continuationActive", out _))
        {
            return HookHarness.Antigravity;
        }

        return HookHarness.ClaudeCode;
    }

    public static string ClaudeContext(string hookEventName, string text) => WriteJson(w =>
    {
        w.WriteStartObject("hookSpecificOutput");
        w.WriteString("hookEventName", hookEventName);
        w.WriteString("additionalContext", text);
        w.WriteEndObject();
    });

    public static string ClaudeBlock(string reason) => WriteJson(w =>
    {
        w.WriteString("decision", "block");
        w.WriteString("reason", reason);
    });

    public static string ClaudeDeny(string reason) => WriteJson(w =>
    {
        w.WriteStartObject("hookSpecificOutput");
        w.WriteString("hookEventName", "PreToolUse");
        w.WriteString("permissionDecision", "deny");
        w.WriteString("permissionDecisionReason", reason);
        w.WriteEndObject();
    });

    public static string ClaudeSystemMessage(string text) => WriteJson(w => w.WriteString("systemMessage", text));

    public static string AgyDeny(string reason) => WriteJson(w =>
    {
        w.WriteString("decision", "deny");
        w.WriteString("reason", reason);
    });

    public static string AgyContinue(string reason) => WriteJson(w =>
    {
        w.WriteString("decision", "continue");
        w.WriteString("reason", reason);
    });

    public static string AgyInjectSystemMessage(string text) => WriteJson(w =>
    {
        w.WriteStartArray("injectSteps");
        w.WriteStartObject();
        w.WriteStartObject("systemMessage");
        w.WriteString("systemMessage", text);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteEndArray();
    });

    private static string WriteJson(Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    private static bool Flag(JsonElement obj, string name) => Bool(obj, name) == true;

    private static string? FirstWorkspacePath(JsonElement obj) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty("workspacePaths", out var arr)
        && arr.ValueKind == JsonValueKind.Array
        && arr.GetArrayLength() > 0
        && arr[0].ValueKind == JsonValueKind.String
            ? arr[0].GetString()
            : null;

    private static string? ToolCallStr(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object
        && obj.TryGetProperty("toolCall", out var tc)
        && tc.ValueKind == JsonValueKind.Object
        && tc.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? ToolInputAny(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetObjectString(root, "tool_input", name, out var claude))
            {
                return claude;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("toolCall", out var tc)
                && tc.ValueKind == JsonValueKind.Object
                && TryGetObjectString(tc, "args", name, out var agy))
            {
                return agy;
            }
        }

        return null;
    }

    private static bool ToolInputBool(JsonElement root, string name)
    {
        if (TryGetObjectBool(root, "tool_input", name))
        {
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("toolCall", out var tc)
            && tc.ValueKind == JsonValueKind.Object
            && TryGetObjectBool(tc, "args", name))
        {
            return true;
        }

        return false;
    }

    private static JsonElement? Response(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var container in new[] { "tool_response", "toolResult", "toolResponse", "result", "response" })
        {
            if (root.TryGetProperty(container, out var r) && r.ValueKind == JsonValueKind.Object && r.TryGetProperty(name, out var v))
            {
                return v;
            }
        }

        return null;
    }

    private static string? ResponseString(JsonElement root, string name) =>
        Response(root, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static HashSet<string> ReadRunningTasks(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("background_tasks", out var tasks) &&
            tasks.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tasks.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Object &&
                    t.TryGetProperty("status", out var s) && s.GetString() == "running" &&
                    t.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    set.Add(id.GetString()!);
                }
            }
        }

        return set;
    }

    private static bool TryGetObjectString(JsonElement parent, string objName, string key, out string? value)
    {
        value = null;
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(objName, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (obj.TryGetProperty(key, out var exact) && exact.ValueKind == JsonValueKind.String)
        {
            value = exact.GetString();
            return true;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                value = p.Value.GetString();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetObjectBool(JsonElement parent, string objName, string key)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(objName, out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True)
            {
                return true;
            }
        }

        return false;
    }
}
