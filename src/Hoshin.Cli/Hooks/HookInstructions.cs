using System.Globalization;
using System.Text;
using Hoshin.Workflow.Engine;

namespace Hoshin.Cli.Hooks;

/// <summary>
/// Builds harness-specific orchestrator directives injected by hooks (never placed in skill procedure files).
/// </summary>
public static class HookInstructions
{
    public const string PluginName = "hoshin";
    public const string BeginMarker = "<<<BEGIN SUBAGENT PROMPT";
    public const string EndMarker = "END SUBAGENT PROMPT>>>";

    public static string For(Step step, HookHarness harness) => WorkflowBriefs.TruncateIfNeeded(step switch
    {
        Implement i => Dispatch(i.Agent, harness),
        Retry r => Dispatch(r.Agent, harness),
        NeedsApproval n => $"Every task is built test first and the repository's checks pass at {WorkflowEngine.Short(n.Head)}. Tell the engineer the change is ready for approval, and that `{SkillCommand("accept", harness)}` approves it. Then end your turn.",
        WriteDocument w => w.Brief,
        ReviewDocument rv => rv.Brief,
        WriteFix fx => fx.Brief,
        AwaitAccept a => $"The {a.Document} is committed and waits for acceptance. Tell the person to review it at {a.Path}, and that `{SkillCommand("accept", harness)}` accepts it. Then end your turn.",
        Done { Success: true } d => $"The change is done: {d.Outcome}. Tell the engineer in one sentence, then end your turn.",
        Done d => $"The run stopped: {d.Outcome}. Tell the engineer in one or two sentences, then end your turn.",
        _ => $"Step {step.Kind} is in progress. End your turn.",
    });

    public static string SkillCommand(string skill, HookHarness harness) =>
        harness == HookHarness.Antigravity ? $"/{skill}" : $"/{PluginName}:{skill}";

    private static string Dispatch(AgentTask agent, HookHarness harness)
    {
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"Next step: build task {agent.Task}, attempt {agent.Attempt} of {WorkflowEngine.MaxAttempts}.");
        b.AppendLine();
        if (harness == HookHarness.Antigravity)
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"Use the `invoke_subagent` tool now, once: `TypeName` \"self\", `Role` \"Build {agent.Task}\", and as `Prompt` the text between the two marker lines below, exactly, without the marker lines.");
            b.AppendLine("Do not do the task yourself and do not edit, commit, or run anything yourself. After calling `invoke_subagent`, end your turn (call no more tools) and wait for its completion message.");
        }
        else
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"Use the Agent tool now, once, in the foreground (`run_in_background: false`): `subagent_type` \"general-purpose\", `description` \"Build {agent.Task}\", and as its prompt the text between the two marker lines below, exactly, without the marker lines.");
            b.AppendLine("Do not do the task yourself and do not edit, commit, or run anything yourself. When the subagent returns, wait for the next step.");
        }

        b.AppendLine();
        b.AppendLine(BeginMarker);
        b.Append(agent.Prompt);
        b.AppendLine(EndMarker);
        return b.ToString();
    }
}
