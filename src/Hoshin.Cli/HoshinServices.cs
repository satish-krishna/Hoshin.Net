using Hoshin.Workflow.Baseline;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Events.Steps;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Red;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli;

/// <summary>Composition root wiring all SOLID workflow interfaces with their default implementations.</summary>
public sealed class HoshinServices
{
    public IJsonSchemaValidator SchemaValidator { get; }
    public ICommandRunner CommandRunner { get; }
    public IGitClient Git { get; }
    public IPullRequestClient PullRequests { get; }
    public SpecReader SpecReader { get; }
    public PlanReader PlanReader { get; }
    public BuildLogReader BuildLogReader { get; }
    public ReviewReader ReviewReader { get; }
    public IDocumentReader DocumentReader { get; }
    public IWorkspaceResolver Resolver { get; }
    public AppService Apps { get; }
    public BranchChecker BranchChecker { get; }
    public ChangeLocator ChangeLocator { get; }
    public ProgressService Progress { get; }
    public FixStateService FixState { get; }
    public ChangePlacer ChangePlacer { get; }
    public FinishedChecker FinishedChecker { get; }
    public ChangeListService ChangeList { get; }
    public ChangeStateService ChangeState { get; }
    public StaleChecker StaleChecker { get; }
    public DocumentCheckService DocumentChecker { get; }
    public Committer Committer { get; }
    public ChangeRenameService ChangeRename { get; }
    public ChangeRemoveService ChangeRemove { get; }
    public WorkspaceMigrator Migrator { get; }
    public HookConfigService HookConfig { get; }
    public LedgerStore Ledger { get; }
    public HookScriptExporter HookScripts { get; }
    public HookProcessRunner HookProcess { get; }
    public EventContextFactory EventContext { get; }
    public BaselineStore BaselineStore { get; }
    public BaselineApprovals BaselineApprovals { get; }
    public BaselineTaker BaselineTaker { get; }
    public RedRecorder RedRecorder { get; }
    public RedVerdictService RedVerdict { get; }
    public BuildStartStep BuildStartStep { get; }
    public EventRunner EventRunner { get; }

    public HoshinServices(
        IJsonSchemaValidator? schemaValidator = null,
        ICommandRunner? commandRunner = null,
        IGitClient? git = null,
        IPullRequestClient? pullRequests = null)
    {
        SchemaValidator = schemaValidator ?? EmbeddedSchemaValidator.Default;
        CommandRunner = commandRunner ?? ProcessCommandRunner.Default;
        Git = git ?? new ProcessGitClient(CommandRunner);
        PullRequests = pullRequests ?? new GhPullRequestClient(CommandRunner);

        SpecReader = new SpecReader();
        PlanReader = new PlanReader(SchemaValidator);
        BuildLogReader = new BuildLogReader(SchemaValidator);
        ReviewReader = new ReviewReader(SchemaValidator);
        DocumentReader = new DocumentReader(SpecReader, PlanReader, BuildLogReader, ReviewReader);

        Resolver = new WorkspaceResolver(Git);
        Apps = new AppService(Git);
        BranchChecker = new BranchChecker(Git);
        ChangeLocator = new ChangeLocator(Git, Apps);
        Progress = new ProgressService(PlanReader, BuildLogReader);
        FixState = new FixStateService(Git, PlanReader, BuildLogReader);
        ChangePlacer = new ChangePlacer(Git, Resolver, ChangeLocator);
        FinishedChecker = new FinishedChecker(Git);
        ChangeList = new ChangeListService(ChangePlacer, FinishedChecker);
        ChangeState = new ChangeStateService(Git, ChangePlacer, FinishedChecker, FixState, Progress, SpecReader, ReviewReader, Apps);
        StaleChecker = new StaleChecker(Git, Resolver, ChangeLocator, Apps);
        DocumentChecker = new DocumentCheckService(DocumentReader, Git);
        Committer = new Committer(Git, Resolver, ChangeLocator, Apps, DocumentChecker, Progress, ReviewReader);
        ChangeRename = new ChangeRenameService(Git, Apps, BranchChecker);
        ChangeRemove = new ChangeRemoveService(Git, Apps);
        Migrator = new WorkspaceMigrator(Git, Resolver);

        HookConfig = new HookConfigService(Git);
        Ledger = new LedgerStore(Git);
        HookScripts = new HookScriptExporter(Git);
        HookProcess = new HookProcessRunner(CommandRunner);
        EventContext = new EventContextFactory(Git, Resolver, ChangeLocator, Apps);
        BaselineStore = new BaselineStore(Git);
        BaselineApprovals = new BaselineApprovals(Ledger);
        BaselineTaker = new BaselineTaker(Git, BaselineStore, Apps, EventContext, HookScripts, HookProcess);
        RedRecorder = new RedRecorder(Git, PlanReader, Apps, EventContext, Ledger, HookProcess);
        RedVerdict = new RedVerdictService(Ledger, RedRecorder);

        BuildStartStep = new BuildStartStep(Git, Apps, EventContext, Ledger, BaselineTaker, BaselineApprovals);
        var steps = new IEventStep[]
        {
            new ChangeStartStep(Git, Apps, BranchChecker),
            new DocAcceptStep(Git),
            BuildStartStep,
            new TaskDoneStep(),
            new BuildFinishStep(Git, Progress, Apps, EventContext, BaselineTaker, BaselineApprovals),
            new ReviewFinishStep(),
            new ChangeDoneStep(Git, PullRequests, Progress, ReviewReader, Apps),
        };
        EventRunner = new EventRunner(Git, HookConfig, Ledger, HookScripts, HookProcess, steps);
    }
}
