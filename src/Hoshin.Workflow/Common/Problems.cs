namespace Hoshin.Workflow.Common;

/// <summary>Thrown when the program cannot run at all (CLI exit code 2), as opposed to reporting problems (exit code 1).</summary>
public sealed class CouldNotRunException(string message) : Exception(message);

/// <summary>Standard CLI exit codes defined by GRAMMAR.md.</summary>
public static class ExitCodes
{
    public const int Pass = 0;
    public const int Problems = 1;
    public const int CouldNotRun = 2;
}

/// <summary>One diagnostic problem in a document or configuration file.</summary>
public sealed record Problem(string File, int Line, string Message);

/// <summary>Formatting and deterministic sorting for <see cref="Problem"/> lists.</summary>
public static class ProblemFormatter
{
    public static string Format(Problem p) => $"{p.File}:{p.Line}: {p.Message}";

    public static List<Problem> Sort(IEnumerable<Problem> problems) =>
        problems
            .OrderBy(p => p.File, StringComparer.Ordinal)
            .ThenBy(p => p.Line)
            .ThenBy(p => p.Message, StringComparer.Ordinal)
            .ToList();
}
