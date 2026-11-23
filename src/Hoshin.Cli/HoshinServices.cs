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

/// <summary>Lazy composition root wiring all SOLID workflow interfaces and narrow role abstractions.</summary>
public sealed class HoshinServices
{
    private readonly Lazy<MarkerReader> _markerReader;
    private readonly Lazy<ChangeRecordStore> _changeRecords;
    private readonly Lazy<FrontMatterEdit> _frontMatterEdit;
    private readonly Lazy<CrossChecker> _crossChecker;
    private readonly Lazy<ScopeRules> _scopeRules;
    private readonly Lazy<IntentReader> _intentReader;
    private readonly Lazy<SpecReader> _specReader;
    private readonly Lazy<PlanReader> _planReader;
    private readonly Lazy<BuildLogReader> _buildLogReader;
    private readonly Lazy<ReviewReader> _reviewReader;
    private readonly Lazy<IDocumentReader> _documentReader;
    private readonly Lazy<IWorkspaceResolver> _resolver;
    private readonly Lazy<AppService> _apps;
    private readonly Lazy<BranchChecker> _branchChecker;
    private readonly Lazy<ChangeLocator> _changeLocator;
    private readonly Lazy<ProgressService> _progress;
    private readonly Lazy<FixStateService> _fixState;
    private readonly Lazy<ChangePlacer> _changePlacer;
    private readonly Lazy<FinishedChecker> _finishedChecker;
    private readonly Lazy<ChangeListService> _changeList;
    private readonly Lazy<ChangeStateService> _changeState;
    private readonly Lazy<StaleChecker> _staleChecker;
    private readonly Lazy<DocumentCheckService> _documentChecker;
    private readonly Lazy<Committer> _committer;
    private readonly Lazy<ChangeRenameService> _changeRename;
    private readonly Lazy<ChangeRemoveService> _changeRemove;
    private readonly Lazy<IYamlToJsoncConverter> _yamlConverter;
    private readonly Lazy<IDocumentMigrator> _documentMigrator;
    private readonly Lazy<WorkspaceMigrator> _migrator;
    private readonly Lazy<HookConfigService> _hookConfig;
    private readonly Lazy<LedgerStore> _ledger;
    private readonly Lazy<HookScriptExporter> _hookScripts;
    private readonly Lazy<HookProcessRunner> _hookProcess;
    private readonly Lazy<EventContextFactory> _eventContext;
    private readonly Lazy<BaselineStore> _baselineStore;
    private readonly Lazy<BaselineApprovals> _baselineApprovals;
    private readonly Lazy<BaselineTaker> _baselineTaker;
    private readonly Lazy<RedRecorder> _redRecorder;
    private readonly Lazy<RedVerdictService> _redVerdict;
    private readonly Lazy<BuildStartStep> _buildStartStep;
    private readonly Lazy<EventRunner> _eventRunner;
    private readonly Lazy<IAllowListedCommandRunner> _allowListedRunner;
    private readonly Lazy<BuildLogWriter> _buildLogWriter;
    private readonly Lazy<IWorkflowEventBridge> _workflowEventBridge;
    private readonly Lazy<ITaskRepoResolver> _taskRepoResolver;
    private readonly Lazy<ITaskEvaluator> _taskEvaluator;
    private readonly Lazy<ICheckRunner> _checkRunner;
    private readonly Lazy<IApprovalRecorder> _approvalRecorder;
    private readonly Lazy<IPublisher> _publisher;
    private readonly Lazy<IDocumentCommitter> _documentCommitter;
    private readonly Lazy<IWorkflowRunner> _workflowRunner;
    private readonly Lazy<WorkflowStateReader> _workflowStateReader;
    private readonly Lazy<IChangeStarter> _changeStarter;
    private readonly Lazy<HarnessAdapter> _hookAdapter;
    private readonly Lazy<HookOrchestrator> _hookOrchestrator;

    public TimeProvider TimeProvider { get; }
    public IFileSystem FileSystem { get; }
    public IJsonSchemaValidator SchemaValidator { get; }
    public ICommandRunner CommandRunner { get; }
    public IGitClient Git { get; }
    public IPullRequestClient PullRequests { get; }

