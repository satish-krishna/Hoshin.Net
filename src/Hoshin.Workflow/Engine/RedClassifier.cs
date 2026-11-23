using System.Text.RegularExpressions;

namespace Hoshin.Workflow.Engine;

public enum RedKind
{
    /// <summary>The test passed without production code — not valid Red.</summary>
    PassedUnexpectedly,

    /// <summary>The test timed out during Red observation — not valid Red.</summary>
    TimedOut,

    /// <summary>The test command could not run (exit 126/127, start failure, missing dependency, or worktree setup error) — not valid Red.</summary>
    CouldNotRun,

    /// <summary>The test file has a syntax or non-missing-symbol compile error — not valid Red.</summary>
    SyntaxError,

    /// <summary>Strong-language compilation failed strictly due to missing target symbols (e.g. CS0246, CS0103, CS1061, TS2304, Rust E0425).</summary>
    MissingSymbol,

    /// <summary>The test executed and failed an assertion or runtime expectation — valid Red.</summary>
    AssertionFailure,
}

public sealed record RedClassification(RedKind Kind, bool IsValidRed, string Summary);

/// <summary>
/// Classifies the outcome of running a task's test command against the isolated test-only commit on a detached worktree.
/// Enforces the strongly-typed Red rule: failing assertions and missing-target-symbol compile errors (<c>CS0246</c>,
/// <c>CS0103</c>, <c>CS1061</c>, <c>TS2304</c>, Rust <c>E0425</c>/<c>E0433</c>/<c>E0412</c>, relative/target module
/// not found, etc.) count as valid Red when resolved by Green; syntax errors and runner failures do not.
/// </summary>
public static partial class RedClassifier
{
    [GeneratedRegex(@"\b(CS1002|CS1003|CS1009|CS1010|CS1012|CS1026|CS1031|CS1513|CS1514|CS1519|CS1525|CS1526|CS1585|TS1005|TS1109|TS1128|TS1434)\b|IndentationError\s*:|\bsyntax error\s*:|\.go:\d+:\d+:\s*expected\b|\berror:\s*expected\b|\berror:\s*';'\s*expected\b", RegexOptions.IgnoreCase)]
    private static partial Regex HardSyntaxErrorRegex();

    [GeneratedRegex(@"SyntaxError\s*:")]
    private static partial Regex GenericSyntaxErrorRegex();

    [GeneratedRegex(@"\b(CS0246|CS0103|CS1061|CS0117|CS0234|CS0115|CS0535|CS1501|CS1729|CS7036|TS2304|TS2305|TS2339|TS2551|TS2554|TS2614)\b|error\[(E0425|E0433|E0412)\]:|does not provide an export named\b")]
    private static partial Regex MissingSymbolRegex();

    [GeneratedRegex(@"\berror\s+(CS\d{4}|TS\d{4})\b|\berror\[(E0\d{3})\]:")]
    private static partial Regex OtherCompileErrorRegex();

    [GeneratedRegex(@"Cannot find (?:module|package)\s+\\?['""]([^'""]+?)(?:\\)?['""]")]
    private static partial Regex ModuleSpecifierRegex();

    [GeneratedRegex(@"\burl\s*:\s*\\?'file:///([^']+?)(?:\\)?'")]
    private static partial Regex FileUrlSpecifierRegex();

    [GeneratedRegex(@"/+")]
    private static partial Regex MultiSlashRegex();

    [GeneratedRegex(@"\b(ERR_MODULE_NOT_FOUND|TS2307)\b|Cannot find (?:module|package)\b")]
    private static partial Regex ModuleNotFoundSignalRegex();

