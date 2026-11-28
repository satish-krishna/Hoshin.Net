using System.Globalization;
using System.Text;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Builds harness-specific orchestrator directives injected by hooks (never placed in skill procedure files).
/// Assembles header/report, body, and closing marker in one place within <see cref="WorkflowBriefs.MaxInjectedChars"/>
/// so middle-truncation never drops the closing marker or splits UTF-16 surrogate pairs.
/// </summary>
public static class HookInstructions
{
    public const string PluginName = "hoshin";
    public const string BeginMarker = "<<<BEGIN SUBAGENT PROMPT";
    public const string EndMarker = "END SUBAGENT PROMPT>>>";

    public static string For(
        Step step,
        HookHarness harness,
        string? dispatchNonce = null,
        string? prefix = null,
        string? suffix = null)
    {
        if (step is Implement i)
        {
            return Dispatch(i.Agent, harness, dispatchNonce, prefix, suffix);
        }

        if (step is Retry r)
        {
            return Dispatch(r.Agent, harness, dispatchNonce, prefix, suffix);
        }

        var body = step switch
        {
            NeedsApproval n => $"Every task is built test first and the repository's checks pass at {WorkflowEngine.Short(n.Head)}. Tell the engineer the change is ready for approval, and that `{SkillCommand("accept", harness)}` approves it. Then end your turn.",
            WriteDocument w => w.Brief,
            ReviewDocument rv => rv.Brief,
            WriteFix fx => fx.Brief,
            AwaitAccept a => $"The {a.Document.ToWireName()} is committed and waits for acceptance. Tell the person to review it at {a.Path}, and that `{SkillCommand("accept", harness)}` accepts it. Then end your turn.",
            AwaitAcceptFix => $"The light-track fix documents are committed and wait for acceptance. Tell the engineer to review them, and that `{SkillCommand("accept", harness)}` or `{SkillCommand("fix", harness)} --accept` accepts them. Then end your turn.",
            Handoff h => $"{h.Message}. Tell the engineer in one sentence, then end your turn.",
            Done { Success: true } d => $"The change is done: {d.Outcome}. Tell the engineer in one sentence, then end your turn.",
            Done d => $"The run stopped: {d.Outcome}. Tell the engineer in one or two sentences, then end your turn.",
            _ => $"Step {step.Kind} is in progress. End your turn.",
        };

        return TextTruncator.TruncateMiddle((prefix ?? "") + body + (suffix ?? ""), WorkflowBriefs.MaxInjectedChars);
    }

    public static string SkillCommand(string skill, HookHarness harness) =>
        harness == HookHarness.Antigravity ? $"/{skill}" : $"/{PluginName}:{skill}";

    private static string Dispatch(
        AgentTask agent,
        HookHarness harness,
        string? dispatchNonce,
        string? prefix,
        string? suffix)
    {
        var directives = new StringBuilder();
        directives.AppendLine(CultureInfo.InvariantCulture, $"Next step: build task {agent.Task}, attempt {agent.Attempt} of {WorkflowEngine.MaxAttempts}.");
        directives.AppendLine();
        if (harness == HookHarness.Antigravity)
        {
            directives.AppendLine(CultureInfo.InvariantCulture, $"Use the `invoke_subagent` tool now, once: `TypeName` \"self\", `Role` \"Build {agent.Task}\", and as `Prompt` the text between the two marker lines below, exactly, without the marker lines.");
            directives.AppendLine("Do not do the task yourself and do not edit, commit, or run anything yourself. After calling `invoke_subagent`, end your turn (call no more tools) and wait for its completion message.");
        }
        else
        {
            directives.AppendLine(CultureInfo.InvariantCulture, $"Use the Agent tool now, once, in the foreground (`run_in_background: false`): `subagent_type` \"general-purpose\", `description` \"Build {agent.Task}\", and as its prompt the text between the two marker lines below, exactly, without the marker lines.");
            directives.AppendLine("Do not do the task yourself and do not edit, commit, or run anything yourself. When the subagent returns, wait for the next step.");
        }

        directives.AppendLine();
        directives.AppendLine(BeginMarker);
        if (!string.IsNullOrWhiteSpace(dispatchNonce))
        {
            directives.AppendLine(dispatchNonce);
        }

        var directiveHeader = directives.ToString();
        var closing = "\n" + EndMarker + "\n" + (suffix ?? "");
        var reportPrefix = prefix ?? "";

        // Reserve room for directives, closing marker, and at least a small slice of the prompt body.
        var maxPrefixBudget = Math.Max(0, WorkflowBriefs.MaxInjectedChars - directiveHeader.Length - closing.Length - 256);
        if (reportPrefix.Length > maxPrefixBudget)
        {
            reportPrefix = TextTruncator.TruncateMiddle(reportPrefix, maxPrefixBudget);
        }

        var fullHeader = reportPrefix + directiveHeader;
        var bodyBudget = Math.Max(0, WorkflowBriefs.MaxInjectedChars - fullHeader.Length - closing.Length);
        var boundedBody = TextTruncator.TruncateMiddle(agent.Prompt.TrimEnd(), bodyBudget);

        return fullHeader + boundedBody + closing;
    }
}