    public MarkerReader MarkerReader => _markerReader.Value;
    public ChangeRecordStore ChangeRecords => _changeRecords.Value;
    public FrontMatterEdit FrontMatterEdit => _frontMatterEdit.Value;
    public CrossChecker CrossChecker => _crossChecker.Value;
    public ScopeRules ScopeRules => _scopeRules.Value;
    public IntentReader IntentReader => _intentReader.Value;
    public SpecReader SpecReader => _specReader.Value;
    public PlanReader PlanReader => _planReader.Value;
    public BuildLogReader BuildLogReader => _buildLogReader.Value;
    public ReviewReader ReviewReader => _reviewReader.Value;
    public IDocumentReader DocumentReader => _documentReader.Value;
    public IWorkspaceResolver Resolver => _resolver.Value;
    public AppService Apps => _apps.Value;
    public BranchChecker BranchChecker => _branchChecker.Value;
    public ChangeLocator ChangeLocator => _changeLocator.Value;
    public ProgressService Progress => _progress.Value;
    public FixStateService FixState => _fixState.Value;
    public ChangePlacer ChangePlacer => _changePlacer.Value;
    public FinishedChecker FinishedChecker => _finishedChecker.Value;
    public ChangeListService ChangeList => _changeList.Value;
    public ChangeStateService ChangeState => _changeState.Value;
    public StaleChecker StaleChecker => _staleChecker.Value;
    public DocumentCheckService DocumentChecker => _documentChecker.Value;
    public IDocumentCheckService DocumentCheckService => _documentChecker.Value;
    public Committer Committer => _committer.Value;
    public ChangeRenameService ChangeRename => _changeRename.Value;
    public ChangeRemoveService ChangeRemove => _changeRemove.Value;
    public IYamlToJsoncConverter YamlConverter => _yamlConverter.Value;
    public IDocumentMigrator DocumentMigrator => _documentMigrator.Value;
    public WorkspaceMigrator Migrator => _migrator.Value;
    public HookConfigService HookConfig => _hookConfig.Value;
    public LedgerStore Ledger => _ledger.Value;
    public HookScriptExporter HookScripts => _hookScripts.Value;
    public HookProcessRunner HookProcess => _hookProcess.Value;
    public EventContextFactory EventContext => _eventContext.Value;
    public BaselineStore BaselineStore => _baselineStore.Value;
    public BaselineApprovals BaselineApprovals => _baselineApprovals.Value;
    public BaselineTaker BaselineTaker => _baselineTaker.Value;
    public RedRecorder RedRecorder => _redRecorder.Value;
    public RedVerdictService RedVerdict => _redVerdict.Value;
    public BuildStartStep BuildStartStep => _buildStartStep.Value;
    public EventRunner EventRunner => _eventRunner.Value;
    public IAllowListedCommandRunner AllowListedRunner => _allowListedRunner.Value;
    public BuildLogWriter BuildLogWriter => _buildLogWriter.Value;
    public IWorkflowEventBridge WorkflowEventBridge => _workflowEventBridge.Value;
    public ITaskRepoResolver TaskRepoResolver => _taskRepoResolver.Value;
    public ITaskEvaluator TaskEvaluator => _taskEvaluator.Value;
    public ICheckRunner CheckRunner => _checkRunner.Value;
    public IApprovalRecorder ApprovalRecorder => _approvalRecorder.Value;
    public IPublisher Publisher => _publisher.Value;
    public IDocumentCommitter DocumentCommitter => _documentCommitter.Value;
    public IWorkflowRunner WorkflowRunner => _workflowRunner.Value;
    public WorkflowStateReader WorkflowStateReader => _workflowStateReader.Value;
    public IChangeStarter ChangeStarter => _changeStarter.Value;
    public HarnessAdapter HookAdapter => _hookAdapter.Value;
    public HookOrchestrator HookOrchestrator => _hookOrchestrator.Value;

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

        _markerReader = new(() => new MarkerReader(FileSystem));
        _changeRecords = new(() => new ChangeRecordStore(FileSystem));
        _frontMatterEdit = new(() => new FrontMatterEdit(FileSystem));
        _crossChecker = new(() => new CrossChecker(FileSystem));
        _scopeRules = new(() => new ScopeRules(ChangeRecords, MarkerReader, FileSystem));

        _intentReader = new(() => new IntentReader());
        _specReader = new(() => new SpecReader());
        _planReader = new(() => new PlanReader(SchemaValidator));
        _buildLogReader = new(() => new BuildLogReader(SchemaValidator));
        _reviewReader = new(() => new ReviewReader(SchemaValidator));
        _documentReader = new(() => new DocumentReader(SpecReader, PlanReader, BuildLogReader, ReviewReader));

        _resolver = new(() => new WorkspaceResolver(Git, Git, MarkerReader));
        _apps = new(() => new AppService(Git, MarkerReader));
        _branchChecker = new(() => new BranchChecker(Git));
        _changeLocator = new(() => new ChangeLocator(Git, Apps, MarkerReader));
        _progress = new(() => new ProgressService(PlanReader, BuildLogReader, MarkerReader));
        _fixState = new(() => new FixStateService(Git, PlanReader, BuildLogReader, MarkerReader));
        _changePlacer = new(() => new ChangePlacer(Git, Resolver, ChangeLocator, MarkerReader));
        _finishedChecker = new(() => new FinishedChecker(Git));
        _changeList = new(() => new ChangeListService(ChangePlacer, FinishedChecker));
        _changeState = new(() => new ChangeStateService(Git, ChangePlacer, FinishedChecker, FixState, Progress, SpecReader, ReviewReader, Apps));
        _staleChecker = new(() => new StaleChecker(Git, Resolver, ChangeLocator, Apps));
        _documentChecker = new(() => new DocumentCheckService(DocumentReader, Git, Git, Git, MarkerReader, CrossChecker));
        _committer = new(() => new Committer(Git, Resolver, ChangeLocator, Apps, DocumentChecker, Progress, ReviewReader, MarkerReader, ScopeRules));
        _changeRename = new(() => new ChangeRenameService(Git, Apps, BranchChecker, ChangeRecords));
        _changeRemove = new(() => new ChangeRemoveService(Git, Apps, ChangeRecords));
        _yamlConverter = new(() => new YamlToJsoncConverter());
        _documentMigrator = new(() => new DocumentMigrator(YamlConverter));
        _migrator = new(() => new WorkspaceMigrator(Git, Resolver, DocumentMigrator, MarkerReader, ScopeRules));

