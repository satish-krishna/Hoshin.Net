using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Driver;

/// <summary>Options for <see cref="HoshinDriver"/>.</summary>
/// <param name="RolesRoot">A folder holding <c>&lt;role&gt;/procedure.md</c>; null uses the procedures embedded in the library.</param>
/// <param name="SessionId">The id the driver records as the owner of the steps it issues.</param>
public sealed record HoshinDriverOptions(string? RolesRoot = null, string SessionId = "hoshin-driver");

/// <summary>The harness-neutral driver: see <see cref="IHoshinDriver"/>.</summary>
public sealed class HoshinDriver(
    IChangeStarter changeStarter,
    StepAdvancer advancer,
    WorkSubmitter submitter,
    DecisionApplier decisions,
    IGitClient git,
    LedgerStore ledgerStore,
    IFileSystem fileSystem,
    HoshinDriverOptions options) : IHoshinDriver
{
    private readonly IChangeStarter _changes = changeStarter ?? throw new ArgumentNullException(nameof(changeStarter));
    private readonly StepAdvancer _advancer = advancer ?? throw new ArgumentNullException(nameof(advancer));
    private readonly WorkSubmitter _submitter = submitter ?? throw new ArgumentNullException(nameof(submitter));
    private readonly DecisionApplier _decisions = decisions ?? throw new ArgumentNullException(nameof(decisions));
    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly LedgerStore _ledgers = ledgerStore ?? throw new ArgumentNullException(nameof(ledgerStore));
    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly HoshinDriverOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public Task<IReadOnlyList<ChangeRef>> ListOpenAsync(WorkspaceRef workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        cancellationToken.ThrowIfCancellationRequested();
        var repo = Find(workspace.Path, null)
            ?? throw new CouldNotRunException($"{workspace.Path} is not inside a git repository.");
        IReadOnlyList<ChangeRef> open = _changes.ListOpenChanges(repo)
            .Select(o => new ChangeRef(o.MasterRoot, o.ChangeFolder, o.Change))
            .ToList();
        return Task.FromResult(open);
    }

    public Task<ChangeRef> StartAsync(StartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var skill = request.Track == Track.Fix ? "fix" : "intent";
        var started = _changes.StartFromHook(request.Workspace.Path, skill, request.Name);
        if (!started.Ok)
        {
            throw new DriverRefusedException(started.Error ?? $"The change `{request.Name}` could not be started.");
        }

        var change = started.ChangeName ?? Path.GetFileName(started.ChangeFolder!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return Task.FromResult(new ChangeRef(started.CopyRoot!, started.ChangeFolder!, change));
    }

    public Task<Turn> NextAsync(ChangeRef change, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repo = Open(change);
        var before = Snapshot(repo);
        var step = _advancer.Advance(repo, _options.RolesRoot, _options.SessionId, InvokedSkill(repo));
        return Task.FromResult(TurnFor(repo, step, [], Snapshot(repo).Since(before)));
    }

    public Task<Turn> SubmitAsync(ChangeRef change, WorkId work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        var repo = Open(change);
        var current = repo.ReadStep();
        if (current is null || TurnMapper.IdOf(current) != work)
        {
            throw new DriverRefusedException($"The work {work.Kind} {work.Task} (attempt {work.Attempt}) is not the change's current work. Call NextAsync for the current work.");
        }

        var before = Snapshot(repo);
        var outcome = Submit(repo, current);
        var (next, feedback) = outcome switch
        {
            SubmitCommitted c => (c.Next, (IReadOnlyList<string>)[]),
            SubmitJudged j => (j.Next, j.Next is Retry ? [] : j.Verdict.Passed ? [] : j.Verdict.Reasons),
            SubmitRefused r => (Reissue(repo), r.Problems.Select(p => $"{p.File} line {p.Line}: {p.Message}").ToList()),
            _ => (Reissue(repo), ["Nothing was written: the document is missing or unchanged since its last commit."]),
        };
        var happened = Snapshot(repo).Since(before);
        if (outcome is SubmitCommitted committed)
        {
            happened = [new Happened("committed", $"{committed.What} committed as {WorkflowEngine.Short(committed.Commit)}"), .. happened];
        }

        return Task.FromResult(TurnFor(repo, next, feedback, happened));
    }

    public Task<Turn> DecideAsync(ChangeRef change, Decision decision, DecidedBy by, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(by);
        cancellationToken.ThrowIfCancellationRequested();
        var repo = Open(change);
        var state = ReadState(repo);
        if (DecisionApplier.Refusal(state, decision.Kind) is { } refusal)
        {
            throw new DriverRefusedException(refusal);
        }

        var note = $"approved by {by.Who} via {by.Channel}";
        var outcome = decision switch
        {
            Accept => _decisions.Accept(repo, state, note),
            Approve a => _decisions.Approve(repo, state, string.IsNullOrWhiteSpace(a.Note) ? note : $"{note}: {a.Note}"),
            Decline d => _decisions.Decline(repo, state, d.Reason),
            Reject => _decisions.Reject(repo, state),
            Reopen r => _decisions.Reopen(repo, state, r.Document),
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unknown decision."),
        };
        if (!outcome.Applied)
        {
            throw new DriverRefusedException(outcome.Summary);
        }

        var before = Snapshot(repo);
        var step = _advancer.Advance(repo, _options.RolesRoot, _options.SessionId);
        Happened[] happened = [new("decided", outcome.Summary), .. Snapshot(repo).Since(before)];
        return Task.FromResult(TurnFor(repo, step, [], happened));
    }

    private SubmitOutcome Submit(WorkflowRepo repo, CurrentStep current) => current.Kind switch
    {
        StepKind.WriteDocument when Enum.TryParse<WorkflowDocument>(current.Task, ignoreCase: true, out var document) =>
            _submitter.SubmitDocument(repo, _options.RolesRoot, document, _options.SessionId),
        StepKind.Review => _submitter.SubmitReview(repo, _options.RolesRoot, _options.SessionId),
        StepKind.Fix => _submitter.SubmitFix(repo, _options.RolesRoot, _options.SessionId),
        StepKind.Implement or StepKind.Retry when current.Task is not null =>
            _submitter.SubmitTask(repo, _options.RolesRoot, current, _options.SessionId),
        _ => throw new DriverRefusedException($"The current step ({current.Kind}) is not agent work."),
    };

    private Step Reissue(WorkflowRepo repo) => _advancer.Advance(repo, _options.RolesRoot, _options.SessionId);

    private Turn TurnFor(WorkflowRepo repo, Step step, IReadOnlyList<string> feedback, IReadOnlyList<Happened> happened) =>
        TurnMapper.ToTurn(step, ReadState(repo), repo.ReadStep(), _options.RolesRoot, feedback, happened);

    private string? InvokedSkill(WorkflowRepo repo)
    {
        var state = ReadState(repo);
        return state.ChangeState?.Track == "fix" && !state.Intent.Exists && !state.Plan.HasPlan ? "fix" : null;
    }

    private WorkflowState ReadState(WorkflowRepo repo) =>
        _advancer.ReadState(repo, _options.RolesRoot, out var error)
            ?? throw new CouldNotRunException(error ?? $"Could not read workflow state for {repo.Root} ({repo.ChangeFolder}).");

    private WorkflowRepo Open(ChangeRef change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Find(change.CheckoutRoot, change.ChangeFolder)
            ?? throw new CouldNotRunException($"The change `{change.Change}` is not in a git checkout at {change.CheckoutRoot}.");
    }

    private WorkflowRepo? Find(string directory, string? changeFolder) =>
        WorkflowRepo.Find(directory, _git, _git, _git, _fs, _ledgers, changeFolder);

    private LedgerSnapshot Snapshot(WorkflowRepo repo)
    {
        var state = _advancer.ReadState(repo, _options.RolesRoot, out _);
        var ledgers = state?.RepoLedgers is { Count: > 0 } byRepo ? byRepo.Values.Append(state.Ledger) : [repo.ReadLedger()];
        return LedgerSnapshot.Of(ledgers);
    }
}
