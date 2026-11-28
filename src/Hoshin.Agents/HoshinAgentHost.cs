using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Hoshin.Agents;

/// <summary>
/// The person a change needs: answers interview questions and makes the decisions only a person may make (accept,
/// approve, decline, reject, reopen). Implement it over whatever channel reaches them: a console, a chat, a web form.
/// </summary>
public interface IHumanChannel
{
    /// <summary>Asks one question during an interview and returns the answer.</summary>
    Task<string> AskAsync(string question, CancellationToken cancellationToken);

    /// <summary>Returns the person's decision; it must be one of <see cref="HumanDecision.Allowed"/>.</summary>
    Task<Decision> DecideAsync(HumanDecision decision, CancellationToken cancellationToken);

    /// <summary>Who the person is, recorded with every decision.</summary>
    DecidedBy Identity { get; }
}

/// <summary>Creates the agent that does one piece of work, with exactly the tools given.</summary>
public interface IRoleAgents
{
    AIAgent Create(AgentWork work, IList<AITool> tools);
}

/// <summary>Creates a <see cref="ChatClientAgent"/> per piece of work over one chat client.</summary>
public sealed class ChatClientRoleAgents(IChatClient chatClient) : IRoleAgents
{
    private readonly IChatClient _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));

    public AIAgent Create(AgentWork work, IList<AITool> tools)
    {
        ArgumentNullException.ThrowIfNull(work);
        return new ChatClientAgent(
            _chatClient,
            instructions: work.Instructions,
            name: $"hoshin-{work.Role.ToString().ToLowerInvariant()}",
            description: $"Does the {work.Role} work of a hoshin change.",
            tools: tools);
    }
}

/// <summary>Limits for <see cref="HoshinAgentHost"/>.</summary>
/// <param name="MaxTurns">The most turns one <see cref="HoshinAgentHost.RunAsync"/> plays before it stops.</param>
/// <param name="MaxTriesPerWork">How many times the same work is handed to an agent before the host stops.</param>
/// <param name="CommandTimeout">The timeout for each command an agent runs.</param>
public sealed record HoshinAgentHostOptions(int MaxTurns = 200, int MaxTriesPerWork = 4, TimeSpan? CommandTimeout = null);

/// <summary>
/// Runs a hoshin change with Microsoft Agent Framework agents. Each piece of work goes to a fresh agent from
/// <see cref="IRoleAgents"/> whose only tools are the ones its <see cref="WorkScope"/> allows; decisions go to the
/// <see cref="IHumanChannel"/>. Agents get no git, commit, push or decision tool: the driver does all of that.
/// </summary>
public sealed class HoshinAgentHost(
    IHoshinDriver driver,
    IRoleAgents agents,
    IHumanChannel human,
    IAllowListedCommandRunner commandRunner,
    HoshinAgentHostOptions options)
{
    private readonly IHoshinDriver _driver = driver ?? throw new ArgumentNullException(nameof(driver));
    private readonly IRoleAgents _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    private readonly IHumanChannel _human = human ?? throw new ArgumentNullException(nameof(human));
    private readonly IAllowListedCommandRunner _runner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    private readonly HoshinAgentHostOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Raised for every turn the driver returns, for progress display.</summary>
    public event EventHandler<Turn>? TurnReceived;

    /// <summary>Starts a change and runs it to the end.</summary>
    public async Task<Finished> StartAndRunAsync(StartRequest request, CancellationToken cancellationToken = default)
    {
        var change = await _driver.StartAsync(request, cancellationToken).ConfigureAwait(false);
        return await RunAsync(change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="change"/> from wherever it stands until it finishes or a limit is reached.</summary>
    public async Task<Finished> RunAsync(ChangeRef change, CancellationToken cancellationToken = default)
    {
        var turn = await _driver.NextAsync(change, cancellationToken).ConfigureAwait(false);
        var tries = new Dictionary<(Role, string?, int), int>();
        for (var i = 0; i < _options.MaxTurns; i++)
        {
            TurnReceived?.Invoke(this, turn);
            switch (turn)
            {
                case Finished finished:
                    return finished;

                case AgentWork work:
                    var key = (work.Role, work.Id.Task, work.Attempt);
                    tries[key] = tries.GetValueOrDefault(key) + 1;
                    if (tries[key] > _options.MaxTriesPerWork)
                    {
                        return new Finished(false, $"The {work.Role} agent did not complete {work.Id.Task ?? work.Role.ToString()} in {_options.MaxTriesPerWork} tries: {string.Join("; ", work.Feedback)}", work.Happened);
                    }

                    await DoWorkAsync(work, cancellationToken).ConfigureAwait(false);
                    turn = await _driver.SubmitAsync(change, work.Id, cancellationToken).ConfigureAwait(false);
                    break;

                case HumanDecision decision:
                    var answer = await _human.DecideAsync(decision, cancellationToken).ConfigureAwait(false);
                    turn = await _driver.DecideAsync(change, answer, _human.Identity, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unknown turn {turn.GetType().Name}.");
            }
        }

        return new Finished(false, $"The change did not finish in {_options.MaxTurns} turns.", []);
    }

    private async Task DoWorkAsync(AgentWork work, CancellationToken cancellationToken)
    {
        var workspace = new ScopedWorkspace(work.Scope, _runner, _options.CommandTimeout ?? TimeSpan.FromMinutes(5));
        var ask = work.Role is Role.Intent or Role.Fix ? (Func<string, CancellationToken, Task<string>>)_human.AskAsync : null;
        var agent = _agents.Create(work, ScopedTools.For(workspace, ask));
        await agent.RunAsync(UserMessage(work), cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The message the agent receives: the brief, the tool rules, and why the last attempt was refused.</summary>
    public static string UserMessage(AgentWork work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var rules = $"Work only through your tools. You may write: {string.Join(", ", work.Scope.MayWrite)}. "
            + "Do not commit or run git: your work is checked and committed for you when you finish. "
            + "When the work is done, reply with one sentence saying what you did.";
        var feedback = work.Feedback.Count == 0 ? "" : $"\n\nYour previous attempt was not accepted:\n- {string.Join("\n- ", work.Feedback)}";
        return $"{work.Brief.Trim()}\n\n{rules}{feedback}";
    }
}

/// <summary>Builds a <see cref="HoshinAgentHost"/> over the library's composition.</summary>
public static class HoshinAgents
{
    /// <summary>A host whose agents are <see cref="ChatClientAgent"/>s over <paramref name="chatClient"/>.</summary>
    public static HoshinAgentHost CreateHost(
        Workflow.Hosting.HoshinServices services,
        IChatClient chatClient,
        IHumanChannel human,
        HoshinAgentHostOptions options,
        HoshinDriverOptions driverOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new HoshinAgentHost(services.CreateDriver(driverOptions), new ChatClientRoleAgents(chatClient), human, services.AllowListedRunner, options);
    }
}
