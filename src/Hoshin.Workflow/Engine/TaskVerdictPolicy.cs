using System.Text.RegularExpressions;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Inputs passed to an <see cref="ITaskVerdictPolicy"/> once common safety checks have passed.</summary>
public sealed record TaskEvaluationContext(
    WorkflowRepo Repo,
    WorkflowState State,
    WorkflowTask Task,
    int Attempt,
    string StartHead,
    IReadOnlyList<WorkingTreeChange> Changed,
    IGitWorktrees GitWorktrees,
    IGitCommitter GitCommitter,
    IAllowListedCommandRunner Runner,
    DateTimeOffset At);

/// <summary>Strategy for verifying and committing one plan task (open-closed per CONTRIBUTING.md §3.3 O2).</summary>
public interface ITaskVerdictPolicy
{
    bool AppliesTo(WorkflowTask task);

    WorkflowVerdict EvaluateAndCommit(TaskEvaluationContext context);
}

/// <summary>
/// Verdict policy for tasks with <c>test: null</c> (no testable behavior such as configuration or documentation).
/// Skips Red/Green, refuses any test-file changes, and records a single commit; <c>build.finish</c> checks prove the task.
/// </summary>
public sealed class NoTestPolicy : ITaskVerdictPolicy
{
    public const string SkippedRedSummary = "skipped: no testable behavior";

    public bool AppliesTo(WorkflowTask task) => task.Test is null or { Length: 0 };

    public WorkflowVerdict EvaluateAndCommit(TaskEvaluationContext context)
    {
        var plan = context.State.Plan;
        var testChanges = context.Changed
            .Where(c => WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns))
            .ToList();
        if (testChanges.Count > 0)
        {
            return new WorkflowVerdict(
                context.Task.Id,
                context.Attempt,
                context.StartHead,
                false,
                SkippedRedSummary,
                ["order: a task with no test must not change test files"],
                context.At);
        }

        var paths = context.Changed.Select(c => c.Path).ToArray();
        var workType = string.IsNullOrWhiteSpace(plan.WorkType) ? "feat" : plan.WorkType;
        var trailers = $"Plan-Task: {context.Task.Id}\nGenerated-By: hoshin";
        var commit = context.GitCommitter.CommitPaths(
            context.Repo.Root,
            paths,
            [$"{workType}({plan.Scope}): {context.Task.Title}", trailers]);
        if (!commit.Ok || string.IsNullOrEmpty(commit.Sha))
        {
            context.GitCommitter.ResetMixed(context.Repo.Root, context.StartHead);
            return new WorkflowVerdict(
                context.Task.Id,
                context.Attempt,
                context.StartHead,
                false,
                SkippedRedSummary,
                [$"git: implementation commit failed: {TaskCommitHelpers.Squash(commit.Error ?? commit.Output)}"],
                context.At);
        }

        return new WorkflowVerdict(
            context.Task.Id,
            context.Attempt,
            commit.Sha,
            true,
            SkippedRedSummary,
            [],
            context.At);
    }
}

/// <summary>
/// Verdict policy for testable tasks: requires test + production changes, verifies Green on the working tree,
/// verifies Red on a cached detached worktree containing only the test files, splits into two atomic commits
/// (<c>test</c> then <c>&lt;type&gt;</c>), and re-verifies Green at committed HEAD.
/// </summary>
public sealed class RedGreenPolicy : ITaskVerdictPolicy
{
    public bool AppliesTo(WorkflowTask task) => task.Test is { Length: > 0 };

