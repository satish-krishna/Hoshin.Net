using System.Text.Json;
using System.Text.Json.Nodes;
using Hoshin.Cli.Hooks;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>Loads anonymized recorded harness hook payload fixtures from <c>Fixtures/hooks/&lt;harness&gt;/&lt;name&gt;.json</c>.</summary>
internal static class HookFixtureLoader
{
    public static readonly string FixturesRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "hooks");

    public static string FixturePath(string harness, string name) =>
        Path.Combine(FixturesRoot, harness, name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : name + ".json");

    public static string LoadRaw(string harness, string name) =>
        File.ReadAllText(FixturePath(harness, name));

    /// <summary>Loads a recorded JSON fixture and applies targeted JSON-node overrides for E2E test runs.</summary>
    public static string LoadAndPatch(string harness, string name, Action<JsonObject> patch)
    {
        var node = JsonNode.Parse(LoadRaw(harness, name))!.AsObject();
        patch(node);
        return node.ToJsonString();
    }
}

/// <summary>
/// T38: verifies that every hook event and tool matcher in both Claude Code and Antigravity manifests has a recorded
/// anonymized payload fixture under <c>tests/Hoshin.Tests/Fixtures/hooks/</c> and that <see cref="HarnessAdapter.Parse"/>
/// extracts every expected normalized field.
/// </summary>
public sealed class HarnessPayloadFixtureTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void EveryManifestEventAndMatcher_HasRecordedFixture()
    {
        var claudeManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "packaging", "claude-code", "hooks", "hooks.json"))).RootElement;
        foreach (var evProp in claudeManifest.GetProperty("hooks").EnumerateObject())
        {
            var eventName = evProp.Name;
            foreach (var entry in evProp.Value.EnumerateArray())
            {
                if (entry.TryGetProperty("matcher", out var matcherProp) && matcherProp.ValueKind == JsonValueKind.String)
                {
                    var matcher = matcherProp.GetString()!;
                    if (string.Equals(matcher, HarnessTools.ClaudeMcpWritePushMatcher, StringComparison.Ordinal))
                    {
                        var mcpPath = HookFixtureLoader.FixturePath("claude-code", $"{eventName}-mcp-write");
                        Assert.True(File.Exists(mcpPath), $"Missing Claude Code fixture for {eventName} (mcp write/push): {mcpPath}");
                        continue;
                    }

                    foreach (var tool in matcher.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var path = HookFixtureLoader.FixturePath("claude-code", $"{eventName}-{tool}");
                        Assert.True(File.Exists(path), $"Missing Claude Code fixture for {eventName} ({tool}): {path}");
                    }
                }
                else
                {
                    var path = HookFixtureLoader.FixturePath("claude-code", eventName);
                    Assert.True(File.Exists(path), $"Missing Claude Code fixture for {eventName}: {path}");
                }
            }
        }

        var agyManifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "packaging", "antigravity", "hooks.json"))).RootElement;
        foreach (var evProp in agyManifest.GetProperty("hoshin").EnumerateObject())
        {
            var eventName = evProp.Name;
            foreach (var entry in evProp.Value.EnumerateArray())
            {
                if (entry.TryGetProperty("matcher", out var matcherProp) && matcherProp.ValueKind == JsonValueKind.String)
                {
                    foreach (var tool in matcherProp.GetString()!.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var path = HookFixtureLoader.FixturePath("antigravity", $"{eventName}-{tool}");
                        Assert.True(File.Exists(path), $"Missing Antigravity fixture for {eventName} ({tool}): {path}");
                    }
                }
                else
                {
                    var path = HookFixtureLoader.FixturePath("antigravity", eventName);
                    Assert.True(File.Exists(path), $"Missing Antigravity fixture for {eventName}: {path}");
                }
            }
        }
    }

    public sealed record FixtureCase(
        string HarnessDir,
        string FixtureName,
        HookHarness ExpectedHarness,
        string ExpectedEvent,
        string? ExpectedTool,
        bool IsShell = false,
        bool IsFileWrite = false,
        bool IsSkillRead = false,
        bool IsSubagentLaunch = false,
        bool IsTaskManage = false,
        bool IsAsync = false);

    public static TheoryData<FixtureCase> FixtureExpectations =>
    [
        new("claude-code", "UserPromptExpansion", HookHarness.ClaudeCode, "UserPromptExpansion", null),
        new("claude-code", "PreToolUse-Bash", HookHarness.ClaudeCode, "PreToolUse", "Bash", IsShell: true),
        new("claude-code", "PreToolUse-PowerShell", HookHarness.ClaudeCode, "PreToolUse", "PowerShell", IsShell: true),
        new("claude-code", "PreToolUse-mcp-write", HookHarness.ClaudeCode, "PreToolUse", "mcp__github__push_files"),
        new("claude-code", "PreToolUse-Edit", HookHarness.ClaudeCode, "PreToolUse", "Edit", IsFileWrite: true),
        new("claude-code", "PreToolUse-Write", HookHarness.ClaudeCode, "PreToolUse", "Write", IsFileWrite: true),
        new("claude-code", "PreToolUse-MultiEdit", HookHarness.ClaudeCode, "PreToolUse", "MultiEdit", IsFileWrite: true),
        new("claude-code", "PreToolUse-NotebookEdit", HookHarness.ClaudeCode, "PreToolUse", "NotebookEdit", IsFileWrite: true),
        new("claude-code", "PostToolUse-Skill", HookHarness.ClaudeCode, "PostToolUse", "Skill"),
        new("claude-code", "PostToolUse-Agent", HookHarness.ClaudeCode, "PostToolUse", "Agent", IsSubagentLaunch: true),
        new("claude-code", "PostToolUse-Agent-async", HookHarness.ClaudeCode, "PostToolUse", "Agent", IsSubagentLaunch: true, IsAsync: true),
        new("claude-code", "PostToolUse-Task", HookHarness.ClaudeCode, "PostToolUse", "Task", IsSubagentLaunch: true),
        new("claude-code", "SubagentStop", HookHarness.ClaudeCode, "SubagentStop", null),
        new("claude-code", "Stop", HookHarness.ClaudeCode, "Stop", null),

        new("antigravity", "PreInvocation", HookHarness.Antigravity, "PreInvocation", null),
        new("antigravity", "PreToolUse-run_command", HookHarness.Antigravity, "PreToolUse", "run_command", IsShell: true),
        new("antigravity", "PreToolUse-write_file", HookHarness.Antigravity, "PreToolUse", "write_file", IsFileWrite: true),
        new("antigravity", "PreToolUse-edit_file", HookHarness.Antigravity, "PreToolUse", "edit_file", IsFileWrite: true),
        new("antigravity", "PreToolUse-write_to_file", HookHarness.Antigravity, "PreToolUse", "write_to_file", IsFileWrite: true),
        new("antigravity", "PreToolUse-replace_file_content", HookHarness.Antigravity, "PreToolUse", "replace_file_content", IsFileWrite: true),
        new("antigravity", "PreToolUse-multi_replace_file_content", HookHarness.Antigravity, "PreToolUse", "multi_replace_file_content", IsFileWrite: true),
        new("antigravity", "PostToolUse-read_file", HookHarness.Antigravity, "PostToolUse", "read_file", IsSkillRead: true),
        new("antigravity", "PostToolUse-view_file", HookHarness.Antigravity, "PostToolUse", "view_file", IsSkillRead: true),
        new("antigravity", "PostToolUse-invoke_subagent", HookHarness.Antigravity, "PostToolUse", "invoke_subagent", IsSubagentLaunch: true, IsAsync: true),
        new("antigravity", "PostToolUse-manage_task", HookHarness.Antigravity, "PostToolUse", "manage_task", IsTaskManage: true),
        new("antigravity", "PostToolUse-manage_subagents", HookHarness.Antigravity, "PostToolUse", "manage_subagents", IsTaskManage: true),
        new("antigravity", "Stop", HookHarness.Antigravity, "Stop", null),
    ];

    [Theory]
    [MemberData(nameof(FixtureExpectations))]
    public void RecordedFixture_ParsesExpectedNormalizedFields(FixtureCase c)
    {
        var raw = HookFixtureLoader.LoadRaw(c.HarnessDir, c.FixtureName);
        var ev = TestServices.HookAdapter.Parse(raw);

        Assert.Equal(c.ExpectedHarness, ev.Harness);
        Assert.Equal(c.ExpectedEvent, ev.EventName);
        Assert.False(string.IsNullOrWhiteSpace(ev.SessionId));
        Assert.False(string.IsNullOrWhiteSpace(ev.Cwd));
        Assert.Equal(c.ExpectedTool, ev.ToolName);
        Assert.Equal(c.IsShell, ev.IsShellTool);
        Assert.Equal(c.IsFileWrite, ev.IsFileWriteTool);
        Assert.Equal(c.IsSkillRead, ev.IsSkillReadTool);
        Assert.Equal(c.IsSubagentLaunch, ev.IsSubagentLaunchTool);
        Assert.Equal(c.IsTaskManage, ev.IsTaskManageTool);
        Assert.Equal(c.IsAsync, ev.IsAsyncLaunch);

        if (c.IsShell)
        {
            Assert.False(string.IsNullOrWhiteSpace(ev.CommandLine));
        }

        if (c.IsFileWrite || c.IsSkillRead)
        {
            Assert.False(string.IsNullOrWhiteSpace(ev.FilePath));
        }

        if (c.IsSubagentLaunch)
        {
            Assert.False(string.IsNullOrWhiteSpace(ev.SubagentPrompt));
        }

        if (c.IsAsync)
        {
            Assert.False(string.IsNullOrWhiteSpace(ev.LaunchedAgentId));
        }

        if (c.ExpectedEvent == "SubagentStop")
        {
            Assert.False(string.IsNullOrWhiteSpace(ev.StoppedAgentId));
        }

        if (c.ExpectedHarness == HookHarness.Antigravity && c.ExpectedEvent == "Stop")
        {
            Assert.NotNull(ev.FullyIdle);
        }
    }
}
