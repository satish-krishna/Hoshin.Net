using System.Diagnostics;
using System.Text;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Engine;

public sealed record AllowListedCommandResult(int ExitCode, string Output, bool TimedOut, TimeSpan Elapsed)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    public string Tail(int lines = 40)
    {
        var all = Output.Replace("\r\n", "\n").Split('\n');
        return string.Join('\n', all.TakeLast(lines)).Trim();
    }
}

/// <summary>Abstraction for running allow-listed commands with no shell and stripped secrets.</summary>
public interface IAllowListedCommandRunner
{
    AllowListedCommandResult Run(string workingDirectory, IReadOnlyList<string> argv, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? extraEnv = null);
}

/// <summary>
/// Executes child processes directly (no shell) with an environment stripped down to a safe OS allow-list,
/// ensuring tests, Red checks, baselines, and <c>build.finish</c> hooks never see model keys or GitHub tokens.
/// </summary>
public sealed class AllowListedCommandRunner : IAllowListedCommandRunner
{
    public static AllowListedCommandRunner Default { get; } = new();

    public static readonly IReadOnlyList<string> AllowedEnvironmentVariables =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "TMPDIR", "USER", "NODE_PATH",
        "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO", "NUGET_PACKAGES",
        // Safe git identity/config variables for deterministic commits (no tokens/secrets).
        "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL",
        "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM",
        // Safe Windows system variables required by git, dotnet, and node (no tokens/secrets).
        "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "OS", "ProgramFiles", "ProgramFiles(x86)", "ProgramData",
        "APPDATA", "LOCALAPPDATA",
    ];

    public AllowListedCommandResult Run(
        string workingDirectory,
        IReadOnlyList<string> argv,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]))
        {
            return new AllowListedCommandResult(127, "command is empty", false, TimeSpan.Zero);
        }

        var start = Stopwatch.StartNew();
        var executable = ProcessExecutable.Resolve(argv[0], workingDirectory);
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        for (var i = 1; i < argv.Count; i++)
        {
            psi.ArgumentList.Add(argv[i]);
        }

        psi.Environment.Clear();
        foreach (var name in AllowedEnvironmentVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                psi.Environment[name] = value;
            }
        }

        if (extraEnv is not null)
        {
            foreach (var (k, v) in extraEnv)
            {
                psi.Environment[k] = v;
            }
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (output)
                {
                    output.AppendLine(e.Data);
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            return new AllowListedCommandResult(127, $"could not start {argv[0]}: {e.Message}", false, start.Elapsed);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var timedOut = !process.WaitForExit((int)Math.Min(effectiveTimeout.TotalMilliseconds, int.MaxValue));
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort process tree termination.
            }

            process.WaitForExit();
        }
        else
        {
            process.WaitForExit();
        }

        lock (output)
        {
            return new AllowListedCommandResult(timedOut ? -1 : process.ExitCode, output.ToString(), timedOut, start.Elapsed);
        }
    }
}
