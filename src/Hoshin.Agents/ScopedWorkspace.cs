using System.Text;
using Hoshin.Workflow.Driver;
using Hoshin.Workflow.Engine;
using Microsoft.Extensions.FileSystemGlobbing;

namespace Hoshin.Agents;

/// <summary>
/// The file and command operations an agent may perform for one piece of work, enforced from its <see cref="WorkScope"/>:
/// reads anywhere in the working directory except <c>.git</c>, writes only where <see cref="WorkScope.MayWrite"/> allows and
/// <see cref="WorkScope.MayNotWrite"/> does not forbid, and runs only the exact commands in <see cref="WorkScope.MayRun"/>.
/// Every refusal is returned as text that starts with <c>refused:</c>, so a model reads it as a tool result.
/// </summary>
public sealed class ScopedWorkspace
{
    private const int MaxOutputChars = 8000;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly WorkScope _scope;
    private readonly IAllowListedCommandRunner _runner;
    private readonly TimeSpan _commandTimeout;
    private readonly Matcher _writable;
    private readonly string _root;

    public ScopedWorkspace(WorkScope scope, IAllowListedCommandRunner runner, TimeSpan commandTimeout)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _commandTimeout = commandTimeout;
        _root = Path.GetFullPath(scope.WorkingDirectory);
        _writable = new Matcher(PathComparison);
        foreach (var include in scope.MayWrite)
        {
            _writable.AddInclude(include);
        }

        foreach (var exclude in scope.MayNotWrite)
        {
            _writable.AddExclude(exclude);
        }
    }

    public WorkScope Scope => _scope;

    /// <summary>Reads a file, by path relative to the working directory.</summary>
    public string ReadFile(string path)
    {
        if (Resolve(path) is not { } full)
        {
            return Refused($"{path} is outside the working directory");
        }

        if (IsGitPath(full))
        {
            return Refused("git internals are not readable");
        }

        return File.Exists(full) ? File.ReadAllText(full) : Refused($"{path} does not exist");
    }

    /// <summary>Lists the files under a directory, relative to the working directory, skipping <c>.git</c>.</summary>
    public string ListFiles(string directory)
    {
        if (Resolve(string.IsNullOrWhiteSpace(directory) ? "." : directory) is not { } full || !Directory.Exists(full))
        {
            return Refused($"{directory} is not a directory in the working directory");
        }

        var files = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
            .Where(f => !IsGitPath(f))
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .Take(500);
        return string.Join('\n', files);
    }

    /// <summary>Creates or replaces a file the work may write.</summary>
    public string WriteFile(string path, string content)
    {
        if (CheckWritable(path, out var full) is { } refusal)
        {
            return refusal;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content ?? "");
        return $"wrote {Relative(full)}";
    }

    /// <summary>Deletes a file the work may write.</summary>
    public string DeleteFile(string path)
    {
        if (CheckWritable(path, out var full) is { } refusal)
        {
            return refusal;
        }

        if (!File.Exists(full))
        {
            return Refused($"{path} does not exist");
        }

        File.Delete(full);
        return $"deleted {Relative(full)}";
    }

    /// <summary>Runs one of the work's allowed commands, given as its exact argument list.</summary>
    public string RunCommand(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var allowed = _scope.MayRun.FirstOrDefault(c => c.SequenceEqual(command, StringComparer.Ordinal));
        if (allowed is null)
        {
            var list = _scope.MayRun.Count == 0 ? "none" : string.Join("; ", _scope.MayRun.Select(c => string.Join(' ', c)));
            return Refused($"`{string.Join(' ', command)}` is not a command this work may run (allowed: {list})");
        }

        var result = _runner.Run(_root, allowed, _commandTimeout);
        var output = result.Output.Length > MaxOutputChars ? "…" + result.Output[^MaxOutputChars..] : result.Output;
        var status = result.TimedOut ? "timed out" : $"exit {result.ExitCode}";
        return new StringBuilder().Append(status).Append('\n').Append(output).ToString();
    }

    /// <summary>Whether the work may write <paramref name="path"/> (relative to the working directory).</summary>
    public bool MayWrite(string path) => CheckWritable(path, out _) is null;

    private string? CheckWritable(string path, out string full)
    {
        full = "";
        if (Resolve(path) is not { } resolved)
        {
            return Refused($"{path} is outside the working directory");
        }

        full = resolved;
        var relative = Relative(resolved);
        return IsGitPath(resolved) || !_writable.Match(relative).HasMatches
            ? Refused($"{relative} is not a file this work may write (may write: {string.Join(", ", _scope.MayWrite)}; may not write: {string.Join(", ", _scope.MayNotWrite)})")
            : null;
    }

    private string? Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_root, path));
        return full.Equals(_root, PathComparison) || full.StartsWith(_root + Path.DirectorySeparatorChar, PathComparison)
            ? full
            : null;
    }

    private bool IsGitPath(string full)
    {
        var relative = Relative(full);
        return relative.Equals(".git", PathComparison) || relative.StartsWith(".git/", PathComparison);
    }

    private string Relative(string full) => Path.GetRelativePath(_root, full).Replace('\\', '/');

    private static string Refused(string why) => $"refused: {why}";
}
