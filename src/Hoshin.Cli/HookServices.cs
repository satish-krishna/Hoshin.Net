using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Hosting;

namespace Hoshin.Cli;

/// <summary>The hook harness adapters (Claude Code, Antigravity) composed over the library's <see cref="HoshinServices"/>.</summary>
public sealed class HookServices
{
    private readonly Lazy<IHookRunResolver> _hookRunResolver;
    private readonly Lazy<HarnessAdapter> _hookAdapter;
    private readonly Lazy<HookOrchestrator> _hookOrchestrator;

    public HookServices(HoshinServices core)
    {
        Core = core ?? throw new ArgumentNullException(nameof(core));
        _hookRunResolver = new(() => new HookRunResolver(Core.Resolver, Core.MarkerReader, Core.Git, Core.Git, Core.Git, Core.FileSystem, Core.Ledger));
        _hookAdapter = new(() => new HarnessAdapter(Core.TimeProvider));
        _hookOrchestrator = new(() => new HookOrchestrator(
            HookAdapter,
            HookRunResolver,
            Core.TaskEvaluator,
            Core.CheckRunner,
            Core.ApprovalRecorder,
            Core.Publisher,
            Core.DocumentCommitter,
            Core.FileSystem,
            Core.WorkflowStateReader,
            Core.WorkflowEventBridge,
            Core.ChangeStarter,
            Core.TimeProvider));
    }

    public HoshinServices Core { get; }

    public IHookRunResolver HookRunResolver => _hookRunResolver.Value;

    public HarnessAdapter HookAdapter => _hookAdapter.Value;

    public HookOrchestrator HookOrchestrator => _hookOrchestrator.Value;
}
