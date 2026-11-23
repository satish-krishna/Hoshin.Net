using System.Text.Json;
using System.Text.Json.Serialization;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Baseline;

public sealed record BaselineFault(string Id, string Output);

public sealed record TakenBaseline(
    string? Base,
    BaselineEntry? Entry,
    bool Reused,
    string Outcome,
    List<string> Problems,
    BaselineFault? Fault = null);

/// <summary>Takes, caches, and restores baselines for a repository's blocking <c>build.finish</c> before hooks.</summary>
public sealed class BaselineTaker(
    IGitClient git,
    BaselineStore store,
    AppService apps,
    EventContextFactory contextFactory,
    HookScriptExporter scriptExporter,
    HookProcessRunner processRunner,
    TimeProvider timeProvider)
{
    private sealed record RestoreInfo(
        [property: JsonPropertyName("branch")] string Branch,
        [property: JsonPropertyName("head")] string Head,
        [property: JsonPropertyName("folder"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Folder = null);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly BaselineStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    private readonly EventContextFactory _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly HookScriptExporter _scripts = scriptExporter ?? throw new ArgumentNullException(nameof(scriptExporter));
    private readonly HookProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public static List<HookDefinition> ChecksOf(HookConfig config, string repo) =>
        HookConfigService.HooksFor(config, "build.finish", "before", repo).Where(h => h.Blocking).ToList();

    public string SignatureOf(HookConfig config, string master, string system, IReadOnlyList<HookDefinition> checks)
    {
        string? Tree(string path) => config.Revision is null ? null : _git.Out(master, ["rev-parse", $"{config.Revision}:{path}"]);
        var payload = new
        {
            checks = checks.Select(c => new { id = c.Id, run = c.Run, timeout = c.Timeout }).ToList(),
            trees = new[] { Tree("intent/hooks"), Tree($"intent/{system}/hooks") },
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public string? BaseCommitOf(EventContext ctx)
    {
        if (ctx.Base is null)
        {
            return null;
        }

        var revision = _git.BaseRevision(ctx.Root, ctx.Base);
        return revision is null ? null : _git.MergeBase(ctx.Root, "HEAD", revision);
    }

    public TakenBaseline Stored(EventContext ctx, HookConfig config)
    {
        var checks = ChecksOf(config, ctx.Repo);
        if (ctx.Repo == "master" || checks.Count == 0)
        {
            return new TakenBaseline(null, null, false, "passed", []);
        }

        var baseCommit = BaseCommitOf(ctx);
        if (baseCommit is null)
        {
            return new TakenBaseline(null, null, false, "failed", [$"{ctx.Repo} has no base branch on this machine, so its checks have no baseline"]);
        }

        var entry = _store.FindEntry(ctx.Master, ctx.System, ctx.Repo, baseCommit, SignatureOf(config, ctx.Master, ctx.System, checks));
        return new TakenBaseline(baseCommit, entry, entry is not null, "passed", []);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public async Task<TakenBaseline> TakeAsync(EventContext ctx, HookConfig config, bool retake = false)
    {
        var stored = Stored(ctx, config);
        if (stored.Base is null)
        {
            return stored;
        }

        var baseCommit = stored.Base;
        var checks = ChecksOf(config, ctx.Repo);
        var signature = SignatureOf(config, ctx.Master, ctx.System, checks);
        if (retake)
        {
            _store.DropEntry(ctx.Master, ctx.System, ctx.Repo, baseCommit, signature);
        }
        else if (stored.Entry is not null)
        {
            return stored;
        }

        TakenBaseline None(string outcome, List<string> problems, BaselineFault? fault = null) =>
            new(baseCommit, null, false, outcome, problems, fault);

        var dirty = _contextFactory.UncommittedOf(ctx);
        if (dirty.Count > 0)
        {
            return None("failed", [$"{ctx.Repo} has uncommitted files, so the baseline of its checks cannot be taken; commit, ignore or remove them: {string.Join(", ", dirty)}"]);
        }

        var head = _git.CommitOf(ctx.Root, "HEAD");
        var atBase = head == baseCommit;
        if (!atBase && (ctx.Branch is null || head is null))
        {
            return None("failed", [$"{ctx.Repo} is on a detached HEAD, so the baseline of its checks cannot be taken"]);
        }

        var folder = ctx.Repo == "master" || !_apps.IsSingleRepo(ctx.Master)
            ? null
            : _apps.AppFolders(ctx.Master, ctx.System).GetValueOrDefault(ctx.Repo);

        if (!atBase && folder is not null)
        {
            var tracked = _git.TrackedChanges(ctx.Root);
            if (tracked.Count > 0)
            {
                return None("failed", [$"{ctx.Root} has uncommitted changes to tracked files, so the base commit cannot be checked out for the baseline of {ctx.Repo}'s checks; commit or put them away: {string.Join(", ", tracked)}"]);
            }
        }

        using var scripts = _scripts.Export(ctx.Master, config.Revision, ctx.System);
        var records = new List<HookRecord>();
        string? restored = null;
        var detached = false;
        try
        {
            if (!atBase)
            {
                WriteRestore(ctx.Root, new RestoreInfo(ctx.Branch!, head!, folder));
                var checkout = _git.Run(ctx.Root, ["checkout", "--quiet", "--detach", baseCommit]);
                if (!checkout.Ok)
                {
                    DeleteRestoreFile(ctx.Root);
                    return None("failed", [$"git could not check out the base commit {baseCommit[..Math.Min(7, baseCommit.Length)]} of {ctx.Repo}: {checkout.Stderr.Trim()}"]);
                }

                detached = true;
            }

            var asFinish = ctx.CloneFor("build.finish", ctx.Repo, ctx.RepoPath, ctx.Root, atBase ? ctx.Branch : null, ctx.Base, baseCommit);
            var log = new CheckLog(CheckLog.ChecksFile(_git, asFinish.Master, asFinish.System, asFinish.Change), asFinish.Event, asFinish.Repo, ctx.CheckProblems ?? [], _time);
            var extraEnv = new Dictionary<string, string>(StringComparer.Ordinal) { ["HOSHIN_BASELINE"] = "1" };

            foreach (var check in checks)
            {
                var record = await log.LoggedAsync(
                    check.Id,
                    "baseline",
                    check.Timeout,
                    null,
                    () => EventRunner.RunHookAsync(_processRunner, check, asFinish, scripts, new Dictionary<string, object?>(), extraEnv)).ConfigureAwait(false);
                records.Add(record);
                log.End(check.Id, "baseline", record, null);
            }
        }
        finally
        {
            if (detached)
            {
                restored = Restore(ctx.Root, new RestoreInfo(ctx.Branch!, head!, folder));
            }
        }

        if (restored is not null)
        {
            return None("could-not-run", [restored]);
        }

        var fault = records.FirstOrDefault(r => r.Outcome == "could-not-run");
        if (fault is not null)
        {
            return None(
                "could-not-run",
                [$"check {fault.Id} could not run on the base commit {baseCommit[..Math.Min(7, baseCommit.Length)]}, so no baseline was stored"],
                new BaselineFault(fault.Id, fault.Output));
        }

        var hooks = records
            .Select(r => new BaselineHookRecord(r.Id, r.Outcome == "passed" ? "passed" : "failed", r.ExitCode, r.Ids, r.Output))
            .ToList();
        var entry = new BaselineEntry(baseCommit, signature, _time.GetUtcNow().ToString("O"), hooks);
        _store.SaveEntry(ctx.Master, ctx.System, ctx.Repo, entry);
        return new TakenBaseline(baseCommit, entry, false, "passed", []);
    }

    public string? RecoverInterrupted(string root)
    {
        var file = RestoreFile(root);
        if (!File.Exists(file))
        {
            return null;
        }

        RestoreInfo? where;
        try
        {
            where = JsonSerializer.Deserialize<RestoreInfo>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            DeleteRestoreFile(root);
            return null;
        }

        if (where is null || _git.CurrentBranch(root) == where.Branch)
        {
            DeleteRestoreFile(root);
            return null;
        }

        return Restore(root, where);
    }

    private string RestoreFile(string root) => _git.GitPath(root, Path.Combine("hoshin", "baseline-restore.json"));

    private void WriteRestore(string root, RestoreInfo where)
    {
        var file = RestoreFile(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(where, JsonOptions) + "\n");
    }

    private void DeleteRestoreFile(string root)
    {
        var file = RestoreFile(root);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string? Restore(string root, RestoreInfo where)
    {
        List<string> Dirty() => where.Folder is null
            ? OwnFiles.UncommittedFiles(_git, root)
            : _apps.UncommittedInApp(root, where.Folder).Concat(_git.TrackedChanges(root)).Distinct(StringComparer.Ordinal).ToList();

        if (!_git.Run(root, ["checkout", "--quiet", where.Branch]).Ok)
        {
            _git.Run(root, ["checkout", "--quiet", "--force", where.Branch]);
        }

        if (_git.CurrentBranch(root) == where.Branch)
        {
            var cleanArgs = new List<string> { "clean", "-fdq" };
            if (where.Folder is not null)
            {
                cleanArgs.Add("--");
                cleanArgs.AddRange(_apps.AppPathspec(where.Folder));
            }

            _git.Run(root, cleanArgs);
        }

        var now = _git.CurrentBranch(root);
        var currentHead = _git.CommitOf(root, "HEAD");
        var files = Dirty();
        var back = now == where.Branch && currentHead == where.Head && files.Count == 0;
        if (!back)
        {
            var shortHead = where.Head[..Math.Min(7, where.Head.Length)];
            var lead = $"{root} could not be put back on {where.Branch} at {shortHead} after its baseline: it is on {now ?? "a detached HEAD"} with {(files.Count > 0 ? string.Join(", ", files) : "no uncommitted files")}.";
            var advice = now == where.Branch && currentHead == where.Head
                ? "The checks changed tracked files there; restore or discard them (git restore) before going on"
                : $"Check out {where.Branch} at {shortHead} there before going on";
            return $"{lead} {advice}; nothing was stored.";
        }

        DeleteRestoreFile(root);
        return null;
    }
}
