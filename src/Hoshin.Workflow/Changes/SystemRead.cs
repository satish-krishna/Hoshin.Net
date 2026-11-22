using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Changes;

/// <summary>
/// Batched Git reader for one system in one repository: reads tree blobs, blob contents via <c>cat-file --batch</c>,
/// and object existence via <c>cat-file --batch-check</c> in a fixed number of Git processes.
/// </summary>
public sealed class SystemRead
{
    private static readonly Regex LsTreeEntry = new(@"^\d+ blob ([0-9a-f]+)\t(.+)$", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IGitClient _git;
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _objects = new(StringComparer.Ordinal);
    private HashSet<string>? _reportedCache;

    public SystemRead(string top, string system, IGitClient? git = null)
    {
        _git = git ?? ProcessGitClient.Default;
        Top = top;
        System = system;
        HasHead = _git.CommitOf(top, "HEAD") is not null;
        if (HasHead)
        {
            var raw = _git.Must(top, ["ls-tree", "-r", "-z", "HEAD", "--", $"intent/{system}/"], "git ls-tree");
            foreach (var entry in raw.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var m = LsTreeEntry.Match(entry);
                if (m.Success)
                {
                    HeadBlobs[m.Groups[2].Value] = m.Groups[1].Value;
                }
            }
        }
    }

    public string Top { get; }

    public string System { get; }

    public bool HasHead { get; }

    public Dictionary<string, string> HeadBlobs { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Reported => _reportedCache ??= _git.ReportedPaths(Top, [$"intent/{System}/"]);

    public void LoadTexts(IEnumerable<string> paths)
    {
        var todo = paths.Distinct(StringComparer.Ordinal)
            .Where(p => HeadBlobs.ContainsKey(p) && !_texts.ContainsKey(p))
            .ToList();
        if (todo.Count == 0)
        {
            return;
        }

        var input = string.Join('\n', todo.Select(p => HeadBlobs[p])) + "\n";
        var raw = _git.RunBytes(Top, ["cat-file", "--batch"], input);
        var texts = ParseBatch(raw, todo.Count);
        for (var i = 0; i < todo.Count; i++)
        {
            if (texts[i] is not null)
            {
                _texts[todo[i]] = texts[i]!;
            }
        }
    }

    public string? HeadText(string path)
    {
        if (!HeadBlobs.ContainsKey(path))
        {
            return null;
        }

        LoadTexts([path]);
        return _texts.TryGetValue(path, out var text) ? text : null;
    }

    public List<string> HeadPaths(string dir) =>
        HeadBlobs.Keys.Where(p => p.StartsWith(dir, StringComparison.Ordinal)).ToList();

    public void Resolve(IEnumerable<string> names)
    {
        var todo = new List<string>();
        foreach (var n in names.Distinct(StringComparer.Ordinal))
        {
            if (_objects.ContainsKey(n))
            {
                continue;
            }

            if (n.Contains('\n'))
            {
                _objects[n] = null;
            }
            else
            {
                todo.Add(n);
            }
        }

        if (todo.Count == 0)
        {
            return;
        }

        var input = string.Join('\n', todo) + "\n";
        var raw = _git.RunBytes(Top, ["cat-file", "--batch-check"], input);
        var lines = Encoding.UTF8.GetString(raw).Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < todo.Count; i++)
        {
            var line = i < lines.Length ? lines[i] : "";
            var missing = line.Length == 0 || line.EndsWith(" missing", StringComparison.Ordinal) || line.EndsWith(" ambiguous", StringComparison.Ordinal);
            _objects[todo[i]] = missing ? null : line.Split(' ')[0];
        }
    }

    public string? ObjectOf(string name)
    {
        Resolve([name]);
        return _objects.TryGetValue(name, out var obj) ? obj : null;
    }

    private static List<string?> ParseBatch(byte[] output, int count)
    {
        var texts = new List<string?>(count);
        var at = 0;
        for (var i = 0; i < count; i++)
        {
            var nl = Array.IndexOf(output, (byte)'\n', at);
            if (nl < 0)
            {
                throw new CouldNotRunException("git cat-file --batch ended before every name was answered");
            }

            var header = Encoding.UTF8.GetString(output, at, nl - at).TrimEnd('\r');
            at = nl + 1;
            if (header.EndsWith(" missing", StringComparison.Ordinal) || header.EndsWith(" ambiguous", StringComparison.Ordinal))
            {
                texts.Add(null);
                continue;
            }

            var parts = header.Split(' ');
            var size = int.Parse(parts[2], CultureInfo.InvariantCulture);
            texts.Add(Encoding.UTF8.GetString(output, at, size));
            at += size + 1;
        }

        return texts;
    }
}
