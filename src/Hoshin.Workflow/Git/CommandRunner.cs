using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Hoshin.Workflow.Common;

namespace Hoshin.Workflow.Git;

public sealed record CommandRequest(
    string WorkingDirectory,
    IReadOnlyList<string> Argv,
    string? StandardInput = null,
    Dictionary<string, string>? ExtraEnvironment = null,
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

/// <summary>Default <see cref="ICommandRunner"/> backed by <see cref="Process"/> with UTF-8 I/O, bounded drain grace, and process-tree kill on timeout.</summary>
public sealed class ProcessCommandRunner : ICommandRunner
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan DrainGracePeriod = TimeSpan.FromSeconds(2);

    public CommandOutcome Run(CommandRequest request) =>
        RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Argv.Count == 0)
        {
            return new CommandOutcome(false, false, null, "", "", null, "empty command");
        }

        var psi = BuildStartInfo(request);
        using var process = new Process { StartInfo = psi };
        if (!TryStart(process, request.Argv[0], out var startError))
        {
            return new CommandOutcome(false, false, null, "", "", null, startError);
        }

        using var drainCts = new CancellationTokenSource();
        var stdoutCollector = StreamCollector.Start(process.StandardOutput.BaseStream, drainCts.Token);
        var stderrCollector = StreamCollector.Start(process.StandardError.BaseStream, drainCts.Token);

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout.HasValue)
        {
            waitCts.CancelAfter(request.Timeout.Value);
        }

        _ = WriteStandardInputAsync(process.StandardInput, request.StandardInput, waitCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            if (cancellationToken.IsCancellationRequested)
            {
                await drainCts.CancelAsync().ConfigureAwait(false);
                throw;
            }

            timedOut = true;
            await WaitForExitBestEffortAsync(process, DrainGracePeriod).ConfigureAwait(false);
        }

        drainCts.CancelAfter(DrainGracePeriod);
        await WaitWithGraceAsync(Task.WhenAll(stdoutCollector.Completion, stderrCollector.Completion), DrainGracePeriod).ConfigureAwait(false);
        await drainCts.CancelAsync().ConfigureAwait(false);

        var rawOut = stdoutCollector.Snapshot();
        var rawErr = stderrCollector.Snapshot();
        var stdout = DecodeUtf8(rawOut);
        var stderr = DecodeUtf8(rawErr);

        return new CommandOutcome(
            Started: true,
            TimedOut: timedOut,
            ExitCode: timedOut ? null : TryGetExitCode(process),
            Stdout: stdout,
            Stderr: stderr,
            RawStdout: request.CaptureRawBytes ? rawOut : null,
            StartError: null);
    }

    private static ProcessStartInfo BuildStartInfo(CommandRequest request)
    {
        var executable = ProcessExecutable.Resolve(request.Argv[0], request.WorkingDirectory);
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
        };

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

        return psi;
    }

    private static bool TryStart(Process process, string argv0, out string? error)
    {
        try
        {
            if (!process.Start())
            {
                error = $"cannot start {argv0}";
                return false;
            }

            error = null;
            return true;
        }
        catch (Win32Exception e)
        {
            error = e.NativeErrorCode == 2 ? "ENOENT" : e.Message;
            return false;
        }
        catch (InvalidOperationException e)
        {
            error = e.Message;
            return false;
        }
        catch (IOException e)
        {
            error = e.Message;
            return false;
        }
    }

    private static Task WriteStandardInputAsync(StreamWriter stdin, string? content, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                if (content is not null)
                {
                    var bytes = Utf8NoBom.GetBytes(content);
                    await stdin.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stdin.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                stdin.Close();
            }
            catch (IOException)
            {
                // Child exited before reading stdin.
            }
            catch (ObjectDisposedException)
            {
                // Process disposed during timeout or cancellation.
            }
            catch (OperationCanceledException)
            {
                // Timeout or caller cancellation interrupted stdin write.
            }
        }, CancellationToken.None);

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited before Kill.
        }
        catch (Win32Exception)
        {
            // Process handle closed or already terminating.
        }
    }

    private static async Task WaitForExitBestEffortAsync(Process process, TimeSpan grace)
    {
        using var cts = new CancellationTokenSource(grace);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Killed child did not exit within grace period.
        }
        catch (InvalidOperationException)
        {
            // Process already disposed or exited.
        }
    }

    private static async Task WaitWithGraceAsync(Task task, TimeSpan grace)
    {
        try
        {
            await task.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Drain grace period expired (e.g., background grandchild kept pipe open).
        }
        catch (OperationCanceledException)
        {
            // Drain cancelled.
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private sealed class StreamCollector
    {
        private readonly List<byte[]> _chunks = [];
        private int _totalLength;
        private readonly Lock _sync = new();

        public Task Completion { get; private set; } = Task.CompletedTask;

        public static StreamCollector Start(Stream stream, CancellationToken cancellationToken)
        {
            var collector = new StreamCollector();
            collector.Completion = Task.Run(() => collector.ReadLoopAsync(stream, cancellationToken), CancellationToken.None);
            return collector;
        }

        public byte[] Snapshot()
        {
            lock (_sync)
            {
                var result = new byte[_totalLength];
                var offset = 0;
                foreach (var chunk in _chunks)
                {
                    Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                    offset += chunk.Length;
                }

                return result;
            }
        }

        private async Task ReadLoopAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    var copy = new byte[read];
                    Buffer.BlockCopy(buffer, 0, copy, 0, read);
                    lock (_sync)
                    {
                        _chunks.Add(copy);
                        _totalLength += read;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Drain cancelled after grace period; retain bytes already collected.
            }
            catch (IOException)
            {
                // Pipe closed during teardown.
            }
            catch (ObjectDisposedException)
            {
                // Process disposed during teardown.
            }
        }
    }
}
