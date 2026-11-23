namespace Hoshin.Cli.Hooks;

/// <summary>Single owner for Claude Code and Antigravity tool-name lists shared by <see cref="HarnessAdapter"/>, <see cref="Commands.GuardCommand"/>, and hook manifests.</summary>
public static class HarnessTools
{
    public static readonly string[] ClaudeShellTools = ["Bash"];

    public static readonly string[] ClaudeFileWriteTools = ["Edit", "Write", "MultiEdit", "NotebookEdit"];

    public static readonly string[] ClaudeGuardTools = [.. ClaudeShellTools, .. ClaudeFileWriteTools];

    public static readonly string[] ClaudeSkillTools = ["Skill"];

    public static readonly string[] ClaudeSubagentTools = ["Agent", "Task"];

    public static readonly string[] AntigravityShellTools = ["run_command"];

    public static readonly string[] AntigravityFileWriteTools =
        ["write_file", "edit_file", "write_to_file", "replace_file_content", "multi_replace_file_content"];

    public static readonly string[] AntigravityGuardTools = [.. AntigravityShellTools, .. AntigravityFileWriteTools];

    public static readonly string[] AntigravitySkillReadTools = ["read_file", "view_file"];

    public static readonly string[] AntigravitySubagentTools = ["invoke_subagent"];

    public static readonly string[] AntigravityTaskManageTools = ["manage_task", "manage_subagents"];

    public static readonly string[] AntigravityResultTools = [.. AntigravitySubagentTools, .. AntigravityTaskManageTools];

    private static readonly HashSet<string> AllShellTools =
        new([.. ClaudeShellTools, .. AntigravityShellTools], StringComparer.Ordinal);

    private static readonly HashSet<string> AllFileWriteTools =
        new([.. ClaudeFileWriteTools, .. AntigravityFileWriteTools], StringComparer.Ordinal);

    private static readonly HashSet<string> AllSkillReadTools =
        new(AntigravitySkillReadTools, StringComparer.Ordinal);

    private static readonly HashSet<string> AllSubagentLaunchTools =
        new([.. ClaudeSubagentTools, .. AntigravitySubagentTools], StringComparer.Ordinal);

    private static readonly HashSet<string> AllTaskManageTools =
        new(AntigravityTaskManageTools, StringComparer.Ordinal);

    public static string ToMatcher(IReadOnlyList<string> tools) => string.Join('|', tools);

    public static bool IsShellTool(string? toolName) => toolName is not null && AllShellTools.Contains(toolName);

    public static bool IsFileWriteTool(string? toolName) => toolName is not null && AllFileWriteTools.Contains(toolName);

    public static bool IsSkillReadTool(string? toolName) => toolName is not null && AllSkillReadTools.Contains(toolName);

    public static bool IsSubagentLaunchTool(string? toolName) => toolName is not null && AllSubagentLaunchTools.Contains(toolName);

    public static bool IsTaskManageTool(string? toolName) => toolName is not null && AllTaskManageTools.Contains(toolName);
}
