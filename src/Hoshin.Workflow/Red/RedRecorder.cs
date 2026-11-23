using System.Text.RegularExpressions;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Workflow.Red;

public sealed record RedRecordResult(RedRecord? Record, List<string> Problems);

/// <summary>Runs a plan task's compile &amp; test commands at its clean test commit in HEAD and records the observed red verdict.</summary>
public sealed class RedRecorder(
    IGitClient git,
    PlanReader planReader,
    AppService apps,
    EventContextFactory contextFactory,
    LedgerStore ledgerStore,
    HookProcessRunner processRunner,
    TimeProvider timeProvider)
{
    private const int DefaultTimeoutSeconds = 600;
    private static readonly Regex PositiveInt = new(@"^[1-9]\d*$", RegexOptions.Compiled);

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly PlanReader _plan = planReader ?? throw new ArgumentNullException(nameof(planReader));
    private readonly AppService _apps = apps ?? throw new ArgumentNullException(nameof(apps));
    private readonly EventContextFactory _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    private readonly LedgerStore _ledger = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
    private readonly HookProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    private readonly TimeProvider _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public List<string> TaskCommits(EventContext ctx, string task)
    {
        if (ctx.Base is null)
        {
            return [];
        }

        var baseRev = _git.BaseRevision(ctx.Root, ctx.Base);
        if (baseRev is null || _git.CommitOf(ctx.Root, "HEAD") is null)
        {
            return [];
        }

        var from = _git.MergeBase(ctx.Root, baseRev, "HEAD");
        if (from is null)
        {
            return [];
        }

        var folder = _apps.IsSingleRepo(ctx.Master)
            ? _apps.AppFolders(ctx.Master, ctx.System).GetValueOrDefault(ctx.Repo)
            : null;
        var pathspec = ctx.Repo == "master" || folder is null ? null : _apps.AppPathspec(folder);
        return _git.CommitsWithTrailer(ctx.Root, from, "HEAD", "Plan-Task", pathspec)
            .Where(c => c.Values.Contains(task))
            .Select(c => c.Sha)
            .ToList();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    public async Task<RedRecordResult> RecordAsync(EventContext ctx, string task)
    {
        var planPath = Path.GetRelativePath(ctx.Master, Path.Combine(ctx.Folder, "plan.md")).Replace('\\', '/');
        var text = _git.ShowFile(ctx.Master, "HEAD", planPath);
        ParsedTask? entry = null;
        if (text is not null)
        {
            try
            {
                entry = _plan.Read(text, planPath).Document.Tasks.FirstOrDefault(t => t.Id == task);
            }
            catch
            {
                entry = null;
            }
        }

        if (entry is null)
        {
            return Refuse($"{task} is not a task of the plan committed in HEAD of the master repository");
        }

        if (entry.Test is null)
        {
            return Refuse($"{task} has no test command in the accepted plan; the plan skill names it in the task's test field");
        }

        var head = _git.CommitOf(ctx.Root, "HEAD");
        var commits = TaskCommits(ctx, task);
        var shortHead = (head ?? "")[..Math.Min(7, (head ?? "").Length)];
        if (head is null || !commits.Contains(head))
        {
            return Refuse($"HEAD ({shortHead}) is not a commit of {task}: red is recorded on the commit that adds {task}'s failing test");
        }

        var type = TypeOf(ctx.Root, head);
        if (type != "test")
        {
            return Refuse($"HEAD ({shortHead}) is a {type} commit: red is recorded on the commit that adds {task}'s failing test, before any production code");
        }

        var production = commits.Take(commits.Count - 1).FirstOrDefault(sha => TypeOf(ctx.Root, sha) != "test");
        if (production is not null)
        {
            return Refuse($"{task} already has production commits before HEAD ({production[..Math.Min(7, production.Length)]}): red is recorded only before the task's first production commit");
        }

        var uncommitted = _contextFactory.UncommittedOf(ctx);
        if (uncommitted.Count > 0)
        {
            return Refuse($"{ctx.Repo} has uncommitted files: {string.Join(", ", uncommitted)}; red is recorded on a clean test commit");
        }

        var timeout = TimeoutSeconds();
        var (ran, outcome) = await RunAndClassifyAsync(entry, ctx.RepoPath, timeout).ConfigureAwait(false);

        var record = new RedRecord(task, head, entry.Compile, entry.Test, outcome, ran.ExitCode, ran.Output, _time.GetUtcNow().ToString("O"));
        var folder = _ledger.LedgerFolder(ctx.Master, ctx.System, ctx.Change);
        _ledger.Update(
            folder,
            ctx.Repo,
            () => LedgerStore.Empty(ctx.System, ctx.Change, ctx.Repo, ctx.RepoPath),
            ledger =>
            {
                var updatedReds = new List<RedRecord>(ledger.Reds ?? []) { record };
                return ledger with { Reds = updatedReds };
            });
        return new RedRecordResult(record, []);
    }

    private async Task<(HookProcessResult Result, string Outcome)> RunAndClassifyAsync(ParsedTask entry, string repoPath, int timeout)
    {
        if (entry.Compile is not null)
        {
            var compile = await _processRunner.RunAsync(entry.Compile, repoPath, null, "", timeout).ConfigureAwait(false);
            if (compile.Outcome != "passed")
            {
                if (compile.Outcome == "could-not-run")
                {
                    return (compile, "could-not-run");
                }

                var classifiedCompile = RedClassifier.Classify(ToCommandResult(compile));
                var compileOutcome = classifiedCompile.Kind == RedKind.MissingSymbol
                    ? RedClassifier.ToRedRecordOutcome(RedKind.MissingSymbol)
                    : "could-not-compile";
                return (compile, compileOutcome);
            }
        }

        var testRun = await _processRunner.RunAsync(entry.Test!, repoPath, null, "", timeout).ConfigureAwait(false);
        var classifiedTest = RedClassifier.Classify(ToCommandResult(testRun));
        return (testRun, RedClassifier.ToRedRecordOutcome(classifiedTest.Kind));
    }

    private static AllowListedCommandResult ToCommandResult(HookProcessResult run)
    {
        var timedOut = run.Outcome == "could-not-run" && run.ExitCode is null &&
            run.Output.Contains("ran past its time limit", StringComparison.OrdinalIgnoreCase);
        var exitCode = run.ExitCode ?? (run.Outcome == "passed" ? 0 : 127);
        return new AllowListedCommandResult(exitCode, run.Output, timedOut, TimeSpan.Zero);
    }

    private string TypeOf(string root, string sha)
    {
        var subject = _git.Out(root, ["log", "-1", "--format=%s", sha]) ?? "";
        var idx = subject.IndexOfAny(['(', ':']);
        return (idx >= 0 ? subject[..idx] : subject).Trim();
    }

    private static int TimeoutSeconds()
    {
        var text = Environment.GetEnvironmentVariable("HOSHIN_RED_TIMEOUT") ?? "";
        return PositiveInt.IsMatch(text) && int.TryParse(text, out var seconds) ? seconds : DefaultTimeoutSeconds;
    }

    private static RedRecordResult Refuse(string problem) => new(null, [problem]);
}
