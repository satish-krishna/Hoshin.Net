using Hoshin.Workflow.Events;

namespace Hoshin.Workflow.Baseline;

/// <summary>Manages engineer approvals for baseline checks that fail without reporting failure IDs.</summary>
public sealed class BaselineApprovals(LedgerStore? ledgerStore = null, string? programCommand = null)
{
    public static BaselineApprovals Default { get; } = new();

    private readonly LedgerStore _ledger = ledgerStore ?? LedgerStore.Default;
    private readonly string _programCommand = programCommand ?? DefaultProgramCommand();

    public static string DefaultProgramCommand()
    {
        var cmd0 = Environment.GetCommandLineArgs().FirstOrDefault();
        var path = !string.IsNullOrEmpty(cmd0) && cmd0.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(cmd0)
            : Path.Combine(AppContext.BaseDirectory, "hoshin.dll");
        return $"dotnet \"{path}\"";
    }

    public static List<string> ApprovedHooks(RepoLedger? ledger, string baseCommit) =>
        (ledger?.Approvals ?? [])
            .Where(a => a.Base == baseCommit)
            .Select(a => a.Hook)
            .ToList();

    public static List<string> NeedsApproval(BaselineEntry entry, IReadOnlyList<string> approved) =>
        entry.Hooks
            .Where(h => h.Outcome == "failed" && h.Ids is null && !approved.Contains(h.Id))
            .Select(h => h.Id)
            .ToList();

    public RepoLedger? LedgerOf(EventContext ctx) =>
        _ledger.Read(_ledger.LedgerFolder(ctx.Master, ctx.System, ctx.Change), ctx.Repo);

    public string ApprovalProblem(EventContext ctx, string baseCommit, string hook) =>
        $"check {hook} already fails on the base ({baseCommit[..Math.Min(7, baseCommit.Length)]}) and does not say what failed, so a new failure in it could not be told from the old one. Fix it on the base branch, or, if you accept it for this build, run: {_programCommand} baseline approve --json --change {ctx.Change} --repo {ctx.Repo} --hook {hook}";

    public List<string> Approve(EventContext ctx, BaselineEntry? entry, string hook)
    {
        if (entry is null)
        {
            return [$"{ctx.Repo} has no baseline at its base commit yet; build.start takes it, or baseline retake"];
        }

        var check = entry.Hooks.FirstOrDefault(h => h.Id == hook);
        if (check is null)
        {
            var names = entry.Hooks.Count > 0 ? string.Join(", ", entry.Hooks.Select(h => h.Id)) : "none";
            return [$"{hook} is not one of {ctx.Repo}'s checks in its baseline ({names})"];
        }

        if (check.Outcome == "passed")
        {
            return [$"check {hook} passes on the base, so there is nothing to approve"];
        }

        if (check.Ids is not null)
        {
            return [$"check {hook} says what failed on the base ({string.Join(", ", check.Ids)}), so build.finish compares it without an approval"];
        }

        var folder = _ledger.LedgerFolder(ctx.Master, ctx.System, ctx.Change);
        var ledger = _ledger.Read(folder, ctx.Repo) ?? LedgerStore.Empty(ctx.System, ctx.Change, ctx.Repo, ctx.RepoPath);
        if (ApprovedHooks(ledger, entry.Base).Contains(hook))
        {
            return [];
        }

        var updatedApprovals = new List<BaselineApproval>(ledger.Approvals ?? [])
        {
            new(hook, entry.Base, DateTimeOffset.UtcNow.ToString("O")),
        };
        _ledger.Write(folder, ledger with { Approvals = updatedApprovals });
        return [];
    }
}