    public WorkflowVerdict EvaluateAndCommit(TaskEvaluationContext context)
    {
        var plan = context.State.Plan;
        var task = context.Task;
        var testCmd = task.Test!;

        var taskTestFiles = WorkflowRepo.ExtractTestFilesFromArgv(testCmd, context.Repo.Root, plan.EffectiveTestPatterns);
        var testChanges = context.Changed.Where(c => WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns, taskTestFiles)).ToList();
        var prodChanges = context.Changed.Where(c => !WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns, taskTestFiles)).ToList();

        var orderProblems = ValidateTestAndProdPartition(task.Id, testChanges, prodChanges);
        if (orderProblems.Count > 0)
        {
            return Failed(context, "not observed: missing test or production changes", orderProblems);
        }

        var taskEnv = AllowListedCommandRunner.CollectAdditionalEnvironment(plan.EnvAllow);
        var taskTimeout = plan.EffectiveTaskTimeout(task);

        if (VerifyWorkingTreeGreen(context, testCmd, taskEnv, taskTimeout) is { } preFail)
        {
            return preFail;
        }

        var prodPathsForRed = prodChanges.Select(c => c.Path).ToList();
        var redCheck = RedWorktree.ObserveRed(
            context.Repo,
            context.GitWorktrees,
            context.Runner,
            context.StartHead,
            testCmd,
            testChanges,
            prodPathsForRed,
            taskEnv,
            taskTimeout,
            task.Compile,
            plan.LinkDirs);
        if (!redCheck.IsValidRed)
        {
            return Failed(context, redCheck.Summary, [$"red: {redCheck.Summary}"]);
        }

        return CommitTwoPhaseAndVerifyGreen(context, testCmd, testChanges, prodChanges, redCheck.Summary, taskEnv, taskTimeout);
    }

    private static List<string> ValidateTestAndProdPartition(
        string taskId,
        List<WorkingTreeChange> testChanges,
        List<WorkingTreeChange> prodChanges)
    {
        var reasons = new List<string>();
        if (testChanges.Count == 0)
        {
            reasons.Add($"order: no test file was added or modified for {taskId}");
        }

        if (prodChanges.Count == 0)
        {
            reasons.Add($"order: no production file was added or modified for {taskId}");
        }

        return reasons;
    }

    private static WorkflowVerdict? VerifyWorkingTreeGreen(
        TaskEvaluationContext context,
        string[] testCmd,
        IReadOnlyDictionary<string, string>? taskEnv,
        TimeSpan taskTimeout)
    {
        if (context.Task.Compile is { Length: > 0 } compileCmd)
        {
            var compile = context.Runner.Run(context.Repo.Root, compileCmd, taskTimeout, taskEnv);
            if (!compile.Ok)
            {
                var kind = compile.TimedOut ? $"timed out after {(int)taskTimeout.TotalSeconds}s" : "failed";
                return Failed(
                    context,
                    "not observed: working tree did not compile",
                    [$"compile: `{string.Join(' ', compileCmd)}` {kind}: {TaskCommitHelpers.Squash(compile.Tail(15))}"]);
            }
        }

        var greenPre = context.Runner.Run(context.Repo.Root, testCmd, taskTimeout, taskEnv);
        if (!greenPre.Ok)
        {
            var kind = greenPre.TimedOut ? $"timed out after {(int)taskTimeout.TotalSeconds}s" : "failed";
            return Failed(
                context,
                "not observed: green did not pass",
                [$"green: `{string.Join(' ', testCmd)}` {kind} in the working tree: {TaskCommitHelpers.Squash(greenPre.Tail(15))}"]);
        }

        return null;
    }

    private static WorkflowVerdict CommitTwoPhaseAndVerifyGreen(
        TaskEvaluationContext context,
        string[] testCmd,
        List<WorkingTreeChange> testChanges,
        List<WorkingTreeChange> prodChanges,
        string redSummary,
        IReadOnlyDictionary<string, string>? taskEnv,
        TimeSpan taskTimeout)
    {
        var repo = context.Repo;
        var plan = context.State.Plan;
        var task = context.Task;
        var trailers = $"Plan-Task: {task.Id}\nGenerated-By: hoshin";
        var testPaths = testChanges.Select(c => c.Path).ToArray();
        var prodPaths = prodChanges.Select(c => c.Path).ToArray();

        var testCommit = context.GitCommitter.CommitPaths(repo.Root, testPaths, [$"test({plan.Scope}): {task.Title}", trailers]);
        if (!testCommit.Ok || string.IsNullOrEmpty(testCommit.Sha))
        {
            return ResetAndFail(context, redSummary, $"git: test commit failed: {TaskCommitHelpers.Squash(testCommit.Error ?? testCommit.Output)}");
        }

        var testCommitSha = testCommit.Sha;
        var workType = string.IsNullOrWhiteSpace(plan.WorkType) ? "feat" : plan.WorkType;
        var prodCommit = context.GitCommitter.CommitPaths(repo.Root, prodPaths, [$"{workType}({plan.Scope}): {task.Title}", trailers]);
        if (!prodCommit.Ok || string.IsNullOrEmpty(prodCommit.Sha))
        {
            return ResetAndFail(context, redSummary, $"git: implementation commit failed: {TaskCommitHelpers.Squash(prodCommit.Error ?? prodCommit.Output)}");
        }

        var headSha = prodCommit.Sha;
        var greenHead = context.Runner.Run(repo.Root, testCmd, taskTimeout, taskEnv);
        if (!greenHead.Ok)
        {
            var kind = greenHead.TimedOut ? $"timed out after {(int)taskTimeout.TotalSeconds}s" : "failed";
            return ResetAndFail(context, redSummary, $"green: `{string.Join(' ', testCmd)}` {kind} at HEAD: {TaskCommitHelpers.Squash(greenHead.Tail(15))}");
        }

        var finalRed = $"{redSummary} at {WorkflowEngine.Short(testCommitSha)}";
        return new WorkflowVerdict(task.Id, context.Attempt, headSha, true, finalRed, [], context.At, testCommitSha);
    }

    private static WorkflowVerdict ResetAndFail(TaskEvaluationContext context, string redSummary, string reason)
    {
        context.GitCommitter.ResetMixed(context.Repo.Root, context.StartHead);
        return Failed(context, redSummary, [reason]);
    }

    private static WorkflowVerdict Failed(TaskEvaluationContext context, string redSummary, IReadOnlyList<string> reasons) =>
        new(context.Task.Id, context.Attempt, context.StartHead, false, redSummary, reasons, context.At);
}

internal static partial class TaskCommitHelpers
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    public static string Squash(string text)
    {
        var s = WhitespaceRegex().Replace(text, " ").Trim();
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}
