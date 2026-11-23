using Hoshin.Workflow.Engine;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class RedClassifierTests
{
    [Theory]
    [InlineData(127, "could not start x", RedKind.CouldNotRun, false)]
    [InlineData(126, "permission denied", RedKind.CouldNotRun, false)]
    [InlineData(1, "Error: Cannot find module 'vitest'", RedKind.CouldNotRun, false)]
    [InlineData(1, "error TS2307: Cannot find module 'zod' or its corresponding type declarations.", RedKind.CouldNotRun, false)]
    [InlineData(1, "Error [ERR_MODULE_NOT_FOUND]: Cannot find package 'vitest' imported from /repo/test/a.test.mjs", RedKind.CouldNotRun, false)]
    [InlineData(1, "Cannot find module '../src/a.mjs'", RedKind.MissingSymbol, true)]
    [InlineData(1, "error TS2307: Cannot find module './math' or its corresponding type declarations.", RedKind.MissingSymbol, true)]
    [InlineData(2, "pkg/math_test.go:12:3: syntax error: unexpected }", RedKind.SyntaxError, false)]
    [InlineData(2, "pkg/math_test.go:8:1: expected 'package', found 'EOF'", RedKind.SyntaxError, false)]
    [InlineData(101, "error[E0425]: cannot find value `sub` in this scope", RedKind.MissingSymbol, true)]
    [InlineData(101, "error[E0433]: failed to resolve: use of undeclared crate or module `math`", RedKind.MissingSymbol, true)]
    [InlineData(101, "error[E0412]: cannot find type `Widget` in this scope", RedKind.MissingSymbol, true)]
    [InlineData(101, "error: expected one of `!`, `.`, `::`, `;`, `?`, `{`, `}`, or an operator, found `foo`", RedKind.SyntaxError, false)]
    [InlineData(101, "error[E0308]: mismatched types", RedKind.SyntaxError, false)]
    [InlineData(1, "MathTest.java:10: error: ';' expected", RedKind.SyntaxError, false)]
    [InlineData(1, "AssertionError: Expected: 3, Actual: 2", RedKind.AssertionFailure, true)]
    public void Classify_ReturnsExpectedKindAndValidity(int exitCode, string output, RedKind expectedKind, bool expectedValid)
    {
        var result = new AllowListedCommandResult(exitCode, output, TimedOut: false, TimeSpan.FromMilliseconds(50));
        var classification = RedClassifier.Classify(result);

        Assert.Equal(expectedKind, classification.Kind);
        Assert.Equal(expectedValid, classification.IsValidRed);
    }

    [Fact]
    public void Classify_CannotFindModuleMatchingProductionPath_CountsAsMissingSymbol()
    {
        var result = new AllowListedCommandResult(
            1,
            "Error [ERR_MODULE_NOT_FOUND]: Cannot find module '/tmp/repo/src/new-feature.mjs' imported from /tmp/repo/test/t1.test.mjs",
            TimedOut: false,
            TimeSpan.FromMilliseconds(20));

        var withProd = RedClassifier.Classify(result, ["src/new-feature.mjs"]);
        Assert.Equal(RedKind.MissingSymbol, withProd.Kind);
        Assert.True(withProd.IsValidRed);

        var withoutProd = RedClassifier.Classify(result, ["src/other.mjs"]);
        Assert.Equal(RedKind.CouldNotRun, withoutProd.Kind);
        Assert.False(withoutProd.IsValidRed);
    }
}
