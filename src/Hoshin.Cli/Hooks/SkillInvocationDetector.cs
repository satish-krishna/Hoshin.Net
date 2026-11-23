using System.Text.RegularExpressions;

namespace Hoshin.Cli.Hooks;

public enum SkillInvocationSource
{
    User,
    Model,
}

public sealed record SkillInvocation(string Skill, string? Args, SkillInvocationSource Source);

/// <summary>Detects which hoshin skill was invoked and whether the invocation came from a human user or the model.</summary>
public static partial class SkillInvocationDetector
{
    private static readonly string[] KnownSkills =
        ["intent", "spec", "plan", "build", "review", "fix", "done", "accept", "reject", "reopen", "setup", "context"];

    [GeneratedRegex(@"(?:^|/)\.agents/skills/([a-z0-9_-]+)/SKILL\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex AgySkillPathRegex();

    [GeneratedRegex(@"(?:^|/)skills/([a-z0-9_-]+)/SKILL\.md$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericSkillPathRegex();

    public static SkillInvocation? Detect(NormalizedHookEvent ev)
    {
        if (ev.IsAntigravity)
        {
            if (ev.EventName == "PostToolUse" && ev.IsSkillReadTool)
            {
                return DetectAntigravitySkillRead(ev);
            }

            if (ev.EventName == "PreInvocation")
            {
                var (skill, args) = ParseSlashCommand(ev.LastUserInput);
                return skill is null ? null : new SkillInvocation(skill, args, SkillInvocationSource.User);
            }

            return null;
        }

        // Claude Code: UserPromptExpansion / UserPromptSubmit are user-typed; PostToolUse(Skill) is model-invoked.
        var name = ev.CommandName;
        if (name is null)
        {
            return null;
        }

        var source = ev.EventName is "UserPromptExpansion" or "UserPromptSubmit"
            ? SkillInvocationSource.User
            : SkillInvocationSource.Model;

        foreach (var prefix in new[] { $"{HookInstructions.PluginName}:", "hoshin-net:" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var skill = name[prefix.Length..].ToLowerInvariant();
                return KnownSkills.Contains(skill) ? new SkillInvocation(skill, ev.CommandArgs, source) : null;
            }
        }

        return null;
    }

    private static SkillInvocation? DetectAntigravitySkillRead(NormalizedHookEvent ev)
    {
        var path = ev.FilePath?.Replace('\\', '/');
        if (path is null)
        {
            return null;
        }

        var match = AgySkillPathRegex().Match(path);
        if (!match.Success && ev.IsSkillFile)
        {
            match = GenericSkillPathRegex().Match(path);
        }

        if (!match.Success)
        {
            return null;
        }

        var skill = match.Groups[1].Value.ToLowerInvariant();
        return KnownSkills.Contains(skill) ? new SkillInvocation(skill, null, SkillInvocationSource.Model) : null;
    }

    private static (string? Skill, string? Args) ParseSlashCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return (null, null);
        }

        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/'))
        {
            return (null, null);
        }

        var space = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        var token = (space < 0 ? trimmed[1..] : trimmed[1..space]).Trim();
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();

        foreach (var prefix in new[] { $"{HookInstructions.PluginName}:", "hoshin-net:" })
        {
            if (token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                token = token[prefix.Length..];
                break;
            }
        }

        token = token.ToLowerInvariant();
        return KnownSkills.Contains(token) ? (token, rest) : (null, null);
    }
}
