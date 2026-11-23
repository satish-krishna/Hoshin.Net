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
    public static HarnessAdapter HookAdapter => Default.HookAdapter;
    public static HookOrchestrator HookOrchestrator => Default.HookOrchestrator;

    public static WorkflowRepo? FindRepo(string directory, string? explicitChangeFolder = null, IAllowListedCommandRunner? runner = null) =>
        WorkflowRepo.Find(directory, runner ?? AllowListedRunner, FileSystem, explicitChangeFolder);

    public static WorkflowState? ReadState(WorkflowRepo repo, string? skillsRoot = null) =>
        WorkflowState.Read(repo, PlanReader, skillsRoot);

    public static WorkflowPlan? ReadPlan(WorkflowRepo repo) =>
        repo.ReadPlan(PlanReader);

    public static WorkflowRunner CreateWorkflowRunner(
        IAllowListedCommandRunner? commandRunner = null,
        IPullRequestClient? pullRequests = null,
        TimeProvider? timeProvider = null) =>
        new(
            commandRunner ?? AllowListedRunner,
            pullRequests ?? PullRequests,
            IntentReader,
            SpecReader,
            PlanReader,
            ReviewReader,
            FrontMatterEdit,
            CrossChecker,
            FileSystem,
            timeProvider ?? Time);

    public static HookOrchestrator CreateOrchestrator(
        IHookAdapter? adapter = null,
        IWorkflowRunner? runner = null,
        IAllowListedCommandRunner? commandRunner = null,
        TimeProvider? timeProvider = null) =>
        new(
            adapter ?? HookAdapter,
            runner ?? WorkflowRunner,
            commandRunner ?? AllowListedRunner,
            FileSystem,
            PlanReader,
            timeProvider ?? Time);

    public static (WorkflowRepo? Repo, string? Reason) EvaluateGuard(
        NormalizedHookEvent ev,
        IAllowListedCommandRunner? runner = null,
        TimeProvider? timeProvider = null) =>
        GuardCommand.Evaluate(ev, runner ?? AllowListedRunner, FileSystem, timeProvider ?? Time);
}
