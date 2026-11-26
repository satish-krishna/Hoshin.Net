namespace Hoshin.Tests.Acceptance.Harness;

/// <summary>One plan task as the compliant model writes it into plan.md, and the work it does when the task is dispatched.</summary>
/// <param name="Do">Does the task's work in the repository folder the dispatch names (the test first, then the change).</param>
internal sealed record PlanTask(string Id, string Repo, string Title, string Change, string Tests, string[] Test, Action<string> Do)
{
    public string[] DependsOn { get; init; } = [];

    public string[] Covers { get; init; } = [];

    public string[]? Compile { get; init; }
}

/// <summary>A high-severity finding the compliant reviewer reports in review 1.</summary>
internal sealed record ReviewFinding(string Repo, string File, int Line, string Text);

/// <summary>
/// What the compliant model knows about one change: the documents it writes when a brief asks, the work each task
/// does, the finding its first review reports, and the fix it applies when the engineer asks for one. The model
/// never decides anything the hooks did not ask for.
/// </summary>
internal sealed class ChangeScript
{
    public required string Slug { get; init; }

    public required string Title { get; init; }

    public required string Problem { get; init; }

    public required string Outcome { get; init; }

    public string Users { get; init; } = "Web visitors and api clients.";

    public string Constraints { get; init; } = "Existing behavior is unchanged.";

    public required string Decisions { get; init; }

    /// <summary>The Gherkin inside the spec's <c>## Requirements</c> fence, starting at <c>Feature:</c>.</summary>
    public required string Requirements { get; init; }

    public required IReadOnlyList<PlanTask> Tasks { get; init; }

    /// <summary>The high finding review 1 reports; null means every review is clean.</summary>
    public ReviewFinding? FirstReviewFinding { get; init; }

    /// <summary>The repository the engineer's fix applies to, and the fix itself (test first, then the change).</summary>
    public string? FixRepo { get; init; }

    public Action<string>? Fix { get; init; }

    public PlanTask Task(string id) =>
        Tasks.SingleOrDefault(t => t.Id == id) ?? throw new InvalidOperationException($"the compliant model was asked to do an unknown task `{id}`");
}

/// <summary>The changes the acceptance tests drive.</summary>
internal static class ChangeScripts
{
    public const string ShoutGreetingRequirements = """
        Feature: Shout greeting
          @R1 @from:D1
          Rule: The calculator multiplies
            @R1.1
            Scenario: Multiplying three by four
              Given the numbers 3 and 4
              When they are multiplied
              Then the result is 12

          @R2 @from:D2
          Rule: A shouted greeting ends with an exclamation mark
            @R2.1
            Scenario: Shouting at Ada
              Given the name "Ada"
              When the greeting is shouted
              Then the result is "Hello, Ada!"
        """;

    /// <summary>
    /// The R0 gate's change: T1 multiplies in api (its test runs against compiled, gitignored output) and T2 shouts in
    /// web (its test runs a gitignored tool). With <paramref name="findingInReview1"/>, review 1 finds that shout does
    /// not trim the name, and the engineer's fix trims it.
    /// </summary>
    public static ChangeScript ShoutGreeting(bool findingInReview1 = true, string slug = "add-shout") => new()
    {
        Slug = slug,
        Title = "Shout greeting",
        Problem = "Callers cannot emphasize a greeting, and the calculator cannot multiply.",
        Outcome = "The web greeting can shout, and the api calculator can multiply.",
        Constraints = "Existing greetings and sums are unchanged.",
        Decisions = "D1: the api calculator multiplies two numbers.\nD2: a shouted greeting ends with `!`.",
        Requirements = ShoutGreetingRequirements,
        Tasks =
        [
            new PlanTask(
                "T1",
                "api",
                "Multiply in the calculator",
                "Add `multiply(a, b)` to `src/calc.mjs`.",
                "`test/multiply.test.mjs` checks `multiply(3, 4) === 12` against the built `dist/calc.mjs`.",
                ["node", "--test", "test/multiply.test.mjs"],
                MultiplyInApi)
            {
                Covers = ["R1.1"],
                Compile = ["node", "build.mjs"],
            },
            new PlanTask(
                "T2",
                "web",
                "Shout the greeting",
                "Add `shout(name)` to `src/greet.mjs`.",
                "`test/shout.test.mjs` checks `shout('Ada') === 'Hello, Ada!'`.",
                ["node", "node_modules/testkit/run.mjs", "test/shout.test.mjs"],
                ShoutInWeb)
            {
                Covers = ["R2.1"],
            },
        ],
        FirstReviewFinding = findingInReview1
            ? new ReviewFinding("web", "src/greet.mjs", 2, "`shout('  Ada ')` returns `Hello,   Ada !`: the name is not trimmed, so padded input from the form shouts garbage.")
            : null,
        FixRepo = "web",
        Fix = TrimBeforeShouting,
    };

