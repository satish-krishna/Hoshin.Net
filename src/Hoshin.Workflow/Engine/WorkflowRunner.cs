using System.Text;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Engine;

/// <summary>Library-owned execution steps for the workflow state machine (Red/Green two-phase commits, checks, baseline, publish, document commits).</summary>
public interface IWorkflowRunner
{
    void Issue(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId = null);

    bool TryRegisterActiveSubagent(WorkflowRepo repo, string taskId, int attempt, string agentId, out string? existingAgentId);

    void ClearActiveSubagent(WorkflowRepo repo, string? agentId = null);

    WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt);

    WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state);

    WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state);

    WorkflowApproval Answer(WorkflowRepo repo, WorkflowState state, bool approved, string note);

    WorkflowPublished? Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = false);

    (IReadOnlyList<Problem> Problems, string? Commit) CommitDocument(WorkflowRepo repo, WorkflowState state, string name);

    string? AcceptDocument(WorkflowRepo repo, WorkflowState state, string name);
}

/// <summary>
/// Default SOLID implementation of <see cref="IWorkflowRunner"/>.
/// Runs tests, Red verification (on a cached detached worktree), baselines, and <c>build.finish</c> hooks through
/// <see cref="IAllowListedCommandRunner"/>, performs the two-phase Red/Green commit split atomically (leaving zero
/// commits on failure), and publishes branches/PRs only after approval.
/// </summary>
public sealed class WorkflowRunner(
    IAllowListedCommandRunner? commandRunner = null,
    IPullRequestClient? pullRequests = null,
    IntentReader? intentReader = null,
    SpecReader? specReader = null,
    PlanReader? planReader = null) : IWorkflowRunner
{
    public static WorkflowRunner Default { get; } = new();

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IAllowListedCommandRunner _runner = commandRunner ?? AllowListedCommandRunner.Default;
    private readonly IPullRequestClient _pullRequests = pullRequests ?? new GhPullRequestClient();
    private readonly IntentReader _intentReader = intentReader ?? new IntentReader();
    private readonly SpecReader _specReader = specReader ?? new SpecReader();
    private readonly PlanReader _planReader = planReader ?? new PlanReader();

    public void Issue(WorkflowRepo repo, WorkflowState state, Step step, string? sessionId = null)
    {
        if (step is Implement or Retry)
        {
            var agent = step is Implement i ? i.Agent : ((Retry)step).Agent;
            var task = state.Plan.Tasks.FirstOrDefault(t => t.Id == agent.Task);
            var taskTestFiles = task is null ? null : WorkflowRepo.ExtractTestFilesFromArgv(task.Test);

            repo.WithLedger(ledger =>
            {
                if (!ledger.Issued.Any(x => x.Task == agent.Task && x.Attempt == agent.Attempt))
                {
                    var taskStart = ledger.Issued.FirstOrDefault(x => x.Task == agent.Task)?.StartHead ?? repo.Head();
                    ledger.Issued.Add(new WorkflowIssued(agent.Task, agent.Attempt, taskStart, DateTimeOffset.UtcNow));
                }

                return 0;
            });

            var start = repo.ReadLedger().Issued.First(x => x.Task == agent.Task).StartHead;
            var previous = repo.ReadStep();
            var same = previous is not null && previous.Task == agent.Task && previous.Attempt == agent.Attempt;
            var activeSession = sessionId ?? previous?.SessionId ?? repo.ReadRunMarker()?.SessionId;
            var nextStep = same
                ? previous! with { SessionId = activeSession, ChangeFolder = repo.ChangeFolder, TestPatterns = state.Plan.EffectiveTestPatterns, TaskTestFiles = taskTestFiles }
                : new CurrentStep(step.Kind, agent.Task, agent.Attempt, start, DateTimeOffset.UtcNow, AgentId: null, SessionId: activeSession, ChangeFolder: repo.ChangeFolder, TestPatterns: state.Plan.EffectiveTestPatterns, TaskTestFiles: taskTestFiles);
            repo.WriteStep(nextStep);
        }
        else
        {
            var document = step switch
            {
                WriteDocument w => w.Document,
                AwaitAccept a => a.Document,
                _ => null,
            };
            var activeSession = sessionId ?? repo.ReadRunMarker()?.SessionId;
            repo.WriteStep(new CurrentStep(step.Kind, document, 0, null, DateTimeOffset.UtcNow, AgentId: null, SessionId: activeSession, ChangeFolder: repo.ChangeFolder));
        }
    }

    public bool TryRegisterActiveSubagent(WorkflowRepo repo, string taskId, int attempt, string agentId, out string? existingAgentId)
    {
        string? conflict = null;
        var registered = repo.WithLedger(ledger =>
        {
            if (ledger.ActiveSubagent is { } active && !string.Equals(active.AgentId, agentId, StringComparison.Ordinal))
            {
                conflict = active.AgentId;
                return false;
            }

            ledger.ActiveSubagent = new ActiveSubagent(taskId, attempt, agentId, DateTimeOffset.UtcNow);
            return true;
        });

        existingAgentId = conflict;
        if (registered)
        {
            var step = repo.ReadStep();
            if (step is not null)
            {
                repo.WriteStep(step with { AgentId = agentId });
            }
        }

        return registered;
    }

    public void ClearActiveSubagent(WorkflowRepo repo, string? agentId = null)
    {
        repo.WithLedger(ledger =>
        {
            if (agentId is null || ledger.ActiveSubagent?.AgentId == agentId)
            {
                ledger.ActiveSubagent = null;
            }

            return 0;
        });

        var step = repo.ReadStep();
        if (step is not null && (agentId is null || step.AgentId == agentId))
        {
            repo.WriteStep(step with { AgentId = null });
        }
    }

    /// <summary>
    /// Evaluates the agent's working-tree edits and performs the strongly-typed two-phase Red/Green commit split:
    /// 1. Partitions working-tree changes into test files vs. production files.
    /// 2. Verifies Green on the working tree (test command passes with both test and production changes).
    /// 3. Verifies Red on a cached detached worktree containing ONLY the test files on top of <c>startHead</c>
    ///    (failing assertion OR missing-symbol compilation error such as CS0246/CS0103/CS1061/TS2304; never syntax errors).
    /// 4. Commits test files first (<c>test(&lt;scope&gt;): &lt;task title&gt;</c> with <c>Plan-Task</c> and <c>Generated-By</c> trailers),
    ///    then commits production files (<c>&lt;type&gt;(&lt;scope&gt;): &lt;task title&gt;</c> with trailers) and verifies Green at HEAD.
    /// Any failure commits NOTHING, preserving working-tree changes as handoff for <see cref="Retry"/>.
    /// </summary>
    public WorkflowVerdict EvaluateAndCommitTask(WorkflowRepo repo, WorkflowState state, string taskId, int attempt)
    {
        var plan = state.Plan;
        var task = plan.Tasks.First(t => t.Id == taskId);
        var startHead = repo.Head();

        var existing = state.Ledger.Verdicts.FirstOrDefault(v => v.Task == taskId && v.Attempt == attempt);
        if (existing is not null)
        {
            return existing;
        }

        ClearActiveSubagent(repo);

        var changed = GetWorkingTreeChanges(repo);
        var reasons = new List<string>();
        var redSummary = "not observed: no test commit";

        if (changed.Count == 0)
        {
            reasons.Add($"work: no working-tree changes were made for {taskId}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        // Refuse edits to protected workflow/harness folders.
        var protectedHits = changed.Where(c => IsProtectedPath(c.Path)).Select(c => c.Path).ToList();
        if (protectedHits.Count > 0)
        {
            reasons.Add($"protected: modified protected workflow files: {string.Join(", ", protectedHits)}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        var taskTestFiles = WorkflowRepo.ExtractTestFilesFromArgv(task.Test);
        var testChanges = changed.Where(c => WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns, taskTestFiles)).ToList();
        var prodChanges = changed.Where(c => !WorkflowRepo.IsTestPath(c.Path, plan.EffectiveTestPatterns, taskTestFiles)).ToList();

        if (testChanges.Count == 0)
        {
            reasons.Add($"order: no test file was added or modified for {taskId}");
        }

        if (prodChanges.Count == 0)
        {
            reasons.Add($"order: no production file was added or modified for {taskId}");
        }

        if (reasons.Count > 0)
        {
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, "not observed: missing test or production changes", reasons, DateTimeOffset.UtcNow));
        }

        // Step 1: Verify Green on the full working tree before committing anything.
        if (task.Compile is { Length: > 0 })
        {
            var compile = _runner.Run(repo.Root, task.Compile, TimeSpan.FromMinutes(5));
            if (!compile.Ok)
            {
                reasons.Add($"compile: `{string.Join(' ', task.Compile)}` failed: {Squash(compile.Tail(15))}");
                return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, "not observed: working tree did not compile", reasons, DateTimeOffset.UtcNow));
            }
        }

        var greenPre = _runner.Run(repo.Root, task.Test, TimeSpan.FromMinutes(5));
        if (!greenPre.Ok)
        {
            reasons.Add($"green: `{string.Join(' ', task.Test)}` failed in the working tree: {Squash(greenPre.Tail(15))}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, "not observed: green did not pass", reasons, DateTimeOffset.UtcNow));
        }

        // Step 2: Verify Red on a cached detached worktree containing ONLY testChanges on top of startHead.
        var redCheck = ObserveRedOnCachedWorktree(repo, task, startHead, testChanges);
        redSummary = redCheck.Summary;
        if (!redCheck.IsValidRed)
        {
            reasons.Add($"red: {redCheck.Summary}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        // Step 3: Two-phase atomic commit split (test commit first, then production commit).
        var trailers = $"Plan-Task: {task.Id}\nGenerated-By: hoshin";
        var testPaths = testChanges.Select(c => c.Path).ToArray();
        var prodPaths = prodChanges.Select(c => c.Path).ToArray();

        if (!StagePaths(repo, testPaths, out var stageTestErr))
        {
            reasons.Add($"git: could not stage test files: {stageTestErr}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        var testCommit = repo.Git("commit", "-q", "-m", $"test({plan.Scope}): {task.Title}", "-m", trailers);
        if (!testCommit.Ok)
        {
            repo.Git("reset", "--mixed", "--quiet", startHead);
            reasons.Add($"git: test commit failed: {Squash(testCommit.Tail(10))}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        var testCommitSha = repo.Head();

        if (!StagePaths(repo, prodPaths, out var stageProdErr))
        {
            repo.Git("reset", "--mixed", "--quiet", startHead);
            reasons.Add($"git: could not stage production files: {stageProdErr}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        var workType = string.IsNullOrWhiteSpace(plan.WorkType) ? "feat" : plan.WorkType;
        var prodCommit = repo.Git("commit", "-q", "-m", $"{workType}({plan.Scope}): {task.Title}", "-m", trailers);
        if (!prodCommit.Ok)
        {
            repo.Git("reset", "--mixed", "--quiet", startHead);
            reasons.Add($"git: implementation commit failed: {Squash(prodCommit.Tail(10))}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        var headSha = repo.Head();

        // Step 4: Observe Green at committed HEAD.
        var greenHead = _runner.Run(repo.Root, task.Test, TimeSpan.FromMinutes(5));
        if (!greenHead.Ok)
        {
            repo.Git("reset", "--mixed", "--quiet", startHead);
            reasons.Add($"green: `{string.Join(' ', task.Test)}` failed at HEAD: {Squash(greenHead.Tail(15))}");
            return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, startHead, false, redSummary, reasons, DateTimeOffset.UtcNow));
        }

        var finalRed = $"{redCheck.Summary} at {WorkflowEngine.Short(testCommitSha)}";
        return RecordVerdict(repo, new WorkflowVerdict(taskId, attempt, headSha, true, finalRed, [], DateTimeOffset.UtcNow, testCommitSha));
    }

    public WorkflowBaselineRun RunBaseline(WorkflowRepo repo, WorkflowState state)
    {
        var baseHead = repo.Git("rev-parse", state.Plan.Base);
        var sha = baseHead.Ok ? baseHead.Output.Trim() : state.Head;
        var outputs = new List<string>();
        var passed = true;

        var env = new Dictionary<string, string> { ["HOSHIN_BASELINE"] = "1" };
        foreach (var check in state.Plan.Checks)
        {
            var run = _runner.Run(repo.Root, check, TimeSpan.FromMinutes(10), env);
            passed &= run.Ok;
            outputs.Add($"$ {string.Join(' ', check)} -> {run.ExitCode}\n{run.Tail(20)}");
        }

        var baseline = new WorkflowBaselineRun(sha, passed, string.Join("\n", outputs), DateTimeOffset.UtcNow);
        repo.WithLedger(l =>
        {
            l.Baselines.Add(baseline);
            return 0;
        });
        return baseline;
    }

    public WorkflowCheckRun RunChecks(WorkflowRepo repo, WorkflowState state)
    {
        var outputs = new List<string>();
        var passed = true;

        foreach (var check in state.Plan.Checks)
        {
            var run = _runner.Run(repo.Root, check, TimeSpan.FromMinutes(10));
            passed &= run.Ok;
            outputs.Add($"$ {string.Join(' ', check)} -> {run.ExitCode}\n{run.Tail(20)}");
        }

        var result = new WorkflowCheckRun(state.Head, passed, string.Join("\n", outputs), DateTimeOffset.UtcNow);
        repo.WithLedger(l =>
        {
            l.Checks.Add(result);
            return 0;
        });
        return result;
    }

    public WorkflowApproval Answer(WorkflowRepo repo, WorkflowState state, bool approved, string note)
    {
        var approval = new WorkflowApproval(state.Head, approved, note, DateTimeOffset.UtcNow);
        repo.WithLedger(l =>
        {
            if (!l.Approvals.Any(a => a.Head == state.Head))
            {
                l.Approvals.Add(approval);
            }

            return 0;
        });
        return approval;
    }

    public WorkflowPublished? Publish(WorkflowRepo repo, WorkflowState state, string targetRef, bool openPullRequest = false)
    {
        var push = repo.Git("push", "origin", $"HEAD:{targetRef}");
        if (!push.Ok)
        {
            return null;
        }

        string? prUrl = null;
        if (openPullRequest)
        {
            var branch = targetRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? targetRef["refs/heads/".Length..]
                : targetRef;
            var existing = _pullRequests.OpenPullRequest(repo.Root, branch);
            if (existing is not null)
            {
                prUrl = existing.Url;
            }
            else
            {
                var created = _pullRequests.OpenNew(repo.Root, new OpenPullRequestInput(state.Plan.Base, branch, state.Plan.Change, $"Automated publish for {state.Plan.Change}."));
                if (created.PullRequest is not null)
                {
                    prUrl = created.PullRequest.Url;
                }
            }
        }

        var published = new WorkflowPublished(state.Head, targetRef, DateTimeOffset.UtcNow, prUrl);
        repo.WithLedger(l =>
        {
            l.Published.Add(published);
            return 0;
        });
        return published;
    }

    /// <summary>
    /// Auto-wraps bare line-1 JSONC front matter in <c>```jsonc ... ```</c> fences if needed, validates the document,
    /// writes upstream source pins (<c>source_intent_commit</c> / <c>source_spec_commit</c>), and commits it alone.
    /// </summary>
    public (IReadOnlyList<Problem> Problems, string? Commit) CommitDocument(WorkflowRepo repo, WorkflowState state, string name)
    {
        var path = state.Document(name).Path;
        var relative = Path.GetRelativePath(repo.Root, path).Replace('\\', '/');
        var rawText = File.ReadAllText(path);

        // Auto-wrap bare line-1 JSONC object into ```jsonc fences before validation/commit.
        var wrapped = TryAutoWrapBareJsonFrontMatter(rawText);
        if (wrapped is not null)
        {
            File.WriteAllText(path, wrapped, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            rawText = wrapped;
        }

        List<Problem> problems = name switch
        {
            "intent" => _intentReader.Check(rawText, relative, state.Plan.Scope),
            "spec" => CheckSpecDocument(rawText, relative),
            "plan" => CheckPlanDocument(rawText, relative),
            _ => [new Problem(relative, 1, $"unknown document kind {name}")],
        };

        if (problems.Count > 0)
        {
            return (problems, null);
        }

        if (name == "spec" && state.Intent.InHead)
        {
            var intentRel = Path.GetRelativePath(repo.Root, state.Intent.Path).Replace('\\', '/');
            var sha = repo.Git("log", "-1", "--format=%H", "--", intentRel).Output.Trim();
            if (sha.Length > 0)
            {
                FrontMatterEdit.Set(path, "source_intent_commit", sha);
            }
        }
        else if (name == "plan" && state.Spec.InHead)
        {
            var specRel = Path.GetRelativePath(repo.Root, state.Spec.Path).Replace('\\', '/');
            var sha = repo.Git("log", "-1", "--format=%H", "--", specRel).Output.Trim();
            if (sha.Length > 0)
            {
                FrontMatterEdit.Set(path, "source_spec_commit", sha);
            }
        }

        repo.Git("add", "--", relative);
        var commit = repo.Git("commit", "-q", "-m", $"docs({state.Plan.Scope}): draft {name} {state.Plan.Change}", "-m", "Generated-By: hoshin", "--", relative);
        return commit.Ok
            ? ([], repo.Head())
            : ([new Problem(relative, 1, $"could not commit: {Squash(commit.Tail(5))}")], null);
    }

    public string? AcceptDocument(WorkflowRepo repo, WorkflowState state, string name)
    {
        var path = state.Document(name).Path;
        var relative = Path.GetRelativePath(repo.Root, path).Replace('\\', '/');
        FrontMatterEdit.Set(path, "status", "accepted");
        repo.Git("add", "--", relative);
        var commit = repo.Git("commit", "-q", "-m", $"docs({state.Plan.Scope}): accept {name} {state.Plan.Change}", "-m", "Generated-By: hoshin", "--", relative);
        return commit.Ok ? repo.Head() : null;
    }

    /// <summary>
    /// If a document starts on line 1 with a bare JSONC object <c>{ ... }</c> (not fenced), wraps it in
    /// <c>```jsonc ... ```</c> fences when the bare object parses cleanly as JSONC.
    /// </summary>
    public static string? TryAutoWrapBareJsonFrontMatter(string text)
    {
        var normalized = Doc.Normalize(text);
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || !lines[0].TrimStart().StartsWith('{'))
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        var closeLine = -1;

        for (var lineIdx = 0; lineIdx < lines.Length && closeLine < 0; lineIdx++)
        {
            var line = lines[lineIdx];
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (inString)
                {
                    if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    break;
                }

                if (c == '"')
                {
                    inString = true;
                }
                else if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeLine = lineIdx;
                        break;
                    }
                }
            }
        }

        if (closeLine < 0)
        {
            return null;
        }

        var candidate = string.Join('\n', lines[..(closeLine + 1)]);
        if (!Jsonc.TryParse(candidate, out var parsed, out _) || parsed is not { ValueKind: System.Text.Json.JsonValueKind.Object })
        {
            return null;
        }

        var wrapped = new List<string> { "```jsonc" };
        wrapped.AddRange(lines[..(closeLine + 1)]);
        wrapped.Add("```");
        wrapped.AddRange(lines[(closeLine + 1)..]);
        return string.Join('\n', wrapped);
    }

    private List<Problem> CheckSpecDocument(string text, string file)
    {
        var doc = Doc.Split(text);
        if (FrontMatterCheck.Check(doc, file) is { } shape)
        {
            return [shape];
        }

        try
        {
            var res = _specReader.Read(text, file);
            var problems = new List<Problem>(res.Problems);
            foreach (var r in res.Document.Requirements)
            {
                if (r.From.Count == 0 && !string.IsNullOrEmpty(r.Id))
                {
                    problems.Add(new Problem(file, r.Line, $"requirement {r.Id} has no @from:<source> tag"));
                }
            }

            return problems;
        }
        catch (CouldNotRunException e)
        {
            return [new Problem(file, 1, e.Message)];
        }
    }

    private List<Problem> CheckPlanDocument(string text, string file)
    {
        var doc = Doc.Split(text);
        if (FrontMatterCheck.Check(doc, file) is { } shape)
        {
            return [shape];
        }

        try
        {
            return _planReader.Read(text, file).Problems;
        }
        catch (CouldNotRunException e)
        {
            return [new Problem(file, 1, e.Message)];
        }
    }

    private sealed record WorkingTreeChange(string Path, bool Deleted);

    private static List<WorkingTreeChange> GetWorkingTreeChanges(WorkflowRepo repo)
    {
        var status = repo.Git("status", "--porcelain", "-uall");
        var list = new List<WorkingTreeChange>();
        if (!status.Ok || string.IsNullOrWhiteSpace(status.Output))
        {
            return list;
        }

        foreach (var raw in status.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.Length < 4)
            {
                continue;
            }

            var xy = raw[..2];
            var pathPart = raw[3..].Trim();
            if (pathPart.StartsWith('"') && pathPart.EndsWith('"') && pathPart.Length >= 2)
            {
                pathPart = pathPart[1..^1];
            }

            var arrow = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                var oldPath = pathPart[..arrow].Trim();
                var newPath = pathPart[(arrow + 4)..].Trim();
                list.Add(new WorkingTreeChange(oldPath.Replace('\\', '/'), Deleted: true));
                list.Add(new WorkingTreeChange(newPath.Replace('\\', '/'), Deleted: false));
                continue;
            }

            var deleted = xy[0] == 'D' || xy[1] == 'D';
            list.Add(new WorkingTreeChange(pathPart.Replace('\\', '/'), deleted));
        }

        return list;
    }

    private RedClassification ObserveRedOnCachedWorktree(
        WorkflowRepo repo,
        WorkflowTask task,
        string startHead,
        List<WorkingTreeChange> testChanges)
    {
        var wtDir = repo.RedWorktreePath;
        var prepared = EnsureCachedWorktree(repo, wtDir, startHead);
        if (!prepared.Ok)
        {
            return new RedClassification(RedKind.TimedOut, false, $"not observed: could not prepare detached worktree at {WorkflowEngine.Short(startHead)}: {Squash(prepared.Tail(5))}");
        }

        // Overlay ONLY the test files from the working tree onto the detached worktree.
        foreach (var change in testChanges)
        {
            var src = Path.Combine(repo.Root, change.Path.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(wtDir, change.Path.Replace('/', Path.DirectorySeparatorChar));
            if (change.Deleted || !File.Exists(src))
            {
                if (File.Exists(dst))
                {
                    File.Delete(dst);
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
        }

        var redRun = _runner.Run(wtDir, task.Test, TimeSpan.FromMinutes(5));
        // Clean worktree back to startHead for reuse.
        WorkflowRepo.GitIn(_runner, wtDir, "reset", "--hard", "--quiet", startHead);
        WorkflowRepo.GitIn(_runner, wtDir, "clean", "-fd", "--quiet");

        return RedClassifier.Classify(redRun);
    }

    private AllowListedCommandResult EnsureCachedWorktree(WorkflowRepo repo, string wtDir, string startHead)
    {
        var gitMarker = Path.Combine(wtDir, ".git");
        if (Directory.Exists(wtDir) && (File.Exists(gitMarker) || Directory.Exists(gitMarker)))
        {
            var checkout = WorkflowRepo.GitIn(_runner, wtDir, "checkout", "--detach", "--force", startHead);
            if (checkout.Ok)
            {
                WorkflowRepo.GitIn(_runner, wtDir, "clean", "-fd", "--quiet");
                return checkout;
            }

            // Corrupted cached worktree: remove and recreate.
            repo.Git("worktree", "remove", "--force", wtDir);
            repo.Git("worktree", "prune");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(wtDir)!);
        if (Directory.Exists(wtDir))
        {
            Directory.Delete(wtDir, recursive: true);
        }

        return repo.Git("worktree", "add", "--detach", "--force", wtDir, startHead);
    }

    private static bool StagePaths(WorkflowRepo repo, string[] paths, out string error)
    {
        var args = new List<string> { "add", "-A", "--" };
        args.AddRange(paths);
        var res = repo.Git([.. args]);
        error = res.Ok ? "" : Squash(res.Tail(10));
        return res.Ok;
    }

    private static bool IsProtectedPath(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/').TrimStart('/');
        return norm.Equals(".hoshin.json", PathComparison)
            || norm.StartsWith(".hoshin/", PathComparison)
            || norm.StartsWith(".git/", PathComparison)
            || norm.StartsWith(".agents/", PathComparison)
            || norm.StartsWith(".claude-plugin/", PathComparison);
    }

    private static WorkflowVerdict RecordVerdict(WorkflowRepo repo, WorkflowVerdict verdict)
    {
        repo.WithLedger(ledger =>
        {
            if (!ledger.Verdicts.Any(v => v.Task == verdict.Task && v.Attempt == verdict.Attempt))
            {
                ledger.Verdicts.Add(verdict);
            }

            return 0;
        });
        return verdict;
    }

    private static string Squash(string text)
    {
        var s = Regex.Replace(text, @"\s+", " ").Trim();
        return s.Length > 400 ? s[..400] + "…" : s;
    }
}
