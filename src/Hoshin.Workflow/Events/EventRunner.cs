using System.Text.Json;
using Hoshin.Workflow.Events.Steps;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Events;

public sealed record StreamProgressEvent(string Type, string Id, string When, string? Outcome = null, bool? Check = null);

public sealed record EventRunOptions(
    bool Again = false,
    bool CheckOnly = false,
    Action<StreamProgressEvent>? Progress = null,
    HookConfig? Config = null);

public sealed record EventOutcome(
    string Event,
    string System,
    string Change,
    string Repo,
    string Key,
    string? Subject,
    string Outcome,
    bool Reused,
    List<string> Problems,
    List<HookRecord> Hooks,
    FailedHookInfo? Failed,
    Dictionary<string, object?> Details);

/// <summary>Orchestrates lifecycle events: preconditions, before hooks, built-in step, after hooks, finish, and ledger persistence.</summary>
public sealed class EventRunner
{
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGitClient _git;
    private readonly HookConfigService _hookConfig;
    private readonly LedgerStore _ledger;
    private readonly HookScriptExporter _scripts;
    private readonly HookProcessRunner _processRunner;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, IEventStep> _steps;

    public EventRunner(
        IGitClient git,
        HookConfigService hookConfig,
        LedgerStore ledger,
        HookScriptExporter scripts,
        HookProcessRunner processRunner,
        IEnumerable<IEventStep> steps,
        TimeProvider timeProvider)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _hookConfig = hookConfig ?? throw new ArgumentNullException(nameof(hookConfig));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _scripts = scripts ?? throw new ArgumentNullException(nameof(scripts));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var stepList = (steps ?? throw new ArgumentNullException(nameof(steps))).ToList();
        _steps = stepList.ToDictionary(s => s.EventName, StringComparer.Ordinal);
        if (_steps.TryGetValue("change.done", out var doneStep) && doneStep is ChangeDoneStep cds)
        {
            cds.BindRunner(RunAsync);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public async Task<EventOutcome> RunAsync(EventContext ctx, EventRunOptions? options = null)
    {
        options ??= new EventRunOptions();
        var step = _steps[ctx.Event];
        var key = step.Key(ctx);
        var subject = step.Subject(ctx);
        var folder = _ledger.LedgerFolder(ctx.Master, ctx.System, ctx.Change);
        var ledger = _ledger.Read(folder, ctx.Repo) ?? LedgerStore.Empty(ctx.System, ctx.Change, ctx.Repo, ctx.RepoPath);

        EventOutcome Make(
            string outcome,
            bool reused = false,
            List<string>? problems = null,
            List<HookRecord>? hooks = null,
            FailedHookInfo? failed = null,
            Dictionary<string, object?>? details = null) =>
            new(ctx.Event, ctx.System, ctx.Change, ctx.Repo, key, subject, outcome, reused,
                problems ?? [], hooks ?? [], failed, details ?? new Dictionary<string, object?>());

        EventOutcome Record(EventOutcome o)
        {
            var mergedProblems = o.Problems.Concat(ctx.CheckProblems ?? []).ToList();
            var updated = o with { Problems = mergedProblems };
            var failedSummary = updated.Failed is null ? null : new FailedHookSummary(updated.Failed.Id, updated.Failed.OnFailure);
            var detailsOrNull = updated.Details.Count > 0 ? updated.Details : null;
            var entry = new EventRecord(
                ctx.Event,
                ctx.Repo,
                key,
                subject,
                updated.Outcome,
                _time.GetUtcNow().ToString("O"),
                updated.Hooks,
                updated.Problems,
                failedSummary,
                detailsOrNull);
            _ledger.Update(
                folder,
                ctx.Repo,
                () => LedgerStore.Empty(ctx.System, ctx.Change, ctx.Repo, ctx.RepoPath),
                fresh => LedgerStore.AppendRun(fresh with { Path = ctx.RepoPath }, entry));
            return updated;
        }

        var preconditions = await step.CheckAsync(ctx).ConfigureAwait(false);
        if (options.CheckOnly)
        {
            if (preconditions.Count > 0)
            {
                return Make("refused", problems: preconditions);
            }

            var checkConfig = options.Config ?? _hookConfig.ReadFromBaseBranch(ctx.Master, ctx.System);
            if (checkConfig.Problems.Count > 0)
            {
                return Make("could-not-run", problems: checkConfig.Problems.Select(p => $"the hook configuration on the base branch: {p}").ToList());
            }

            return Make("passed", details: new Dictionary<string, object?> { ["checkOnly"] = true });
        }

        if (preconditions.Count > 0)
        {
            return Record(Make("refused", problems: preconditions));
        }

        var idle = step.Idle(ctx);
        if (idle is not null)
        {
            return Record(Make("passed", details: idle));
        }

        var previous = LedgerStore.Latest(ledger, ctx.Event, key, subject);
        if (step.ReusePassed && !options.Again && previous?.Outcome == "passed")
        {
            return Make("passed", reused: true, hooks: previous.Hooks);
        }

        var config = options.Config ?? _hookConfig.ReadFromBaseBranch(ctx.Master, ctx.System);
        if (config.Problems.Count > 0)
        {
            return Make("could-not-run", problems: config.Problems.Select(p => $"the hook configuration on the base branch: {p}").ToList());
        }

        ctx.CheckProblems = [];
        var log = new CheckLog(CheckLog.ChecksFile(_git, ctx.Master, ctx.System, ctx.Change), ctx.Event, ctx.Repo, ctx.CheckProblems, _time);
        using var scripts = _scripts.Export(ctx.Master, config.Revision, ctx.System);

        var details = new Dictionary<string, object?>();
        var prepared = await step.PrepareAsync(ctx, config).ConfigureAwait(false);
        MergeDetails(details, prepared.Details);
        if (prepared.Problems.Count > 0)
        {
            return Record(Make(prepared.Outcome ?? "failed", problems: prepared.Problems, failed: prepared.Failed, details: details));
        }

        var beforeHooks = HookConfigService.HooksFor(config, ctx.Event, "before", ctx.Repo);
        var (beforeRecords, stoppedBy) = await RunHooksAsync(beforeHooks, ctx, scripts, details, options.Progress, prepared.Judge, log).ConfigureAwait(false);
        if (stoppedBy is not null)
        {
            var failure = beforeRecords[^1];
            var outcome = failure.Outcome == "could-not-run" ? "could-not-run" : "failed";
            var problem = stoppedBy.Value.Problem ?? $"hook {failure.Id} {(failure.Outcome == "failed" ? $"failed with exit code {failure.ExitCode}" : "could not run")}";
            return Record(Make(
                outcome,
                problems: [problem],
                hooks: beforeRecords,
                failed: new FailedHookInfo(failure.Id, stoppedBy.Value.OnFailure, failure.Output),
                details: details));
        }

        var acted = await step.ActAsync(ctx).ConfigureAwait(false);
        MergeDetails(details, acted.Details);
        if (acted.Problems.Count > 0)
        {
            return Record(Make("failed", problems: acted.Problems, hooks: beforeRecords, details: details));
        }

        var afterHooks = HookConfigService.HooksFor(config, ctx.Event, "after", ctx.Repo);
        var (afterRecords, _) = await RunHooksAsync(afterHooks, ctx, scripts, details, options.Progress, null, null).ConfigureAwait(false);
        var allHooks = beforeRecords.Concat(afterRecords).ToList();

        var warnings = afterRecords
            .Where(h => h.Outcome != "passed")
            .Select(h => $"after hook {h.Id} {(h.Outcome == "failed" ? $"failed with exit code {h.ExitCode}" : "could not run")}; nothing was undone")
            .Concat(HookConfigService.Deprecations(allHooks.Select(h => new HookDefinition(h.Id, ctx.Event, h.When, [], null, null, 0, false, "stop", "global"))))
            .ToList();

        var finished = await step.FinishAsync(ctx, config).ConfigureAwait(false);
        MergeDetails(details, finished.Details);
        if (finished.Problems.Count > 0)
        {
            return Record(Make(
                finished.Outcome ?? "failed",
                problems: finished.Problems.Concat(warnings).ToList(),
                hooks: allHooks,
                failed: finished.Failed,
                details: details));
        }

        return Record(Make("passed", problems: warnings, hooks: allHooks, details: details));
    }

    private async Task<(List<HookRecord> Records, (HookDefinition Hook, string? Problem, string OnFailure)? StoppedBy)> RunHooksAsync(
        List<HookDefinition> hooks,
        EventContext ctx,
        ScriptFolders scripts,
        Dictionary<string, object?> details,
        Action<StreamProgressEvent>? progress,
        HookJudge? judge,
        CheckLog? log)
    {
        var records = new List<HookRecord>();
        foreach (var hook in hooks)
        {
            var isCheck = CheckLog.IsCheck(ctx.Event, hook);
            progress?.Invoke(new StreamProgressEvent("hook-start", hook.Id, hook.When, null, isCheck ? true : null));
            var record = isCheck && log is not null
                ? await log.LoggedAsync(hook.Id, "head", hook.Timeout, true, () => RunHookAsync(_processRunner, hook, ctx, scripts, details)).ConfigureAwait(false)
                : await RunHookAsync(_processRunner, hook, ctx, scripts, details).ConfigureAwait(false);
            records.Add(record);
            progress?.Invoke(new StreamProgressEvent("hook-end", hook.Id, hook.When, record.Outcome, isCheck ? true : null));

            if (!hook.Blocking)
            {
                continue;
            }

            var verdict = judge is not null ? judge(hook, record) : new HookVerdict(record.Outcome == "passed");
            var blocks = record.Outcome != "passed" && !verdict.Allow;
            if (isCheck)
            {
                log?.End(hook.Id, "head", record, blocks);
            }

            if (blocks)
            {
                return (records, (hook, verdict.Problem, verdict.OnFailure ?? hook.OnFailure));
            }
        }

        return (records, null);
    }

    public static async Task<HookRecord> RunHookAsync(
        HookProcessRunner runner,
        HookDefinition hook,
        EventContext ctx,
        ScriptFolders scripts,
        Dictionary<string, object?> details,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var tempFolder = Path.Combine(Path.GetTempPath(), $"hoshin-ids-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempFolder);
        try
        {
            var failuresFile = Path.Combine(tempFolder, "failures.txt");
            var own = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOSHIN_HOOKS_VERSION"] = "1",
                ["HOSHIN_EVENT"] = ctx.Event,
                ["HOSHIN_WHEN"] = hook.When,
                ["HOSHIN_HOOK"] = hook.Id,
                ["HOSHIN_SYSTEM"] = ctx.System,
                ["HOSHIN_CHANGE"] = ctx.Change,
                ["HOSHIN_REPO"] = ctx.Repo,
                ["HOSHIN_REPO_PATH"] = ctx.RepoPath,
                ["HOSHIN_ROOT"] = ctx.Root,
                ["HOSHIN_MASTER"] = ctx.Master,
                ["HOSHIN_BRANCH"] = ctx.Branch ?? "",
                ["HOSHIN_BASE"] = ctx.Base ?? "",
                ["HOSHIN_HEAD"] = ctx.Head ?? "",
                ["HOSHIN_FAILURES"] = failuresFile,
            };

            if (extraEnv is not null)
            {
                foreach (var (k, v) in extraEnv)
                {
                    own[k] = v;
                }
            }

            var stdinObj = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in own)
            {
                stdinObj[k] = v;
            }

            stdinObj["details"] = details;
            var input = JsonSerializer.Serialize(stdinObj, CompactJson);

            var envWithAliases = WithLegacyNames(own);
            var argv = hook.Run
                .Select(a => a
                    .Replace("${hooks}", scripts.FolderOf(hook.Layer), StringComparison.Ordinal)
                    .Replace("${repo}", ctx.RepoPath, StringComparison.Ordinal)
                    .Replace("${master}", ctx.Master, StringComparison.Ordinal))
                .ToList();

            var res = await runner.RunAsync(argv, ctx.RepoPath, envWithAliases, input, hook.Timeout, failuresFile).ConfigureAwait(false);
            return new HookRecord(hook.Id, hook.When, res.Outcome, res.ExitCode, res.Output, res.Ids);
        }
        finally
        {
            try
            {
                Directory.Delete(tempFolder, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    public static Dictionary<string, string> WithLegacyNames(IReadOnlyDictionary<string, string> env)
    {
        var result = new Dictionary<string, string>(env, StringComparer.Ordinal);
        foreach (var (k, v) in env)
        {
            if (k.StartsWith("HOSHIN_", StringComparison.Ordinal))
            {
                result["AI_SDLC_" + k["HOSHIN_".Length..]] = v;
            }
        }

        return result;
    }

    private static void MergeDetails(Dictionary<string, object?> target, Dictionary<string, object?>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (k, v) in source)
        {
            target[k] = v;
        }
    }
}
