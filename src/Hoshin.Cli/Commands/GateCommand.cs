using System.Text.Json;
using Hoshin.Workflow.Baseline;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Events;

namespace Hoshin.Cli.Commands;

/// <summary>GRAMMAR.md Stop gate hook (always exits 0; prints decision JSON only when blocking or noting exhausted retries).</summary>
public sealed class GateCommand(HoshinServices services) : IHoshinCommand
{
    private const int TailLines = 60;

    public string Name => "gate";
    public string Usage => "gate   (a Claude Code Stop hook: reads the hook input on standard input)";

    public Task<int> RunAsync(IReadOnlyList<string> args)
    {
        try
        {
            var cwd = ReadCwdFromStdin() ?? Directory.GetCurrentDirectory();
            var (block, note) = Decide(cwd);
            if (block is not null)
            {
                CliIo.PrintJson(new { decision = "block", reason = block });
            }
            else if (note is not null)
            {
                CliIo.PrintJson(new { systemMessage = note });
            }
        }
        catch (Exception e)
        {
            CliIo.WriteStderrLine($"hoshin gate: {e.Message}");
        }

        return Task.FromResult(ExitCodes.Pass);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private (string? Block, string? Note) Decide(string cwd)
    {
        var top = services.Git.TopLevel(cwd);
        if (top is null)
        {
            return (null, null);
        }

        var active = services.BuildStartStep.ReadActiveBuild(top);
        if (active is null)
        {
            return (null, null);
        }

        var folder = Path.Combine(active.Master, "intent", active.System, active.Change);
        if (!Directory.Exists(folder) || services.Git.CurrentBranch(top) != services.ChangeRecords.BranchOf(folder))
        {
            return (null, null);
        }

        var missing = services.Progress.UnloggedTasks(folder, active.Repo);
        if (missing is null || missing.Count > 0)
        {
            return (null, null);
        }

        var head = services.Apps.IsSingleRepo(active.Master) && active.Repo != "master"
            ? services.Apps.CurrentAppHead(top, active.Master, active.System, active.Repo)
            : services.Git.CommitOf(top, "HEAD");
        if (head is null)
        {
            return (null, null);
        }

        var run = $"{BaselineApprovals.DefaultProgramCommand()} run build.finish --json --change {active.Change} --repo {active.Repo} --master \"{active.Master}\"";
        var ledgers = services.Ledger.LedgerFolder(active.Master, active.System, active.Change);
        var ledger = services.Ledger.Read(ledgers, active.Repo);
        var finish = LedgerStore.Latest(ledger, "build.finish", head);
        var shortHead = head[..Math.Min(7, head.Length)];

        if (finish is null)
        {
            return ($"Every task for {active.Repo} has a build log entry, but its build.finish event has not run at {shortHead}, so its checks have not run on this work. Run it and act on its outcome (references/workspace.md, section 11):\n{run}", null);
        }

        if (finish.Outcome != "failed" || finish.Failed?.OnFailure != "fix" || ledger is null)
        {
            return (null, null);
        }

        var blockKey = $"{finish.Failed.Id}@{head}";
        var count = ledger.GateBlocks.GetValueOrDefault(blockKey, 0);
        var gateRetries = services.HookConfig.ReadFromBaseBranch(active.Master, active.System).GateRetries;
        if (count >= gateRetries)
        {
            return (null, $"hoshin: hook {finish.Failed.Id} still fails on {active.Repo} after {count} attempts to fix it; the build stops here and the engineer decides what happens next.");
        }

        var updatedCount = count + 1;
        services.Ledger.Update(ledgers, active.Repo, current =>
        {
            var liveCount = current.GateBlocks.GetValueOrDefault(blockKey, 0);
            updatedCount = liveCount + 1;
            var updatedBlocks = new Dictionary<string, int>(current.GateBlocks, StringComparer.Ordinal) { [blockKey] = updatedCount };
            return current with { GateBlocks = updatedBlocks };
        });

        var hookOut = finish.Hooks.FirstOrDefault(h => h.Id == finish.Failed.Id)?.Output ?? "";
        var output = string.Join('\n', hookOut.Replace("\r\n", "\n").Split('\n').TakeLast(TailLines));
        var problems = finish.Problems.Count > 0 ? $"\n\nWhat failed:\n{string.Join('\n', finish.Problems)}" : "";
        return ($"The check {finish.Failed.Id} failed on {active.Repo} at {shortHead} (attempt {updatedCount} of {gateRetries}). Find and fix the fault it reports in the repository, commit the fix following the commit rules, then run build.finish again:\n{run}{problems}\n\nThe end of its output:\n{output}", null);
    }

    private static string? ReadCwdFromStdin()
    {
        try
        {
            var raw = CliIo.ReadStdinUtf8();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("cwd", out var cwdProp) &&
                cwdProp.ValueKind == JsonValueKind.String
                ? cwdProp.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
