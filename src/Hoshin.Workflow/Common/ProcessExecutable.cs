namespace Hoshin.Workflow.Common;

/// <summary>
/// Resolves relative executable paths (containing a directory separator) against a command's
/// working directory rather than the parent process's current directory.
/// </summary>
internal static class ProcessExecutable
{
    public static string Resolve(string executable, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || Path.IsPathRooted(executable))
        {
            return executable;
        }

        if (executable.Contains('/') || executable.Contains('\\'))
        {
            return Path.GetFullPath(executable, workingDirectory);
        }

        return executable;
    }
}
