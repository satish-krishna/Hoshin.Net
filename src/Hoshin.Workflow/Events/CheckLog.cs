using System.Text.Json;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Events;

/// <summary>Best-effort append-only writer for <c>checks.jsonl</c> beside a change's ledger.</summary>
public sealed class CheckLog
{
    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly string _event;
    private readonly string _repo;
    private readonly List<string> _problems;
    private readonly TimeProvider _time;
    private bool _reported;

    public CheckLog(string file, string eventName, string repo, List<string> problems, TimeProvider timeProvider)
    {
        RunId = Guid.NewGuid().ToString();
        _file = file;
        _event = eventName;
        _repo = repo;
        _problems = problems;
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string RunId { get; }

    public static string ChecksFile(IGitWorktrees git, string master, string system, string change) =>
        Path.Combine(git.GitPath(master, Path.Combine("hoshin", system, change), common: true), "checks.jsonl");

    public static bool IsCheck(string eventName, HookDefinition hook) =>
        eventName == "build.finish" && hook.When == "before" && hook.Blocking;

    public void Start(string check, string phase, int timeout) =>
        Append(new Dictionary<string, object?>
        {
            ["run"] = RunId,
            ["event"] = _event,
            ["repo"] = _repo,
            ["at"] = _time.GetUtcNow().ToString("O"),
            ["type"] = "check-start",
            ["check"] = check,
            ["phase"] = phase,
            ["pid"] = Environment.ProcessId,
            ["timeout"] = timeout,
        });

    public void End(string check, string phase, HookRecord record, bool? blocks) =>
        Append(new Dictionary<string, object?>
        {
            ["run"] = RunId,
            ["event"] = _event,
            ["repo"] = _repo,
            ["at"] = _time.GetUtcNow().ToString("O"),
            ["type"] = "check-end",
            ["check"] = check,
            ["phase"] = phase,
            ["outcome"] = record.Outcome,
            ["ids"] = record.Ids,
            ["blocks"] = blocks,
        });

    public async Task<HookRecord> LoggedAsync(string check, string phase, int timeout, bool? blocksOnThrow, Func<Task<HookRecord>> run)
    {
        Start(check, phase, timeout);
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch
        {
            End(check, phase, new HookRecord(check, "before", "could-not-run", null, "", null), blocksOnThrow);
            throw;
        }
    }

    private void Append(Dictionary<string, object?> payload)
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(_file, JsonSerializer.Serialize(payload, CompactOptions) + "\n");
        }
        catch (Exception e)
        {
            if (_reported)
            {
                return;
            }

            _reported = true;
            var problem = $"check events could not be written to {_file}: {e.Message}";
            if (!_problems.Contains(problem))
            {
                _problems.Add(problem);
            }
        }
    }
}
