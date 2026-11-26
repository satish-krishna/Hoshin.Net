using System.Text.Json.Serialization;

namespace Hoshin.Workflow.Engine;

/// <summary>A single task in a workflow plan; its optional test command is the verdict (null means no testable behavior).</summary>
public sealed record WorkflowTask(
    string Id,
    string Title,
    string Text,
    string[]? Test,
    string[]? Compile = null,
    List<string>? Files = null,
    int? TimeoutSeconds = null,
    string? Repo = null,
    List<string>? DependsOn = null);

/// <summary>The change's loaded workflow plan, test patterns, and blocking <c>build.finish</c> checks.</summary>
public sealed record WorkflowPlan(
    string Change,
    string Scope,
    string Base,
    IReadOnlyList<WorkflowTask> Tasks,
    IReadOnlyList<string[]> Checks,
    bool HasPlan = true,
    string? Branch = null,
    string WorkType = "feat",
    string[]? TestPatterns = null,
    string[]? EnvAllow = null,
    int TestTimeoutSeconds = 300,
    int CheckTimeoutSeconds = 600,
    int MaxAttempts = WorkflowEngine.MaxAttempts,
    string[]? LinkDirs = null)
{
    public string EffectiveBranch => string.IsNullOrWhiteSpace(Branch) ? $"change/{Change}" : Branch;

    public string[] EffectiveTestPatterns =>
        TestPatterns is { Length: > 0 } ? TestPatterns : DefaultTestPatterns;

    public TimeSpan EffectiveTaskTimeout(WorkflowTask task) =>
        TimeSpan.FromSeconds(task.TimeoutSeconds is > 0 ? task.TimeoutSeconds.Value : (TestTimeoutSeconds > 0 ? TestTimeoutSeconds : 300));

    public TimeSpan EffectiveCheckTimeout =>
        TimeSpan.FromSeconds(CheckTimeoutSeconds > 0 ? CheckTimeoutSeconds : 600);

    public int EffectiveMaxAttempts => MaxAttempts > 0 ? MaxAttempts : WorkflowEngine.MaxAttempts;

    public static readonly string[] DefaultTestPatterns =
        ["test/**", "tests/**", "*.test.*", "*Tests.cs", "*Test.cs"];
}

/// <summary>Status of one change document (<c>intent.md</c>, <c>spec.md</c>, or <c>plan.md</c>) in working tree and HEAD, with optional upstream pin state.</summary>
public sealed record WorkflowDocState(
    string Path,
    bool Exists,
    bool InHead,
    bool Uncommitted,
    string? WorkingStatus,
    string? HeadStatus,
    string? Pin = null);

/// <summary>Work handed to an agent: the role's procedure text and the attempt brief.</summary>
public sealed record AgentTask(string Role, string Task, int Attempt, string Instructions, string Brief, string? TargetRepo = null)
{
    public string Prompt => $"{Instructions.Trim()}\n\n---\n\n{Brief.Trim()}\n";
}

/// <summary>Closed set of authoring documents in the change pipeline.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkflowDocument>))]
public enum WorkflowDocument
{
    Intent,
    Spec,
    Plan,
}

public static class WorkflowDocumentExtensions
{
    public static string ToWireName(this WorkflowDocument document) => document switch
    {
        WorkflowDocument.Intent => "intent",
        WorkflowDocument.Spec => "spec",
        WorkflowDocument.Plan => "plan",
        _ => throw new ArgumentOutOfRangeException(nameof(document), document, "Unknown workflow document."),
    };

    public static string FileName(this WorkflowDocument document) => $"{document.ToWireName()}.md";
}

/// <summary>Closed set of workflow state machine step kinds serialized to <c>.git/hoshin/step.json</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StepKind>))]
public enum StepKind
{
    WriteDocument,
    AwaitAccept,
    AwaitAcceptFix,
    Review,
    Fix,
    Implement,
    Retry,
    RunChecks,
    NeedsApproval,
    Publish,
    Done,
}

/// <summary>The single step <see cref="WorkflowEngine.Next"/> returns. Hosts switch on the concrete type.</summary>
public abstract record Step
{
    public abstract StepKind Kind { get; }
}

public sealed record WriteDocument(WorkflowDocument Document, string Path, string Brief) : Step
{
    public override StepKind Kind => StepKind.WriteDocument;
}

public sealed record AwaitAccept(WorkflowDocument Document, string Path, string Head) : Step
{
    public override StepKind Kind => StepKind.AwaitAccept;
}

