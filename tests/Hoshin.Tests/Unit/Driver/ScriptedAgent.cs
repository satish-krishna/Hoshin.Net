using System.Globalization;
using System.Text.RegularExpressions;
using Hoshin.Workflow.Driver;

namespace Hoshin.Tests.Unit.Driver;

/// <summary>
/// A compliant agent for driver tests: does the work an <see cref="AgentWork"/> asks for in a small single-repo
/// project (a greeting module that learns to shout), writing only inside the work's <see cref="WorkScope"/>.
/// </summary>
internal sealed partial class ScriptedAgent
{
    public List<Role> Done { get; } = [];

    /// <summary>When set, the first plan written is invalid, to exercise the driver's feedback loop.</summary>
    public bool WriteBrokenPlanFirst { get; init; }

    /// <summary>When set, the first implement attempt changes code without a test, so Red is not observed.</summary>
    public bool SkipTestOnFirstAttempt { get; init; }

    private bool _brokePlan;

    public void Do(AgentWork work)
    {
        foreach (var (path, text) in FilesFor(work))
        {
            TestGitFixture.Write(Path.Combine(work.Scope.WorkingDirectory, path), text);
        }
    }

    /// <summary>The files (relative to the work's directory) a compliant agent writes for <paramref name="work"/>.</summary>
    public IReadOnlyList<(string Path, string Text)> FilesFor(AgentWork work)
    {
        Done.Add(work.Role);
        var root = work.Scope.WorkingDirectory;
        switch (work.Role)
        {
            case Role.Intent:
            case Role.Spec:
            case Role.Plan:
                return [(work.Scope.MayWrite[0], DocumentText(work))];
            case Role.Review:
                var reviewPath = Path.Combine(root, work.Scope.MayWrite[0]);
                var existing = File.Exists(reviewPath) ? File.ReadAllText(reviewPath).TrimEnd() + "\n\n" : "```jsonc\n{ \"layout\": 2 }\n```\n\n# Review findings\n\n";
                return [(work.Scope.MayWrite[0], existing + ReviewSection(work.Brief))];
            case Role.Implement when work.Id.Task?.StartsWith("FIX-", StringComparison.Ordinal) == true:
                return FixDeclined();
            case Role.Implement:
                return Implement(work.Attempt);
            default:
                throw new InvalidOperationException($"The scripted agent does not do {work.Role} work.");
        }
    }

    private List<(string Path, string Text)> Implement(int attempt)
    {
        var files = new List<(string, string)>();
        if (!(SkipTestOnFirstAttempt && attempt == 1))
        {
            files.Add(("test/shout.test.mjs", """
                import test from 'node:test';
                import assert from 'node:assert/strict';
                import { shout } from '../src/greet.mjs';
                test('shout', () => { assert.equal(shout('Ada'), 'Hello, Ada!'); });
                """));
        }

        files.Add(("src/greet.mjs", "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => `${greet(name)}!`;\n"));
        return files;
    }

    private static List<(string Path, string Text)> FixDeclined() =>
    [
        ("test/shout-empty.test.mjs", """
            import test from 'node:test';
            import assert from 'node:assert/strict';
            import { shout } from '../src/greet.mjs';
            test('shout an empty name', () => { assert.equal(shout(''), 'Hello!'); });
            """),
        ("src/greet.mjs", "export const greet = (name) => `Hello, ${name}`;\nexport const shout = (name) => (name ? `${greet(name)}!` : 'Hello!');\n"),
    ];

    private string DocumentText(AgentWork work)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string FrontMatter(string startsWith) =>
            (work.Brief.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(startsWith, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"the brief gives no front matter starting with {startsWith}:\n{work.Brief}"))
            .Replace("<short title>", "Shout greeting", StringComparison.Ordinal)
            .Replace("<intent title>", "Shout greeting", StringComparison.Ordinal)
            .Replace("<name and role>", "Pat, product owner", StringComparison.Ordinal)
            .Replace("<YYYY-MM-DD>", today, StringComparison.Ordinal);

        var text = work.Role switch
        {
            Role.Intent => $$"""
                ```jsonc
                {{FrontMatter("{ \"title\"")}}
                ```

                # Intent: Shout greeting

                ## Problem

                Callers cannot emphasize a greeting.

                ## Proposed outcome

                The greeting can shout.

                ## Affected users and systems

                Visitors.

                ## Constraints

                Existing greetings are unchanged.

                ## Decisions

                D1: a shouted greeting ends with `!`.

                ## Open questions

                None.
                """,
            Role.Spec => $$"""
                ```jsonc
                {{FrontMatter("{ \"layout\": 2, \"title\"")}}
                ```

                # Spec: Shout greeting

                ## Requirements

                ```gherkin
                Feature: Shout greeting
                  @R1 @from:D1
                  Rule: A shouted greeting ends with an exclamation mark
                    @R1.1
                    Scenario: Shouting at Ada
                      Given the name "Ada"
                      When the greeting is shouted
                      Then the result is "Hello, Ada!"
                ```
                """,
            Role.Plan => PlanText(FrontMatter("{ \"layout\": 2, \"system\"")),
            _ => throw new InvalidOperationException($"no document for {work.Role}"),
        };
        return text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private string PlanText(string frontMatter)
    {
        var covers = WriteBrokenPlanFirst && !_brokePlan ? "R9.9" : "R1.1";
        _brokePlan = true;
        return $$"""
            ```jsonc
            {{frontMatter}}
            ```

            # Plan: Shout greeting

            ## Tasks

            ### T1: Shout the greeting

            ```jsonc
            {
              "id": "T1",
              "repo": "app",
              "depends_on": [],
              "covers": ["{{covers}}"],
              "test": ["node", "--test", "test/shout.test.mjs"]
            }
            ```

            #### Change

            Add `shout(name)` to `src/greet.mjs`.

            #### Tests

            `test/shout.test.mjs` checks `shout('Ada') === 'Hello, Ada!'`.
            """;
    }

    private static string ReviewSection(string brief)
    {
        var number = int.Parse(ReviewNumber().Match(brief).Groups[1].Value, CultureInfo.InvariantCulture);
        var commits = string.Join(", ", AppCommit().Matches(brief).Select(m => $"\"{m.Groups[1].Value}\": \"{m.Groups[2].Value}\""));
        return $$"""
            ## Review {{number}}

            ```jsonc
            { "review": {{number}}, "reviewed_at": "{{DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}}", "commits": { {{commits}} } }
            ```

            No findings.
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    [GeneratedRegex(@"\(Review (\d+)\)")]
    private static partial Regex ReviewNumber();

    [GeneratedRegex(@"`([\w.-]+)`: `([0-9a-f]{7,40})`")]
    private static partial Regex AppCommit();
}
