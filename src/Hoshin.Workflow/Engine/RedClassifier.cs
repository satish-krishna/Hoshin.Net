using System.Text.RegularExpressions;

namespace Hoshin.Workflow.Engine;

public enum RedKind
{
    /// <summary>The test passed without production code — not valid Red.</summary>
    PassedUnexpectedly,

    /// <summary>The test timed out during Red observation — not valid Red.</summary>
    TimedOut,

    /// <summary>The test file has a syntax error — not valid Red.</summary>
    SyntaxError,

    /// <summary>Strong-language compilation failed strictly due to missing target symbols (e.g. CS0246, CS0103, CS1061, TS2304).</summary>
    MissingSymbol,

    /// <summary>The test executed and failed an assertion or runtime expectation — valid Red.</summary>
    AssertionFailure,
}

public sealed record RedClassification(RedKind Kind, bool IsValidRed, string Summary);

/// <summary>
/// Classifies the outcome of running a task's test command against the isolated test-only commit on a detached worktree.
/// Enforces the strongly-typed Red rule: failing assertions and missing-target-symbol compile errors (<c>CS0246</c>,
/// <c>CS0103</c>, <c>CS1061</c>, <c>TS2304</c>, etc.) count as valid Red when resolved by Green; syntax errors do not.
/// </summary>
public static class RedClassifier
{
    private static readonly Regex HardSyntaxErrorRegex = new(
        @"\b(CS1002|CS1003|CS1009|CS1010|CS1012|CS1026|CS1031|CS1513|CS1514|CS1519|CS1525|CS1526|CS1585|TS1005|TS1109|TS1128|TS1434)\b|IndentationError\s*:",
        RegexOptions.Compiled);

    private static readonly Regex GenericSyntaxErrorRegex = new(
        @"SyntaxError\s*:",
        RegexOptions.Compiled);

    private static readonly Regex MissingSymbolRegex = new(
        @"\b(CS0246|CS0103|CS1061|CS0117|CS0234|CS0115|CS0535|CS1501|CS1729|CS7036|TS2304|TS2305|TS2307|TS2339|TS2551|TS2554|TS2614)\b|does not provide an export named\b|Cannot find module\b|ERR_MODULE_NOT_FOUND\b",
        RegexOptions.Compiled);

    private static readonly Regex OtherCompileErrorRegex = new(
        @"\berror\s+(CS\d{4}|TS\d{4})\b",
        RegexOptions.Compiled);

    public static RedClassification Classify(AllowListedCommandResult result)
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

        if (HardSyntaxErrorRegex.IsMatch(output))
        {
            var match = HardSyntaxErrorRegex.Match(output).Value;
            return new RedClassification(RedKind.SyntaxError, false, $"not observed: test failed with a syntax error ({match})");
        }

        // Check missing target symbols before generic JS SyntaxError (Node ESM reports missing named exports as
        // "SyntaxError: The requested module ... does not provide an export named '...'").
        var missingMatches = MissingSymbolRegex.Matches(output).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        if (missingMatches.Count > 0)
        {
            return new RedClassification(
                RedKind.MissingSymbol,
                true,
                $"observed (missing target symbol: {string.Join(", ", missingMatches)})");
        }

        if (GenericSyntaxErrorRegex.IsMatch(output))
        {
            var match = GenericSyntaxErrorRegex.Match(output).Value;
            return new RedClassification(RedKind.SyntaxError, false, $"not observed: test failed with a syntax error ({match})");
        }

        if (OtherCompileErrorRegex.IsMatch(output))
        {
            var code = OtherCompileErrorRegex.Match(output).Groups[1].Value;
            return new RedClassification(RedKind.SyntaxError, false, $"not observed: test failed to compile with {code} (not a missing-symbol error)");
        }

        return new RedClassification(RedKind.AssertionFailure, true, "observed");
    }
}
