using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

/// <summary>Appends schema-valid Layout 2 JSONC build-log entries to <c>build/&lt;repo&gt;.md</c>.</summary>
public sealed partial class BuildLogWriter(IFileSystem fileSystem)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IFileSystem _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public static string RedValueFor(WorkflowTask task) =>
        task.Test is { Length: > 0 } ? "observed" : "not observed: no testable behavior";

    public string AppendEntry(
        string changeFolder,
        string repoName,
        string taskId,
        IReadOnlyList<string> commits,
        string red)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentException.ThrowIfNullOrWhiteSpace(red);

        var buildDir = Path.Combine(changeFolder, "build");
        _fs.CreateDirectory(buildDir);
        var filePath = Path.Combine(buildDir, $"{repoName}.md");

        var existing = _fs.FileExists(filePath)
            ? _fs.ReadAllText(filePath)
            : InitialDocument(repoName);

        if (HasTaskHeading(existing, taskId))
        {
            return filePath;
        }

        var entryBlock = FormatEntry(taskId, commits, red);
        var sb = new StringBuilder(existing.TrimEnd());
        sb.Append("\n\n");
        sb.Append(entryBlock);
        sb.Append('\n');
        _fs.WriteAllTextAtomic(filePath, sb.ToString());
        return filePath;
    }

    private static string InitialDocument(string repoName) =>
        $"```jsonc\n{{ \"layout\": 2 }}\n```\n\n# Build log: {repoName}\n";

    private static string FormatEntry(string taskId, IReadOnlyList<string> commits, string red)
    {
        var payload = new Dictionary<string, object?>
        {
            ["task"] = taskId,
            ["commits"] = commits,
            ["red"] = red,
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions).Replace("\r\n", "\n", StringComparison.Ordinal);
        return $"### {taskId}\n\n```jsonc\n{json}\n```";
    }

    private static bool HasTaskHeading(string content, string taskId) =>
        Regex.IsMatch(content, $@"^### {Regex.Escape(taskId)}\s*$", RegexOptions.Multiline, TimeSpan.FromSeconds(1));
}
