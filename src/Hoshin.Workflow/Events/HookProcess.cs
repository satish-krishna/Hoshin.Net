using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Events;

public sealed record HookProcessResult(string Outcome, int? ExitCode, string Output, List<string>? Ids);

/// <summary>Executes a single hook or test process with timeout, process-tree kill, tailed output, and failure-ID parsing.</summary>
public sealed class HookProcessRunner(ICommandRunner runner)
{
    private const int KeepLines = 200;

    private readonly ICommandRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));

    public static List<string>? ReadFailureIds(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        var ids = File.ReadAllText(file)
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ids.Count > 0 ? ids : null;
    }

    public static string Tail(string text, int maxLines = KeepLines)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.EndsWith('\n'))
        {
            normalized = normalized[..^1];
        }

        var all = normalized.Split('\n');
        if (all.Length <= maxLines)
        {
            return string.Join('\n', all);
        }

        var omitted = all.Length - maxLines;
        return string.Join('\n', new[] { $"… {omitted} earlier lines left out" }.Concat(all.TakeLast(maxLines)));
    }

    public async Task<HookProcessResult> RunAsync(
        IReadOnlyList<string> argv,
        string cwd,
        IReadOnlyDictionary<string, string>? env,
        string input,
        int timeoutSeconds,
        string? failuresFile = null)
    {
        var outcome = await _runner.RunAsync(new CommandRequest(
            cwd,
            argv,
            StandardInput: input,
            ExtraEnvironment: env is null ? null : new Dictionary<string, string>(env, StringComparer.Ordinal),
            Timeout: TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);

        if (!outcome.Started)
        {
            return new HookProcessResult("could-not-run", null, $"cannot start {argv[0]}: {outcome.StartError}", null);
        }

        var combined = outcome.Stdout + outcome.Stderr;
        if (outcome.TimedOut)
        {
            return new HookProcessResult(
                "could-not-run",
                null,
                Tail(combined + $"\nstopped: ran past its time limit of {timeoutSeconds} seconds"),
                null);
        }

        var failed = outcome.ExitCode != 0;
        var ids = failed && failuresFile is not null ? ReadFailureIds(failuresFile) : null;
        return new HookProcessResult(failed ? "failed" : "passed", outcome.ExitCode, Tail(combined), ids);
    }
}
