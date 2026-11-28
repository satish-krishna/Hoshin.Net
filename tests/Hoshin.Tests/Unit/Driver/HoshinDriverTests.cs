using System.Text.Json;
using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Hosting;
using Xunit;

namespace Hoshin.Tests.Unit.Driver;

/// <summary>The harness-neutral driver takes a change from start to published pull request with no harness involved.</summary>
public sealed class HoshinDriverTests
{
    private static readonly DecidedBy Engineer = new("pat", "test");

    [Fact]
    public async Task Driver_TakesASingleRepoChange_FromStartToPublishedPullRequest()
    {
        using var project = DriverProject.Create();
        var agent = new ScriptedAgent();

        var (finished, transcript) = await project.RunToEnd(agent, d => d.Allowed.Contains(DecisionKind.Accept) ? new Accept() : new Approve("ship it"));

        Assert.True(finished.Success, $"{finished.Outcome}\n{transcript}");
        Assert.Equal([Role.Intent, Role.Spec, Role.Plan, Role.Implement, Role.Review], agent.Done);
        Assert.Equal(1, project.PullRequests.OpenNewCalls);
        var log = project.Git("log", "--format=%s%n%b", "HEAD");
        Assert.Contains("test(", log, StringComparison.Ordinal);
        Assert.Contains("Plan-Task: T1", log, StringComparison.Ordinal);
        Assert.Contains("checks passed", transcript, StringComparison.Ordinal);
        Assert.Contains("published", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Driver_StaysFinished_AfterPublishing()
    {
        using var project = DriverProject.Create();
        await project.RunToEnd(new ScriptedAgent(), d => d.Allowed.Contains(DecisionKind.Accept) ? new Accept() : new Approve(""));

        var again = await project.Driver.NextAsync(project.Change!);

        var finished = Assert.IsType<Finished>(again);
        Assert.True(finished.Success);
        Assert.Equal(1, project.PullRequests.OpenNewCalls);
    }

    [Fact]
    public async Task Submit_ReturnsTheSameWorkWithProblems_WhenTheDocumentIsInvalid()
    {
        using var project = DriverProject.Create();
        var agent = new ScriptedAgent { WriteBrokenPlanFirst = true };

        var (finished, transcript) = await project.RunToEnd(agent, d => d.Allowed.Contains(DecisionKind.Accept) ? new Accept() : new Approve(""));

        Assert.True(finished.Success, $"{finished.Outcome}\n{transcript}");
        Assert.Equal(2, agent.Done.Count(r => r == Role.Plan));
        Assert.Contains("R9.9", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_RetriesTheTask_WhenRedIsNotObserved()
    {
        using var project = DriverProject.Create();
        var agent = new ScriptedAgent { SkipTestOnFirstAttempt = true };

        var (finished, transcript) = await project.RunToEnd(agent, d => d.Allowed.Contains(DecisionKind.Accept) ? new Accept() : new Approve(""));

        Assert.True(finished.Success, $"{finished.Outcome}\n{transcript}");
        Assert.Equal(2, agent.Done.Count(r => r == Role.Implement));
        Assert.Contains("task refused", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_IsRefused_ForWorkThatIsNoLongerCurrent()
    {
        using var project = DriverProject.Create();
        var change = await project.Start();
        var work = Assert.IsType<AgentWork>(await project.Driver.NextAsync(change));

        var stale = work.Id with { Nonce = "stale" };

        await Assert.ThrowsAsync<DriverRefusedException>(() => project.Driver.SubmitAsync(change, stale));
    }

    [Fact]
    public async Task Decide_IsRefused_WhenTheChangeIsNotAwaitingThatDecision()
    {
        using var project = DriverProject.Create();
        var change = await project.Start();
        Assert.IsType<AgentWork>(await project.Driver.NextAsync(change));

        var refused = await Assert.ThrowsAsync<DriverRefusedException>(() => project.Driver.DecideAsync(change, new Approve(""), Engineer));

        Assert.Contains("does not apply now", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decline_RunsAFixTask_ThenAsksForApprovalAgain()
    {
        using var project = DriverProject.Create();
        var agent = new ScriptedAgent();
        var declined = false;

        var (finished, transcript) = await project.RunToEnd(agent, d =>
        {
            if (d.Allowed.Contains(DecisionKind.Accept))
            {
                return new Accept();
            }

            if (!declined)
            {
                declined = true;
                return new Decline("shout must also work for an empty name");
            }

            return new Approve("");
        });

        Assert.True(finished.Success, $"{finished.Outcome}\n{transcript}");
        Assert.True(agent.Done.Count(r => r == Role.Implement) >= 2, transcript);
    }

    [Fact]
    public async Task ListOpen_FindsAChange_OnceItsIntentIsDrafted()
    {
        using var project = DriverProject.Create();
        var change = await project.Start();
        var work = Assert.IsType<AgentWork>(await project.Driver.NextAsync(change));
        new ScriptedAgent().Do(work);
        Assert.IsType<HumanDecision>(await project.Driver.SubmitAsync(change, work.Id));

        var open = await project.Driver.ListOpenAsync(new WorkspaceRef(project.Repo));

        Assert.True(open.Any(c => c.Change == change.Change), $"started {change}; listed: {string.Join("; ", open)}");
    }

    [Fact]
    public async Task Start_IsRefused_WithoutAName()
    {
        using var project = DriverProject.Create();

        await Assert.ThrowsAsync<DriverRefusedException>(() => project.Driver.StartAsync(new StartRequest(new WorkspaceRef(project.Repo), "")));
    }

    /// <summary>A single-repo hoshin project with a node test suite, a bare remote and a recording PR client.</summary>
    internal sealed class DriverProject : IDisposable
    {
        private const int MaxTurns = 40;
        private readonly TempDir _temp;

        private DriverProject(TempDir temp, string repo, RecordingPullRequests prs, IHoshinDriver driver)
        {
            _temp = temp;
            Repo = repo;
            PullRequests = prs;
            Driver = driver;
        }

        public string Repo { get; }

        public RecordingPullRequests PullRequests { get; }

        public IHoshinDriver Driver { get; }

        public ChangeRef? Change { get; private set; }

        public static DriverProject Create()
        {
            var temp = new TempDir("hoshin-driver-");
            var repo = Path.Combine(temp.Path, "ws", "greeter");
            TestGitFixture.InitRepo(repo, new Dictionary<string, string>
            {
                [".hoshin.json"] = JsonSerializer.Serialize(new
                {
                    version = 1,
                    layout = "single-repo",
                    systems = new Dictionary<string, object> { ["shop"] = new { apps = new Dictionary<string, string> { ["app"] = "." } } },
                    hooks = new Dictionary<string, object> { ["build.finish"] = new[] { new { id = "unit", run = new[] { "node", "--test" } } } },
                }) + "\n",
                ["package.json"] = "{ \"name\": \"greeter\", \"type\": \"module\", \"private\": true }\n",
                ["src/greet.mjs"] = "export const greet = (name) => `Hello, ${name}`;\n",
                ["test/greet.test.mjs"] = """
                    import test from 'node:test';
                    import assert from 'node:assert/strict';
                    import { greet } from '../src/greet.mjs';
                    test('greet', () => { assert.equal(greet('Ada'), 'Hello, Ada'); });
                    """,
            });
            var prs = new RecordingPullRequests();
            var services = new HoshinServices(TestServices.SchemaValidator, TestServices.CommandRunner, TestServices.Git, prs, TestServices.Time, TestServices.FileSystem);
            return new DriverProject(temp, repo, prs, services.CreateDriver(new HoshinDriverOptions()));
        }

        public async Task<ChangeRef> Start()
        {
            Change = await Driver.StartAsync(new StartRequest(new WorkspaceRef(Repo), "add-shout"));
            return Change;
        }

        public string Git(params string[] args) => TestGitFixture.Git(Change?.CheckoutRoot ?? Repo, args);

        /// <summary>Plays the host loop: the agent does every piece of work, <paramref name="decide"/> answers every decision.</summary>
        public async Task<(Finished Finished, string Transcript)> RunToEnd(ScriptedAgent agent, Func<HumanDecision, Decision> decide)
        {
            var change = Change ?? await Start();
            var transcript = new System.Text.StringBuilder();
            var turn = await Driver.NextAsync(change);
            for (var i = 0; i < MaxTurns; i++)
            {
                transcript.AppendLine(Describe(turn));
                switch (turn)
                {
                    case Finished finished:
                        return (finished, transcript.ToString());
                    case AgentWork work:
                        agent.Do(work);
                        turn = await Driver.SubmitAsync(change, work.Id);
                        break;
                    case HumanDecision decision:
                        turn = await Driver.DecideAsync(change, decide(decision), Engineer);
                        break;
                    default:
                        throw new InvalidOperationException($"unknown turn {turn}");
                }
            }

            throw new InvalidOperationException($"the change did not finish in {MaxTurns} turns:\n{transcript}");
        }

        private static string Describe(Turn turn)
        {
            var happened = string.Concat(turn.Happened.Select(h => $"\n  {h.What}: {h.Detail}"));
            return turn switch
            {
                AgentWork w => $"work {w.Role} {w.Id.Task} attempt {w.Attempt}{string.Concat(w.Feedback.Select(f => $"\n  feedback: {f}"))}{happened}",
                HumanDecision d => $"decide [{string.Join(", ", d.Allowed)}]: {d.Summary}{happened}",
                Finished f => $"finished {f.Success}: {f.Outcome}{happened}",
                _ => turn.ToString(),
            };
        }

        public void Dispose() => _temp.Dispose();
    }

    internal sealed class RecordingPullRequests : IPullRequestClient
    {
        private readonly Dictionary<string, PullRequestRef> _opened = new(StringComparer.Ordinal);

        public int OpenNewCalls { get; private set; }

        public string? HostOf(string remoteUrl) => "github";

        public string? SignInProblem(string cwd, string? host) => null;

        public PullRequestRef? OpenPullRequest(string cwd, string branch) =>
            _opened.TryGetValue($"{Path.GetFileName(cwd)}:{branch}", out var existing) ? existing : null;

        public OpenPullRequestResult OpenNew(string cwd, OpenPullRequestInput pr)
        {
            OpenNewCalls++;
            var created = new PullRequestRef($"https://example.test/{Path.GetFileName(cwd)}/pull/{OpenNewCalls}", OpenNewCalls);
            _opened[$"{Path.GetFileName(cwd)}:{pr.Head}"] = created;
            return new OpenPullRequestResult(created, null);
        }

        public string? StateOf(string cwd, string url) => PullRequestStates.Open;
    }
}
