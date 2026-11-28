using System.Runtime.CompilerServices;
using Hoshin.Agents;
using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Hosting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Hoshin.Tests.Unit.Driver;

/// <summary>
/// The Microsoft Agent Framework host runs a whole change with real <see cref="ChatClientAgent"/>s. The model is scripted:
/// it answers with tool calls, so every file reaches disk only through the scoped tools the host gives the agent.
/// </summary>
public sealed class HoshinAgentHostTests
{
    [Fact]
    public async Task Host_RunsAChangeToPublished_WithChatClientAgentsUsingOnlyScopedTools()
    {
        using var project = HoshinDriverTests.DriverProject.Create();
        var model = new ScriptedModel(new ScriptedAgent());
        var human = new ScriptedHuman();
        var host = new HoshinAgentHost(project.Driver, new TrackingRoleAgents(model), human, TestServices.AllowListedRunner, new HoshinAgentHostOptions());
        var turns = new List<Turn>();
        host.TurnReceived += (_, t) => turns.Add(t);

        var finished = await host.StartAndRunAsync(new StartRequest(new WorkspaceRef(project.Repo), "add-shout"));

        Assert.True(finished.Success, finished.Outcome + "\n" + string.Join("\n", turns));
        Assert.Equal(1, project.PullRequests.OpenNewCalls);
        Assert.Empty(model.Refusals);
        Assert.Contains(model.ToolsCalled, t => t == ScopedTools.AskPerson);
        Assert.Contains(model.ToolsCalled, t => t == ScopedTools.RunCommand);
        Assert.Equal(1, human.Questions);
        Assert.Contains(DecisionKind.Approve, human.Decided);
    }

    [Fact]
    public async Task Host_StopsAfterMaxTries_WhenAnAgentNeverCompletesItsWork()
    {
        using var project = HoshinDriverTests.DriverProject.Create();
        var idle = new ScriptedModel(null);
        var host = new HoshinAgentHost(project.Driver, new TrackingRoleAgents(idle), new ScriptedHuman(), TestServices.AllowListedRunner, new HoshinAgentHostOptions(MaxTriesPerWork: 2));

        var finished = await host.StartAndRunAsync(new StartRequest(new WorkspaceRef(project.Repo), "add-shout"));

        Assert.False(finished.Success);
        Assert.Contains("did not complete", finished.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void UserMessage_CarriesTheBriefTheWriteScopeAndFeedback()
    {
        var work = new AgentWork(
            new WorkId(StepKind.WriteDocument, "plan", 1, "n"),
            Role.Plan,
            "instructions",
            "the brief",
            new WorkScope("/repo", ["intent/x/plan.md"], [], []),
            1,
            ["line 3: covers R9.9, which the spec does not define"],
            []);

        var message = HoshinAgentHost.UserMessage(work);

        Assert.StartsWith("the brief", message, StringComparison.Ordinal);
        Assert.Contains("intent/x/plan.md", message, StringComparison.Ordinal);
        Assert.Contains("covers R9.9", message, StringComparison.Ordinal);
        Assert.DoesNotContain("instructions", message, StringComparison.Ordinal);
    }

    private sealed class ScriptedHuman : IHumanChannel
    {
        public int Questions { get; private set; }

        public List<DecisionKind> Decided { get; } = [];

        public DecidedBy Identity { get; } = new("pat", "test");

        public Task<string> AskAsync(string question, CancellationToken cancellationToken)
        {
            Questions++;
            return Task.FromResult("Yes, the default is fine.");
        }

        public Task<Decision> DecideAsync(HumanDecision decision, CancellationToken cancellationToken)
        {
            Decision answer = decision.Allowed.Contains(DecisionKind.Accept) ? new Accept() : new Approve("looks good");
            Decided.Add(answer.Kind);
            return Task.FromResult(answer);
        }
    }

    /// <summary>
    /// A chat model that replies with tool calls. Its first reply to an intent asks the person one question; its next reply
    /// writes the files the <see cref="ScriptedAgent"/> would write (and runs the task's test for implement work); once it
    /// sees tool results it says it is done. With no agent it never calls a tool.
    /// </summary>
    private sealed class ScriptedModel(ScriptedAgent? agent) : IChatClient
    {
        private int _calls;

        public AgentWork? Work { get; set; }

        public List<string> ToolsCalled { get; } = [];

        public List<string> Refusals { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages.ToList();
            foreach (var result in history.SelectMany(m => m.Contents).OfType<FunctionResultContent>())
            {
                if (result.Result?.ToString() is { } text && text.StartsWith("refused:", StringComparison.Ordinal))
                {
                    Refusals.Add(text);
                }
            }

            var work = Work;
            var toolResults = history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Count();
            if (agent is null || work is null)
            {
                return Reply(new TextContent("I have nothing to do."));
            }

            if (work.Role == Role.Intent && toolResults == 0)
            {
                return Call(ScopedTools.AskPerson, new() { ["question"] = "Should a shouted greeting end with `!`? (default: yes)" });
            }

            var asked = work.Role == Role.Intent ? 1 : 0;
            if (toolResults > asked)
            {
                return Reply(new TextContent("Done."));
            }

            var calls = agent.FilesFor(work)
                .Select(f => (AIContent)NewCall(ScopedTools.WriteFile, new() { ["path"] = f.Path, ["content"] = f.Text }))
                .ToList();
            if (work.Role == Role.Implement && work.Scope.MayRun.Count > 0)
            {
                calls.Add(NewCall(ScopedTools.RunCommand, new() { ["command"] = work.Scope.MayRun[^1].ToArray() }));
            }

            return Reply([.. calls]);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private Task<ChatResponse> Call(string name, Dictionary<string, object?> args) => Reply(NewCall(name, args));

        private FunctionCallContent NewCall(string name, Dictionary<string, object?> args)
        {
            ToolsCalled.Add(name);
            return new FunctionCallContent($"call-{++_calls}", name, args);
        }

        private static Task<ChatResponse> Reply(params AIContent[] contents) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, contents)));
    }

    /// <summary>Wraps <see cref="ChatClientRoleAgents"/> so the scripted model knows which work it is doing.</summary>
    private sealed class TrackingRoleAgents(ScriptedModel model) : IRoleAgents
    {
        private readonly ChatClientRoleAgents _inner = new(model);

        public AIAgent Create(AgentWork work, IList<AITool> tools)
        {
            model.Work = work;
            return _inner.Create(work, tools);
        }
    }
}
