using System.Text.RegularExpressions;

namespace Hoshin.Workflow.Events.Steps;

/// <summary><c>task.done</c>: fired after each task's build log entry is committed.</summary>
public sealed class TaskDoneStep : IEventStep
{
    private static readonly Regex TaskRegex = new(@"^T\d+$", RegexOptions.Compiled);

    public string EventName => "task.done";
    public bool ReusePassed => true;

    public Task<List<string>> CheckAsync(EventContext ctx) =>
        Task.FromResult(TaskRegex.IsMatch(ctx.Extra.GetValueOrDefault("task", ""))
            ? new List<string>()
            : new List<string> { "task.done needs --task T<n>" });

    public string? Subject(EventContext ctx) => ctx.Extra.GetValueOrDefault("task");
}

/// <summary><c>review.finish</c>: fired after a review is committed.</summary>
public sealed class ReviewFinishStep : IEventStep
{
    public string EventName => "review.finish";
    public bool ReusePassed => true;
}
