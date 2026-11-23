using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class RelativeExecutableTests
{
    private static (string ScriptPath, string RelativeArgv0) CreateExecutableScript(string root)
    {
        var binDir = Path.Combine(root, "bin");
        Directory.CreateDirectory(binDir);

        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(binDir, "hello.cmd");
            File.WriteAllText(path, "@echo hello-from-relative\r\n");
            return (path, "bin/hello.cmd");
        }
        else
        {
            var path = Path.Combine(binDir, "hello.sh");
            File.WriteAllText(path, "#!/bin/sh\necho hello-from-relative\n");
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return (path, "bin/hello.sh");
        }
    }

    [Fact]
    public void AllowListedCommandRunner_ResolvesRelativeExecutableAgainstWorkingDirectory()
    {
        using var tmp = new TempDir("hoshin-t16-allow-");
        var (_, relArgv0) = CreateExecutableScript(tmp.Path);

        // Current process working directory is NOT tmp.Path.
        Assert.NotEqual(Path.GetFullPath(tmp.Path), Path.GetFullPath(Environment.CurrentDirectory));

        var res = TestServices.AllowListedRunner.Run(tmp.Path, [relArgv0]);
        Assert.True(res.Ok, $"Expected Ok, got exit {res.ExitCode}: {res.Output}");
        Assert.Contains("hello-from-relative", res.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessCommandRunner_ResolvesRelativeExecutableAgainstWorkingDirectory()
    {
        using var tmp = new TempDir("hoshin-t16-proc-");
        var (_, relArgv0) = CreateExecutableScript(tmp.Path);

        Assert.NotEqual(Path.GetFullPath(tmp.Path), Path.GetFullPath(Environment.CurrentDirectory));

        var res = TestServices.CommandRunner.Run(new CommandRequest(tmp.Path, [relArgv0]));
        Assert.True(res.Ok, $"Expected Ok, got started={res.Started} exit={res.ExitCode} err={res.StartError} stderr={res.Stderr}");
        Assert.Contains("hello-from-relative", res.Stdout, StringComparison.Ordinal);
    }
}
