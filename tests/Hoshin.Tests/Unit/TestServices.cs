using Hoshin.Cli;
using Hoshin.Cli.Commands;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;

namespace Hoshin.Tests.Unit;

/// <summary>Shared test composition helper built on <see cref="HoshinServices"/> so tests stay DRY without production defaults.</summary>
internal static class TestServices
{
    public static HoshinServices Default { get; } = new();

    public static TimeProvider Time => Default.TimeProvider;
    public static IFileSystem FileSystem => Default.FileSystem;
    public static IJsonSchemaValidator SchemaValidator => Default.SchemaValidator;
    public static ICommandRunner CommandRunner => Default.CommandRunner;
    public static IGitClient Git => Default.Git;
    public static IPullRequestClient PullRequests => Default.PullRequests;
    public static IAllowListedCommandRunner AllowListedRunner => Default.AllowListedRunner;
    public static MarkerReader MarkerReader => Default.MarkerReader;
    public static ChangeRecordStore ChangeRecords => Default.ChangeRecords;
    public static FrontMatterEdit FrontMatterEdit => Default.FrontMatterEdit;
    public static CrossChecker CrossChecker => Default.CrossChecker;
    public static ScopeRules ScopeRules => Default.ScopeRules;
    public static IntentReader IntentReader => Default.IntentReader;
    public static SpecReader SpecReader => Default.SpecReader;
    public static PlanReader PlanReader => Default.PlanReader;
    public static BuildLogReader BuildLogReader => Default.BuildLogReader;
    public static ReviewReader ReviewReader => Default.ReviewReader;
    public static IDocumentReader DocumentReader => Default.DocumentReader;
    public static IWorkspaceResolver Resolver => Default.Resolver;
    public static AppService Apps => Default.Apps;
    public static LedgerStore Ledger => Default.Ledger;
    public static IWorkflowRunner WorkflowRunner => Default.WorkflowRunner;
    public static WorkflowStateReader WorkflowStateReader => Default.WorkflowStateReader;
    public static HarnessAdapter HookAdapter => Default.HookAdapter;
    public static HookOrchestrator HookOrchestrator => Default.HookOrchestrator;

    public static WorkflowRepo? FindRepo(string directory, string? explicitChangeFolder = null, IGitClient? git = null)
    {
        var g = git ?? Git;
        var ledger = git is null ? Ledger : new LedgerStore(g, FileSystem);
        return WorkflowRepo.Find(directory, g, g, g, FileSystem, ledger, explicitChangeFolder);
    }

    public static WorkflowState? ReadState(WorkflowRepo repo, string? skillsRoot = null) =>
        WorkflowStateReader.Read(repo, skillsRoot);

    public static WorkflowPlan? ReadPlan(WorkflowRepo repo) =>
        repo.ReadPlan(PlanReader);

    public static WorkflowRunner CreateWorkflowRunner(
        IAllowListedCommandRunner? commandRunner = null,
        IPullRequestClient? pullRequests = null,
        IGitClient? git = null,
        TimeProvider? timeProvider = null) =>
        new(
            git ?? Git,
            commandRunner ?? AllowListedRunner,
            pullRequests ?? PullRequests,
            IntentReader,
            SpecReader,
            PlanReader,
            ReviewReader,
            FrontMatterEdit,
            CrossChecker,
            Default.WorkflowEventBridge,
            Default.TaskRepoResolver,
            FileSystem,
            timeProvider ?? Time);

    public static HookOrchestrator CreateOrchestrator(
        IHookAdapter? adapter = null,
        IWorkflowRunner? runner = null,
        IGitClient? git = null,
        TimeProvider? timeProvider = null)
    {
        var r = runner ?? WorkflowRunner;
        var g = git ?? Git;
        var ledger = git is null ? Ledger : new LedgerStore(g, FileSystem);
        return new(
            adapter ?? HookAdapter,
            r,
            r,
            r,
            r,
            r,
            g,
            g,
            g,
            FileSystem,
            ledger,
            WorkflowStateReader,
            Default.WorkflowEventBridge,
            timeProvider ?? Time);
    }

    public static (WorkflowRepo? Repo, string? Reason) EvaluateGuard(
        NormalizedHookEvent ev,
        IGitClient? git = null,
        TimeProvider? timeProvider = null)
    {
        var g = git ?? Git;
        var ledger = git is null ? Ledger : new LedgerStore(g, FileSystem);
        return GuardCommand.Evaluate(ev, g, g, g, FileSystem, ledger, timeProvider ?? Time);
    }
}
