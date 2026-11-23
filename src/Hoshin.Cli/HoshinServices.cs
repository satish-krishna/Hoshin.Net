using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Baseline;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Events.Steps;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Migration;
using Hoshin.Workflow.Red;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Cli;

/// <summary>Composition root wiring all SOLID workflow interfaces with their implementations.</summary>
public sealed class HoshinServices
{
    public TimeProvider TimeProvider { get; }
    public IFileSystem FileSystem { get; }
    public IJsonSchemaValidator SchemaValidator { get; }
    public ICommandRunner CommandRunner { get; }
    public IGitClient Git { get; }
    public IPullRequestClient PullRequests { get; }
    public MarkerReader MarkerReader { get; }
    public ChangeRecordStore ChangeRecords { get; }
    public FrontMatterEdit FrontMatterEdit { get; }
    public CrossChecker CrossChecker { get; }
    public ScopeRules ScopeRules { get; }
    public IntentReader IntentReader { get; }
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
    public IYamlToJsoncConverter YamlConverter { get; }
    public IDocumentMigrator DocumentMigrator { get; }
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
    public IAllowListedCommandRunner AllowListedRunner { get; }
    public IWorkflowRunner WorkflowRunner { get; }
    public HarnessAdapter HookAdapter { get; }
    public HookOrchestrator HookOrchestrator { get; }

    public HoshinServices()
        : this(new EmbeddedSchemaValidator(), new ProcessCommandRunner(), TimeProvider.System, new PhysicalFileSystem())
    {
    }

    public HoshinServices(
        IJsonSchemaValidator schemaValidator,
        ICommandRunner commandRunner,
        TimeProvider timeProvider,
        IFileSystem fileSystem)
        : this(
            schemaValidator,
            commandRunner,
            new ProcessGitClient(commandRunner),
            new GhPullRequestClient(commandRunner),
            timeProvider,
            fileSystem)
    {
    }

    public HoshinServices(
        IJsonSchemaValidator schemaValidator,
        ICommandRunner commandRunner,
        IGitClient git,
        IPullRequestClient pullRequests,
        TimeProvider timeProvider,
        IFileSystem fileSystem)
    {
        TimeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        FileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        SchemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
        CommandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        Git = git ?? throw new ArgumentNullException(nameof(git));
        PullRequests = pullRequests ?? throw new ArgumentNullException(nameof(pullRequests));

        MarkerReader = new MarkerReader(FileSystem);
        ChangeRecords = new ChangeRecordStore(FileSystem);
        FrontMatterEdit = new FrontMatterEdit(FileSystem);
        CrossChecker = new CrossChecker(FileSystem);
        ScopeRules = new ScopeRules(ChangeRecords, MarkerReader, FileSystem);

        IntentReader = new IntentReader();
        SpecReader = new SpecReader();
        PlanReader = new PlanReader(SchemaValidator);
        BuildLogReader = new BuildLogReader(SchemaValidator);
        ReviewReader = new ReviewReader(SchemaValidator);
        DocumentReader = new DocumentReader(SpecReader, PlanReader, BuildLogReader, ReviewReader);

        Resolver = new WorkspaceResolver(Git, MarkerReader);
        Apps = new AppService(Git, MarkerReader);
        BranchChecker = new BranchChecker(Git);
        ChangeLocator = new ChangeLocator(Git, Apps, MarkerReader);
        Progress = new ProgressService(PlanReader, BuildLogReader, MarkerReader);
        FixState = new FixStateService(Git, PlanReader, BuildLogReader, MarkerReader);
        ChangePlacer = new ChangePlacer(Git, Resolver, ChangeLocator, MarkerReader);
        FinishedChecker = new FinishedChecker(Git);
        ChangeList = new ChangeListService(ChangePlacer, FinishedChecker);
        ChangeState = new ChangeStateService(Git, ChangePlacer, FinishedChecker, FixState, Progress, SpecReader, ReviewReader, Apps);
        StaleChecker = new StaleChecker(Git, Resolver, ChangeLocator, Apps);
        DocumentChecker = new DocumentCheckService(DocumentReader, Git, MarkerReader, CrossChecker);
        Committer = new Committer(Git, Resolver, ChangeLocator, Apps, DocumentChecker, Progress, ReviewReader, MarkerReader, ScopeRules);
        ChangeRename = new ChangeRenameService(Git, Apps, BranchChecker, ChangeRecords);
        ChangeRemove = new ChangeRemoveService(Git, Apps, ChangeRecords);
        YamlConverter = new YamlToJsoncConverter();
        DocumentMigrator = new DocumentMigrator(YamlConverter);
        Migrator = new WorkspaceMigrator(Git, Resolver, DocumentMigrator, MarkerReader, ScopeRules);

        HookConfig = new HookConfigService(Git);
        Ledger = new LedgerStore(Git, FileSystem);
        HookScripts = new HookScriptExporter(Git);
        HookProcess = new HookProcessRunner(CommandRunner);
        EventContext = new EventContextFactory(Git, Resolver, ChangeLocator, Apps);
        BaselineStore = new BaselineStore(Git);
        BaselineApprovals = new BaselineApprovals(Ledger, TimeProvider);
        BaselineTaker = new BaselineTaker(Git, BaselineStore, Apps, EventContext, HookScripts, HookProcess, TimeProvider);
        RedRecorder = new RedRecorder(Git, PlanReader, Apps, EventContext, Ledger, HookProcess, TimeProvider);
        RedVerdict = new RedVerdictService(Ledger, RedRecorder);

        BuildStartStep = new BuildStartStep(Git, Apps, EventContext, Ledger, BaselineTaker, BaselineApprovals, ChangeRecords, ScopeRules);
        var steps = new IEventStep[]
        {
            new ChangeStartStep(Git, Apps, BranchChecker, ChangeRecords),
            new DocAcceptStep(Git),
            BuildStartStep,
            new TaskDoneStep(),
            new BuildFinishStep(Git, Progress, Apps, EventContext, BaselineTaker, BaselineApprovals, ChangeRecords, ScopeRules),
            new ReviewFinishStep(),
            new ChangeDoneStep(Git, PullRequests, Progress, ReviewReader, Apps, ChangeRecords, ScopeRules),
        };
        EventRunner = new EventRunner(Git, HookConfig, Ledger, HookScripts, HookProcess, steps, TimeProvider);
        AllowListedRunner = new AllowListedCommandRunner();
        WorkflowRunner = new WorkflowRunner(
            AllowListedRunner,
            PullRequests,
            IntentReader,
            SpecReader,
            PlanReader,
            ReviewReader,
            FrontMatterEdit,
            CrossChecker,
            FileSystem,
            TimeProvider);
        HookAdapter = new HarnessAdapter(TimeProvider);
        HookOrchestrator = new HookOrchestrator(HookAdapter, WorkflowRunner, AllowListedRunner, FileSystem, PlanReader, TimeProvider);
    }
}