    /// <summary>Two tasks in web, the second building on the first: T1 shouts, T2 whispers.</summary>
    public static ChangeScript ShoutThenWhisper(string slug = "shout-whisper") => new()
    {
        Slug = slug,
        Title = "Shout and whisper",
        Problem = "Callers cannot change how loud a greeting is.",
        Outcome = "The web greeting can shout and whisper.",
        Users = "Web visitors.",
        Constraints = "Existing greetings are unchanged.",
        Decisions = "D1: a shouted greeting ends with `!`.\nD2: a whispered greeting is lower case and ends with `...`.",
        Requirements = """
            Feature: Shout and whisper
              @R1 @from:D1
              Rule: A shouted greeting ends with an exclamation mark
                @R1.1
                Scenario: Shouting at Ada
                  Given the name "Ada"
                  When the greeting is shouted
                  Then the result is "Hello, Ada!"

              @R2 @from:D2
              Rule: A whispered greeting is lower case
                @R2.1
                Scenario: Whispering to Ada
                  Given the name "Ada"
                  When the greeting is whispered
                  Then the result is "hello, ada..."
            """,
        Tasks =
        [
            new PlanTask(
                "T1",
                "web",
                "Shout the greeting",
                "Add `shout(name)` to `src/greet.mjs`.",
                "`test/shout.test.mjs` checks `shout('Ada') === 'Hello, Ada!'`.",
                ["node", "node_modules/testkit/run.mjs", "test/shout.test.mjs"],
                ShoutInWeb)
            {
                Covers = ["R1.1"],
            },
            new PlanTask(
                "T2",
                "web",
                "Whisper the greeting",
                "Add `whisper(name)` to `src/greet.mjs`, next to `shout`.",
                "`test/whisper.test.mjs` checks `whisper('Ada') === 'hello, ada...'`.",
                ["node", "node_modules/testkit/run.mjs", "test/whisper.test.mjs"],
                WhisperInWeb)
            {
                DependsOn = ["T1"],
                Covers = ["R2.1"],
            },
        ],
    };

    /// <summary>A light-track bug fix in a single-repository workspace whose one app is the repository itself.</summary>
    public static ChangeScript TrimTheGreetingName(string slug = "trim-greeting") => new()
    {
        Slug = slug,
        Title = "Greeting trims the name",
        Problem = "`greet('  Ada ')` returns `Hello,   Ada `: the name is not trimmed, so padded input from the form greets garbage. Root cause: `src/greet.mjs:1` uses the name as given.",
        Outcome = "The greeting trims the name before using it.",
        Users = "Visitors.",
        Constraints = "Greetings of names without padding are unchanged.",
        Decisions = "None.",
        Requirements = """
            Feature: Greeting trims the name
              @R1 @from:problem
              Rule: The greeting trims the name
                @R1.1
                Scenario: Greeting a padded name
                  Given the name "  Ada "
                  When the visitor is greeted
                  Then the result is "Hello, Ada"
            """,
        Tasks =
        [
            new PlanTask(
                "T1",
                "app",
                "Trim the name",
                "Trim the name in `greet` in `src/greet.mjs`.",
                "`test/greet-trim.test.mjs` checks `greet('  Ada ') === 'Hello, Ada'`.",
                ["node", "--test", "test/greet-trim.test.mjs"],
                root =>
                {
                    Write(root, "test/greet-trim.test.mjs", """
                        import test from 'node:test';
                        import assert from 'node:assert/strict';
                        import { greet } from '../src/greet.mjs';
                        test('greet trims', () => { assert.equal(greet('  Ada '), 'Hello, Ada'); });
                        """);
                    Write(root, "src/greet.mjs", "export const greet = (name) => `Hello, ${name.trim()}`;\n");
                })
            {
                Covers = ["R1.1"],
            },
        ],
    };

    public static void MultiplyInApi(string root)
    {
        Write(root, "test/multiply.test.mjs", """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { multiply } from '../dist/calc.mjs';
            test('multiply', () => { assert.equal(multiply(3, 4), 12); });
            """);
        Write(root, "src/calc.mjs", "export const add = (a, b) => a + b;\nexport const multiply = (a, b) => a * b;\n");
    }

    public static void ShoutInWeb(string root)
    {
        Write(root, "test/shout.test.mjs", """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout', () => { assert.equal(shout('Ada'), 'Hello, Ada!'); });
            """);
        Write(root, "src/greet.mjs", "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name)}!`;\n");
    }

    public static void WhisperInWeb(string root)
    {
        Write(root, "test/whisper.test.mjs", """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { whisper } from '../src/greet.mjs';
            test('whisper', () => { assert.equal(whisper('Ada'), 'hello, ada...'); });
            """);
        Write(root, "src/greet.mjs", "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name)}!`;\nexport const whisper = (name) => `${greet(name).toLowerCase()}...`;\n");
    }

    public static void TrimBeforeShouting(string root)
    {
        Write(root, "test/shout-trim.test.mjs", """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout trims', () => { assert.equal(shout('  Ada '), 'Hello, Ada!'); });
            """);
        Write(root, "src/greet.mjs", "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name.trim())}!`;\n");
    }

    public static void Write(string root, string relative, string text) =>
        Unit.TestGitFixture.Write(Path.Combine(root, relative), text.Replace("\r\n", "\n", StringComparison.Ordinal));
}
