namespace Hoshin.Workflow.Events.Steps;

public sealed record FailedHookInfo(string Id, string OnFailure, string Output);

public sealed record HookVerdict(bool Allow, string? Problem = null, string? OnFailure = null);

public delegate HookVerdict HookJudge(HookDefinition hook, HookRecord record);

public sealed record StepResult(
    List<string> Problems,
    Dictionary<string, object?>? Details = null,
    FailedHookInfo? Failed = null,
    string? Outcome = null)
{
    public HookJudge? Judge { get; init; }

    public static StepResult Empty { get; } = new([]);
}

/// <summary>Built-in lifecycle step executed by <see cref="EventRunner"/> around before/after hooks.</summary>
public interface IEventStep
{
    string EventName { get; }
    bool ReusePassed { get; }
    Task<List<string>> CheckAsync(EventContext ctx) => Task.FromResult(new List<string>());
    Dictionary<string, object?>? Idle(EventContext ctx) => null;
    Task<StepResult> PrepareAsync(EventContext ctx, HookConfig config) => Task.FromResult(StepResult.Empty);
    Task<StepResult> ActAsync(EventContext ctx) => Task.FromResult(StepResult.Empty);
    Task<StepResult> FinishAsync(EventContext ctx, HookConfig config) => Task.FromResult(StepResult.Empty);
    string Key(EventContext ctx) => ctx.Head ?? "";
    string? Subject(EventContext ctx) => null;
}