        _hookConfig = new(() => new HookConfigService(Git, Git));
        _ledger = new(() => new LedgerStore(Git, FileSystem));
        _hookScripts = new(() => new HookScriptExporter(Git, Git));
        _hookProcess = new(() => new HookProcessRunner(CommandRunner));
        _eventContext = new(() => new EventContextFactory(Git, Resolver, ChangeLocator, Apps));
        _baselineStore = new(() => new BaselineStore(Git));
        _baselineApprovals = new(() => new BaselineApprovals(Ledger, TimeProvider));
        _baselineTaker = new(() => new BaselineTaker(Git, BaselineStore, Apps, EventContext, HookScripts, HookProcess, TimeProvider));
        _redRecorder = new(() => new RedRecorder(Git, PlanReader, Apps, EventContext, Ledger, HookProcess, TimeProvider));
        _redVerdict = new(() => new RedVerdictService(Ledger, RedRecorder));

        _buildStartStep = new(() => new BuildStartStep(Git, Apps, EventContext, Ledger, BaselineTaker, BaselineApprovals, ChangeRecords, ScopeRules));
        _eventRunner = new(() => new EventRunner(
            Git,
            HookConfig,
            Ledger,
            HookScripts,
            HookProcess,
            [
                new ChangeStartStep(Git, Apps, BranchChecker, ChangeRecords),
                new DocAcceptStep(Git),
                BuildStartStep,
                new TaskDoneStep(),
                new BuildFinishStep(Git, Progress, Apps, EventContext, BaselineTaker, BaselineApprovals, ChangeRecords, ScopeRules),
                new ReviewFinishStep(),
                new ChangeDoneStep(Git, PullRequests, Progress, ReviewReader, Apps, ChangeRecords, ScopeRules),
            ],
            TimeProvider));
        _allowListedRunner = new(() => new AllowListedCommandRunner());
        _buildLogWriter = new(() => new BuildLogWriter(FileSystem));
        _workflowEventBridge = new(() => new WorkflowEventBridge(EventContext, EventRunner, FileSystem));
        _taskRepoResolver = new(() => new TaskRepoResolver(Resolver, Apps, Git, FileSystem, Ledger));
        _taskEvaluator = new(() => new TaskEvaluator(Git, Git, Git, Git, AllowListedRunner, BuildLogWriter, WorkflowEventBridge, TaskRepoResolver, TimeProvider));
        _checkRunner = new(() => new CheckRunner(Git, AllowListedRunner, WorkflowEventBridge, TaskRepoResolver, TimeProvider));
        _approvalRecorder = new(() => new ApprovalRecorder(TimeProvider));
        _publisher = new(() => new Publisher(Git, Git, Git, PullRequests, WorkflowEventBridge, TimeProvider));
        _documentCommitter = new(() => new DocumentCommitter(
            Git,
            Git,
            IntentReader,
            SpecReader,
            PlanReader,
            ReviewReader,
            FrontMatterEdit,
            CrossChecker,
            WorkflowEventBridge,
            FileSystem,
            TimeProvider));
        _workflowRunner = new(() => new WorkflowRunner(
            TaskEvaluator,
            CheckRunner,
            ApprovalRecorder,
            Publisher,
            DocumentCommitter));
        _workflowStateReader = new(() => new WorkflowStateReader(PlanReader, ChangeState, TaskRepoResolver, FileSystem));
        _changeStarter = new(() => new ChangeStarter(Resolver, MarkerReader, ChangeLocator, ChangeList, ScopeRules, EventRunner, Git, Git, Git, FileSystem, TimeProvider));
        _hookAdapter = new(() => new HarnessAdapter(TimeProvider));
        _hookOrchestrator = new(() => new HookOrchestrator(
            HookAdapter,
            TaskEvaluator,
            CheckRunner,
            ApprovalRecorder,
            Publisher,
            DocumentCommitter,
            Git,
            Git,
            Git,
            FileSystem,
            Ledger,
            WorkflowStateReader,
            WorkflowEventBridge,
            ChangeStarter,
            TimeProvider));
    }
}