/// <summary>Committed light-track fix documents awaiting human acceptance (<c>accept</c> or <c>fix --accept</c>).</summary>
public sealed record AwaitAcceptFix(string ChangeFolder, string Head, WorkflowDocument NextDocument) : Step
{
    public override StepKind Kind => StepKind.AwaitAcceptFix;
}

/// <summary>The built change is being reviewed and findings written to <c>build/review.md</c>.</summary>
public sealed record ReviewDocument(string Path, string Head, string Brief) : Step
{
    public override StepKind Kind => StepKind.Review;
}

/// <summary>Light-track fix drafting (<c>intent.md</c>, <c>spec.md</c>, <c>plan.md</c> together, capped at 1 repo and 3 tasks).</summary>
public sealed record WriteFix(string ChangeFolder, string Brief) : Step
{
    public override StepKind Kind => StepKind.Fix;
}

public sealed record Implement(AgentTask Agent) : Step
{
    public override StepKind Kind => StepKind.Implement;
}

public sealed record Retry(AgentTask Agent, IReadOnlyList<string> Handoff) : Step
{
    public override StepKind Kind => StepKind.Retry;
}

public sealed record RunChecks(string Head) : Step
{
    public override StepKind Kind => StepKind.RunChecks;
}

public sealed record NeedsApproval(string Head, string Reason) : Step
{
    public override StepKind Kind => StepKind.NeedsApproval;
}

public sealed record Publish(string Head, string Ref) : Step
{
    public override StepKind Kind => StepKind.Publish;
}

public sealed record Done(bool Success, string Outcome) : Step
{
    public override StepKind Kind => StepKind.Done;
}

/// <summary>Where an attempt started, so failed attempts stay clean and retries have a known baseline.</summary>
public sealed record WorkflowIssued(
    string Task,
    int Attempt,
    string StartHead,
    DateTimeOffset At,
    bool IndexHadStagedChanges = false,
    Dictionary<string, string>? PreIssuePaths = null);

/// <summary>The library's verdict on one agent attempt.</summary>
public sealed record WorkflowVerdict(
    string Task,
    int Attempt,
    string Head,
    bool Passed,
    string Red,
    IReadOnlyList<string> Reasons,
    DateTimeOffset At,
    string? TestCommit = null);

public sealed record WorkflowCheckRun(
    string Head,
    bool Passed,
    string Output,
    DateTimeOffset At,
    string? FailedHookId = null,
    string? OnFailure = null,
    string? FailedRepo = null,
    int GateRetries = 0);

/// <summary>Pure summary of a high/critical review finding extracted from <c>build/review.md</c> for fix-step briefs.</summary>
public sealed record ReviewFindingSummary(string Id, string? Repo, string File, int Line, string Text);

public sealed record WorkflowBaselineRun(string BaseHead, bool Passed, string Output, DateTimeOffset At);

public sealed record WorkflowApproval(string Head, bool Approved, string Note, DateTimeOffset At);

public sealed record WorkflowPublished(string Head, string Ref, DateTimeOffset At, string? PullRequestUrl = null);

public sealed record WorkflowPublishResult(WorkflowPublished? Published, string? Error)
{
    public bool Ok => Published is not null && Error is null;
}

public sealed record WorkflowReviewRun(string Head, int ReviewNumber, int FindingCount, int HighCount, DateTimeOffset At);

/// <summary>An active async subagent recorded in the ledger so concurrent launches are refused and completion is judged once.</summary>
public sealed record ActiveSubagent(string Task, int Attempt, string AgentId, DateTimeOffset LaunchedAt);

/// <summary>
/// Fast-path step state written beside the ledger (<c>.git/hoshin/step.json</c>) so <c>hoshin guard</c> never
/// recomputes <see cref="WorkflowState"/> and isolates unrelated sessions.
/// </summary>
public sealed record CurrentStep(
    StepKind Kind,
    string? Task,
    int Attempt,
    string? StartHead,
    DateTimeOffset At,
    string? AgentId = null,
    string? SessionId = null,
    string? ChangeFolder = null,
    string[]? TestPatterns = null,
    string[]? TaskTestFiles = null,
    string? Change = null,
    string? DispatchNonce = null,
    bool HasTest = true,
    string? TargetRepo = null,
    bool IsFix = false,
    string? FixInstructions = null);

/// <summary>Active run marker stored in <c>.git/hoshin/run.json</c> identifying the owning session and harness.</summary>
public sealed record RunMarker(
    DateTimeOffset Started,
    string? SessionId = null,
    string? Harness = null,
    string? ChangeFolder = null,
    string? Change = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CurrentStep))]
[JsonSerializable(typeof(AgentTask))]
[JsonSerializable(typeof(RunMarker))]
public sealed partial class WorkflowJsonContext : JsonSerializerContext;