    public static RedClassification Classify(AllowListedCommandResult result, IReadOnlyList<string>? prodPaths = null)
    {
        if (result.TimedOut)
        {
            return new RedClassification(RedKind.TimedOut, false, "not observed: test timed out on the isolated test commit");
        }

        if (result.Ok)
        {
            return new RedClassification(RedKind.PassedUnexpectedly, false, "not observed: the test passed without the production changes");
        }

        var output = result.Output ?? "";
        if (result.ExitCode is 126 or 127 || output.Contains("could not start", StringComparison.OrdinalIgnoreCase))
        {
            return new RedClassification(RedKind.CouldNotRun, false, $"not observed: test command could not run (exit {result.ExitCode})");
        }

        if (TryClassifyModuleNotFound(output, prodPaths) is { } moduleResult)
        {
            return moduleResult;
        }

        if (HardSyntaxErrorRegex().Match(output) is { Success: true } hardMatch)
        {
            return new RedClassification(RedKind.SyntaxError, false, $"not observed: test failed with a syntax error ({hardMatch.Value})");
        }

        // Check missing target symbols before generic JS SyntaxError (Node ESM reports missing named exports as
        // "SyntaxError: The requested module ... does not provide an export named '...'").
        var missingMatches = MissingSymbolRegex().Matches(output).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        if (missingMatches.Count > 0)
        {
            return new RedClassification(
                RedKind.MissingSymbol,
                true,
                $"observed (missing target symbol: {string.Join(", ", missingMatches)})");
        }

        if (GenericSyntaxErrorRegex().Match(output) is { Success: true } genericMatch)
        {
            return new RedClassification(RedKind.SyntaxError, false, $"not observed: test failed with a syntax error ({genericMatch.Value})");
        }

        if (OtherCompileErrorRegex().Match(output) is { Success: true } otherMatch)
        {
            var code = otherMatch.Groups[1].Success ? otherMatch.Groups[1].Value : otherMatch.Groups[2].Value;
            return new RedClassification(RedKind.SyntaxError, false, $"not observed: test failed to compile with {code} (not a missing-symbol error)");
        }

        return new RedClassification(RedKind.AssertionFailure, true, "observed");
    }

    /// <summary>Maps a <see cref="RedKind"/> onto the <c>hoshin red record</c> wire outcome values preserved by CLIENTS.md.</summary>
    public static string ToRedRecordOutcome(RedKind kind) => kind switch
    {
        RedKind.AssertionFailure or RedKind.MissingSymbol => "failed",
        RedKind.PassedUnexpectedly => "passed",
        RedKind.SyntaxError => "could-not-compile",
        RedKind.CouldNotRun or RedKind.TimedOut => "could-not-run",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown red kind."),
    };

    private static RedClassification? TryClassifyModuleNotFound(string output, IReadOnlyList<string>? prodPaths)
    {
        if (!ModuleNotFoundSignalRegex().IsMatch(output))
        {
            return null;
        }

        var specifiers = ModuleSpecifierRegex().Matches(output).Select(m => m.Groups[1].Value).ToList();
        if (specifiers.Count == 0)
        {
            specifiers.AddRange(FileUrlSpecifierRegex().Matches(output).Select(m => m.Groups[1].Value));
        }

        var targetMatches = specifiers.Where(spec => IsTargetModuleSpecifier(spec, prodPaths)).Distinct(StringComparer.Ordinal).ToList();
        if (targetMatches.Count > 0)
        {
            return new RedClassification(
                RedKind.MissingSymbol,
                true,
                $"observed (missing target module: {string.Join(", ", targetMatches)})");
        }

        var detail = specifiers.Count > 0 ? string.Join(", ", specifiers.Distinct(StringComparer.Ordinal)) : "unknown module";
        return new RedClassification(RedKind.CouldNotRun, false, $"not observed: test could not run due to missing dependency ({detail})");
    }

    private static bool IsTargetModuleSpecifier(string specifier, IReadOnlyList<string>? prodPaths)
    {
        var norm = MultiSlashRegex().Replace(specifier.Replace('\\', '/'), "/").TrimEnd('/');
        if (norm.StartsWith("./", StringComparison.Ordinal) || norm.StartsWith("../", StringComparison.Ordinal))
        {
            return true;
        }

        if (prodPaths is null || prodPaths.Count == 0)
        {
            return false;
        }

        foreach (var rawProd in prodPaths)
        {
            var prod = rawProd.Replace('\\', '/').TrimStart('/');
            if (prod.Length == 0)
            {
                continue;
            }

            if (norm.Equals(prod, StringComparison.OrdinalIgnoreCase) ||
                norm.EndsWith("/" + prod, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
