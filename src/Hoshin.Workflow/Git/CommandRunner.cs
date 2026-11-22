using System.Diagnostics;
using System.Text;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Git;

public sealed record CommandRequest(
    string WorkingDirectory,
    IReadOnlyList<string> Argv,
    string? StandardInput = null,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
    TimeSpan? Timeout = null,
    bool CaptureRawBytes = false);

public sealed record CommandOutcome(
    bool Started,
    bool TimedOut,
    int? ExitCode,
    string Stdout,
    string Stderr,
    byte[]? RawStdout,
    string? StartError)
{
    public bool Ok => Started && !TimedOut && ExitCode == 0;
}

/// <summary>Executes external processes without a shell. Replaceable for unit testing or alternative hosts.</summary>
public interface ICommandRunner
{
    CommandOutcome Run(CommandRequest request);
    Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ICommandRunner"/> backed by <see cref="Process"/> with UTF-8 I/O and process-tree kill on timeout.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    public static ProcessCommandRunner Default { get; } = new();

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public CommandOutcome Run(CommandRequest request) => RunCore(request);

    public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunCore(request), cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1502:Avoid excessive complexity", Justification = "baselined: existing method")]
    private static CommandOutcome RunCore(CommandRequest request)
    {
        if (request.Argv.Count == 0)
        {
            return new CommandOutcome(false, false, null, "", "", null, "empty command");
        }

        var executable = ProcessExecutable.Resolve(request.Argv[0], request.WorkingDirectory);
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = Utf8NoBom,
        };

        if (!request.CaptureRawBytes)
        {
            psi.StandardOutputEncoding = Encoding.UTF8;
        }

        for (var i = 1; i < request.Argv.Count; i++)
        {
            psi.ArgumentList.Add(request.Argv[i]);
        }

        if (request.ExtraEnvironment is not null)
        {
            foreach (var (k, v) in request.ExtraEnvironment)
            {
                psi.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new CommandOutcome(false, false, null, "", "", null, $"cannot start {request.Argv[0]}");
            }
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            var code = e.NativeErrorCode == 2 ? "ENOENT" : e.Message;
            return new CommandOutcome(false, false, null, "", "", null, code);
        }
        catch (Exception e)
        {
            return new CommandOutcome(false, false, null, "", "", null, e.Message);
        }

        Task<byte[]?>? rawOutTask = null;
        Task<string>? textOutTask = null;
        if (request.CaptureRawBytes)
        {
            rawOutTask = Task.Run(() =>
            {
                using var ms = new MemoryStream();
                process.StandardOutput.BaseStream.CopyTo(ms);
                return (byte[]?)ms.ToArray();
            });
        }
        else
        {
            textOutTask = process.StandardOutput.ReadToEndAsync();
        }

        var errTask = process.StandardError.ReadToEndAsync();

        try
        {
            if (request.StandardInput is not null)
            {
                process.StandardInput.Write(request.StandardInput);
            }

            process.StandardInput.Close();
        }
        catch
        {
            // Ignore broken pipe if child exited before reading stdin.
        }

        var timeoutMs = request.Timeout.HasValue
            ? (int)Math.Min(request.Timeout.Value.TotalMilliseconds, int.MaxValue)
            : Timeout.Infinite;

        var exited = process.WaitForExit(timeoutMs);
        var timedOut = !exited;
        if (timedOut)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Process may have exited between WaitForExit and Kill.
            }

            process.WaitForExit();
        }
        else
        {
            process.WaitForExit();
        }

        var rawOut = rawOutTask?.GetAwaiter().GetResult();
        var stdout = textOutTask is not null ? textOutTask.GetAwaiter().GetResult() : (rawOut is not null ? Encoding.UTF8.GetString(rawOut) : "");
        var stderr = errTask.GetAwaiter().GetResult();

        return new CommandOutcome(
            Started: true,
            TimedOut: timedOut,
            ExitCode: timedOut ? null : process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            RawStdout: rawOut,
            StartError: null);
    }
}
