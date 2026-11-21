using System.Text.Json;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Migration;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class DocumentMigrationTests
{
    private const string TsHoshinRoot = @"D:\workspaces\hoshin";

    // =========================================================================
    // 1. YAML-to-JSONC converter unit tests
    // =========================================================================

    [Fact]
    public void YamlConverter_PreservesCommentsFieldOrderTypesAndDigitHashes()
    {
        const string yaml = """
            # Top comment
            layout: 2 # schema version
            system: demo
            # Pin to upstream spec
            source_spec_commit: 0123456
            accepted_in_advance: true
            date: 2026-10-22
            policies_applied: [house-style, security]
            # trailing comment
            """;

        var jsonc = YamlToJsoncConverter.Default.ConvertFrontMatter(yaml);
        Assert.Contains("// Top comment", jsonc);
        Assert.Contains("// schema version", jsonc);
        Assert.Contains("// Pin to upstream spec", jsonc);
        Assert.Contains("// trailing comment", jsonc);

        Assert.True(Jsonc.TryParse(jsonc, out var parsed, out var err), err);
        Assert.NotNull(parsed);
        var obj = parsed!.Value;

        // Exact key order preserved.
        var keys = obj.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["layout", "system", "source_spec_commit", "accepted_in_advance", "date", "policies_applied"], keys);

        Assert.Equal(2, obj.GetProperty("layout").GetInt32());
        Assert.Equal("demo", obj.GetProperty("system").GetString());
        Assert.Equal("0123456", obj.GetProperty("source_spec_commit").GetString());
        Assert.True(obj.GetProperty("accepted_in_advance").GetBoolean());
        Assert.Equal("2026-10-22", obj.GetProperty("date").GetString());
        Assert.Equal(["house-style", "security"], obj.GetProperty("policies_applied").EnumerateArray().Select(e => e.GetString()).ToList());
    }

    [Fact]
    public void YamlConverter_HandlesBlockScalarsNestedMapsAndSequences()
    {
        const string yaml = """
            task: T7
            commits:
              - 1234567
              - 1e50000
              - abcdef0
            departure: |-
              First line of departure.
              Second line with "quotes" and 'apostrophes'.
            notes: >
              Folded paragraph
              across two lines.
            """;

        var jsonc = YamlToJsoncConverter.Default.ConvertBlock(yaml);
        Assert.True(Jsonc.TryParse(jsonc, out var parsed, out var err), err);
        var obj = parsed!.Value;

        Assert.Equal("T7", obj.GetProperty("task").GetString());
        Assert.Equal(["1234567", "1e50000", "abcdef0"], obj.GetProperty("commits").EnumerateArray().Select(e => e.GetString()).ToList());
        Assert.Equal("First line of departure.\nSecond line with \"quotes\" and 'apostrophes'.", obj.GetProperty("departure").GetString());
        Assert.Equal("Folded paragraph across two lines.\n", obj.GetProperty("notes").GetString());
    }

    [Fact]
    public void YamlConverter_ConvertsChangeYmlWithCommentsAndPullRequests()
    {
        const string changeYml = """
            # Written by the hoshin program (change start, change rename, change.done); do not edit by hand.
            version: 1
            branch: "feat/greeting"
            type: feat
            scope: "demo"
            base: "main"
            pullRequests:
              # Opened by change.done
              - repo: "app"
                url: "https://github.com/org/app/pull/42"
                number: 42
                pushed: "0123456"
            """;

        var jsonc = DocumentMigrator.Default.MigrateChangeYamlText(changeYml);
        Assert.StartsWith("// Written by the hoshin program", jsonc);
        Assert.Contains("// Opened by change.done", jsonc);
        Assert.True(Jsonc.TryParse(jsonc, out var parsed, out var err), err);
        Assert.Empty(ChangeRecordStore.RecordProblems(parsed));

        var obj = parsed!.Value;
        Assert.Equal(["version", "branch", "type", "scope", "base", "pullRequests"], obj.EnumerateObject().Select(p => p.Name).ToList());
        var pr = obj.GetProperty("pullRequests")[0];
        Assert.Equal("app", pr.GetProperty("repo").GetString());
        Assert.Equal(42, pr.GetProperty("number").GetInt32());
        Assert.Equal("0123456", pr.GetProperty("pushed").GetString());
    }

    // =========================================================================
    // 2. DocumentMigrator unit tests (Layout 1 & Layout 2 -> Layout 2 JSONC)
    // =========================================================================

    [Fact]
    public void DocumentMigrator_MigratesIntentAndContextFrontMatterWithoutAddingLayout()
    {
        const string intentMd = """
            ---
            # Intent metadata
            title: Greeting
            system: demo
            author: tester
            date: 2026-11-01
            status: accepted
            scope: demo
            ---

            # Intent: Greeting

            ## Problem

            No greeting.

            ## Proposed outcome

            A greeting.

            ## Affected users and systems

            Users of demo.

            ## Constraints

            None.

            ## Decisions

            D1: say hello.

            ## Open questions

            None.
            """;

        var migrated = DocumentMigrator.Default.MigrateMarkdownText(intentMd, "intent/demo/c1/intent.md", out var changed);
        Assert.True(changed);
        Assert.StartsWith("```jsonc\n", migrated);
        Assert.Contains("// Intent metadata", migrated);
        Assert.DoesNotContain("\"layout\"", migrated);
        Assert.Empty(new IntentReader().Check(migrated, "intent.md", "demo"));

        // Second pass is strictly idempotent.
        var second = DocumentMigrator.Default.MigrateMarkdownText(migrated, "intent/demo/c1/intent.md", out var changedAgain);
        Assert.False(changedAgain);
        Assert.Equal(migrated, second);
    }

    [Fact]
    public void DocumentMigrator_MigratesLayout1SpecToLayout2JsoncAndPassesSpecReader()
    {
        const string layout1Spec = """
            ---
            system: demo
            status: accepted
            ---

            # Spec: An older document

            ```gherkin
            Feature: Old layout
              Rule: R1 - Hyphen separator (from: D1)
                Scenario: R1.1 – En dash separator
                  Given a thing
                  Then it is read

              Rule: R2 — Em dash separator (from: Proposed outcome 1–2, D3)
                Scenario Outline: R2.1 — Pays <amount>
                  Given a bill of <amount>
                  Then it is paid

                  Examples:
                    | amount |
                    | 5      |
            ```
            """;

        var migrated = DocumentMigrator.Default.MigrateMarkdownText(layout1Spec, "spec.md", out var changed);
        Assert.True(changed);
        Assert.StartsWith("```jsonc\n", migrated);

        var read = new SpecReader().Read(migrated, "spec.md");
        Assert.Equal(2, read.Layout);
        Assert.Empty(read.Problems);
        Assert.Equal(2, read.Document.Requirements.Count);

        var r1 = read.Document.Requirements[0];
        Assert.Equal("R1", r1.Id);
        Assert.Equal("Hyphen separator", r1.Title);
        Assert.Equal(["D1"], r1.From);
        Assert.Single(r1.Scenarios);
        Assert.Equal("R1.1", r1.Scenarios[0].Id);
        Assert.Equal("En dash separator", r1.Scenarios[0].Title);
        Assert.False(r1.Scenarios[0].Outline);

        var r2 = read.Document.Requirements[1];
        Assert.Equal("R2", r2.Id);
        Assert.Equal("Em dash separator", r2.Title);
        Assert.Equal(["Proposed-outcome-1–2", "D3"], r2.From);
        Assert.Single(r2.Scenarios);
        Assert.Equal("R2.1", r2.Scenarios[0].Id);
        Assert.True(r2.Scenarios[0].Outline);
        Assert.Equal([["amount"], ["5"]], r2.Scenarios[0].Examples);

        // Idempotent on second pass.
        var second = DocumentMigrator.Default.MigrateMarkdownText(migrated, "spec.md", out var changed2);
        Assert.False(changed2);
        Assert.Equal(migrated, second);
    }

    [Fact]
    public void DocumentMigrator_MigratesLayout1PlanWithImplicitDepsSharedDecisionsAndCoverage()
    {
        const string layout1Plan = """
            ---
            system: demo
            ---

            # Plan: Old shape

            ## Shared decisions

            | Question | Owner | Defined as | Used by |
            |---|---|---|---|
            | How a problem is reported | T1 | `formatProblem(p)` | T2 |

            ## Scenario coverage

            | Scenario | Covering test | Task |
            |---|---|---|
            | R1.1 | `test_r1_1` | T1 |
            | R2.1 | `test_r2_1` | T2 |

            ## Tasks

            ### T1: First task (`app`)

            - **Depends on:** none
            - **Files:** src/a.ts
            - **Tests:** a failing test first

            ### T2: Second task (`app`)

            - **Change:** no Depends on line, so it depends on every task above
            - **Tests:** verify T2 after T1
            """;

        var migrated = DocumentMigrator.Default.MigrateMarkdownText(layout1Plan, "plan.md", out var changed);
        Assert.True(changed);

        var read = new PlanReader().Read(migrated, "plan.md", ["app"], "demo");
        Assert.Equal(2, read.Layout);
        Assert.Empty(read.Problems);
        Assert.Single(read.Document.SharedDecisions);
        Assert.Equal("How a problem is reported", read.Document.SharedDecisions[0].Question);
        Assert.Equal("T1", read.Document.SharedDecisions[0].Owner);
        Assert.Equal("`formatProblem(p)`", read.Document.SharedDecisions[0].Function);

        Assert.Equal(2, read.Document.Tasks.Count);
        Assert.Equal("T1", read.Document.Tasks[0].Id);
        Assert.Equal("app", read.Document.Tasks[0].Repo);
        Assert.Empty(read.Document.Tasks[0].DependsOn);
        Assert.Equal(["R1.1"], read.Document.Tasks[0].Covers);

        Assert.Equal("T2", read.Document.Tasks[1].Id);
        Assert.Equal(["T1"], read.Document.Tasks[1].DependsOn);
        Assert.Equal(["R2.1"], read.Document.Tasks[1].Covers);

        // Idempotent on second pass.
        var second = DocumentMigrator.Default.MigrateMarkdownText(migrated, "plan.md", out var changed2);
        Assert.False(changed2);
        Assert.Equal(migrated, second);
    }

    [Fact]
    public void DocumentMigrator_MigratesLayout1AndHybridBuildLogs()
    {
        const string hybridLog = """
            ---
            layout: 2
            ---

            # Build log: app

            | Task | Commits | Departure |
            |---|---|---|
            | T1 | `abc1234`, `1234567` | none |
            | T2 | 0123456 | Used a | in the header; see note |

            ### T3

            ```yaml
            task: T3
            commits: [aaa1111, 9999999]
            departure: written in the new layout
            ```
            """;

        var migrated = DocumentMigrator.Default.MigrateMarkdownText(hybridLog, "build/app.md", out var changed);
        Assert.True(changed);

        var read = new BuildLogReader().Read(migrated, "build/app.md");
        Assert.Equal(2, read.Layout);
        Assert.Empty(read.Problems);
        Assert.Equal(3, read.Document.Entries.Count);
        Assert.Equal("T1", read.Document.Entries[0].Task);
        Assert.Equal(["abc1234", "1234567"], read.Document.Entries[0].Commits);
        Assert.Null(read.Document.Entries[0].Departure);

        Assert.Equal("T2", read.Document.Entries[1].Task);
        Assert.Equal(["0123456"], read.Document.Entries[1].Commits);
        Assert.Equal("Used a|in the header; see note", read.Document.Entries[1].Departure);

        Assert.Equal("T3", read.Document.Entries[2].Task);
        Assert.Equal(["aaa1111", "9999999"], read.Document.Entries[2].Commits);
        Assert.Equal("written in the new layout", read.Document.Entries[2].Departure);

        // Idempotent on second pass.
        var second = DocumentMigrator.Default.MigrateMarkdownText(migrated, "build/app.md", out var changed2);
        Assert.False(changed2);
        Assert.Equal(migrated, second);
    }

    [Fact]
    public void DocumentMigrator_MigratesReviewAndPreservesProseYamlBlocks()
    {
        const string reviewMd = """
            ---
            layout: 2
            ---

            # Review findings

            ## Review 1

            ```yaml
            review: 1
            reviewed_at: 2026-10-22
            commits: { kata: 840fa0c, hoshin: 1234567 }
            ```

            ### F1

            ```yaml
            id: F1
            severity: high
            repo: hoshin
            concern: convention
            rule: CLAUDE.md:12
            file: app/src/styles/workbench.css
            line: 304
            ```

            The toolbar overflows at 1280.
            """;

        var migrated = DocumentMigrator.Default.MigrateMarkdownText(reviewMd, "build/review.md", out var changed);
        Assert.True(changed);

        var read = new ReviewReader().Read(migrated, "build/review.md");
        Assert.Empty(read.Problems);
        Assert.Single(read.Document.Reviews);
        Assert.Equal("1234567", read.Document.Reviews[0].Commits["hoshin"]);
        Assert.Single(read.Document.Reviews[0].Findings);
        Assert.Equal(304, read.Document.Reviews[0].Findings[0].Line);
        Assert.Equal("The toolbar overflows at 1280.", read.Document.Reviews[0].Findings[0].Text);

        // Prose yaml blocks inside #### Change stay untouched.
        const string planWithYamlProse = """
            ---
            layout: 2
            system: demo
            ---

            # Plan: Example

            ## Tasks

            ### T1: first

            ```yaml
            id: T1
            repo: app
            ```

            #### Change

            Change it.

            ```yaml
            note: not a record
            ```

            #### Tests

            Test it first.
            """;

        var migratedPlan = DocumentMigrator.Default.MigrateMarkdownText(planWithYamlProse, "plan.md", out var planChanged);
        Assert.True(planChanged);
        Assert.Contains("```yaml\nnote: not a record\n```", migratedPlan);
        var planRead = new PlanReader().Read(migratedPlan, "plan.md", ["app"], "demo");
        Assert.Empty(planRead.Problems);
    }

    // =========================================================================
    // 3. CLI --dry-run, idempotency, refusal, and workspace migrate tests
    // =========================================================================

    [Fact]
    public void CliMigrate_DryRunChangesNothing_WriteMigratesAndIsIdempotent()
    {
        using var tmp = new TempDir("hoshin-mig-cli-");
        var changeDir = Path.Combine(tmp.Path, "intent", "demo", "2026-11-01-demo");
        TestGitFixture.Write(Path.Combine(changeDir, "change.yml"), """
            # Written by the hoshin program; do not edit by hand.
            version: 1
            branch: "feat/demo"
            type: feat
            scope: "demo"
            base: "main"
            """);
        TestGitFixture.Write(Path.Combine(changeDir, "intent.md"), """
            ---
            title: Demo
            system: demo
            author: tester
            date: 2026-11-01
            status: accepted
            scope: demo
            ---

            # Intent: Demo
            """);

        var beforeIntent = File.ReadAllText(Path.Combine(changeDir, "intent.md"));
        var beforeChange = File.ReadAllText(Path.Combine(changeDir, "change.yml"));

        // Dry run touches nothing.
        var dry = TestGitFixture.RunCli(tmp.Path, "migrate", tmp.Path, "--dry-run", "--json");
        Assert.Equal(0, dry.ExitCode);
        Assert.NotNull(dry.Json);
        Assert.False(dry.Json!.Value.GetProperty("write").GetBoolean());
        Assert.Equal(beforeIntent, File.ReadAllText(Path.Combine(changeDir, "intent.md")));
        Assert.Equal(beforeChange, File.ReadAllText(Path.Combine(changeDir, "change.yml")));
        Assert.False(File.Exists(Path.Combine(changeDir, "change.jsonc")));

        // Real write migrates both files.
        var write = TestGitFixture.RunCli(tmp.Path, "migrate", tmp.Path, "--json");
        Assert.Equal(0, write.ExitCode);
        Assert.True(write.Json!.Value.GetProperty("write").GetBoolean());
        Assert.False(File.Exists(Path.Combine(changeDir, "change.yml")));
        Assert.True(File.Exists(Path.Combine(changeDir, "change.jsonc")));
        var afterIntent = File.ReadAllText(Path.Combine(changeDir, "intent.md"));
        var afterChange = File.ReadAllText(Path.Combine(changeDir, "change.jsonc"));
        Assert.StartsWith("```jsonc", afterIntent);
        Assert.StartsWith("// Written by the hoshin program", afterChange);

        // `hoshin check` passes on migrated change.jsonc.
        var check = TestGitFixture.RunCli(tmp.Path, "check", Path.Combine(changeDir, "change.jsonc"));
        Assert.Equal(0, check.ExitCode);

        // Second run is strictly idempotent.
        var again = TestGitFixture.RunCli(tmp.Path, "migrate", tmp.Path, "--json");
        Assert.Equal(0, again.ExitCode);
        var files = again.Json!.Value.GetProperty("files").EnumerateArray().ToList();
        Assert.All(files, f => Assert.Equal("current", f.GetProperty("status").GetString()));
        Assert.Equal(afterIntent, File.ReadAllText(Path.Combine(changeDir, "intent.md")));
        Assert.Equal(afterChange, File.ReadAllText(Path.Combine(changeDir, "change.jsonc")));
    }

    [Fact]
    public void CliMigrate_RefusesWhenBothChangeYmlAndChangeJsoncExist()
    {
        using var tmp = new TempDir("hoshin-mig-conflict-");
        TestGitFixture.Write(Path.Combine(tmp.Path, "change.yml"), "version: 1\nbranch: feat/x\ntype: feat\nscope: demo\nbase: main\n");
        TestGitFixture.Write(Path.Combine(tmp.Path, "change.jsonc"), "{ \"version\": 1 }\n");

        var r = TestGitFixture.RunCli(tmp.Path, "migrate", tmp.Path, "--json");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("both change.yml and change.jsonc exist", r.Stdout);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "change.yml")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "change.jsonc")));
    }

    [Fact]
    public void WorkspaceMigrate_AlsoMigratesLegacyYamlDocumentsInWorkspace()
    {
        using var ws = TestGitFixture.CreateWorkspace();
        var changeDir = Path.Combine(ws.Master, "intent", "demo", TestGitFixture.ChangeName);
        TestGitFixture.Write(Path.Combine(changeDir, "intent.md"), """
            ---
            title: Greeting
            system: demo
            author: t
            date: 2026-11-01
            status: accepted
            scope: demo
            ---

            # Intent: Greeting
            """);
        TestGitFixture.Write(Path.Combine(changeDir, "change.yml"), """
            version: 1
            branch: "change/2026-11-01-greeting"
            type: feat
            scope: "demo"
            base: "main"
            """);

        // Dry run reports document:would and touches nothing.
        var dry = TestGitFixture.RunCli(ws.Ws, "migrate", "--json", "--dry-run");
        Assert.Equal(0, dry.ExitCode);
        var drySteps = dry.Json!.Value.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("step").GetString() == "document").ToList();
        Assert.Equal(2, drySteps.Count);
        Assert.All(drySteps, s => Assert.Equal("would", s.GetProperty("status").GetString()));
        Assert.True(File.Exists(Path.Combine(changeDir, "change.yml")));

        // --write migrates them cleanly.
        var applied = TestGitFixture.RunCli(ws.Ws, "migrate", "--json", "--write");
        Assert.Equal(0, applied.ExitCode);
        var doneSteps = applied.Json!.Value.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("step").GetString() == "document").ToList();
        Assert.Equal(2, doneSteps.Count);
        Assert.All(doneSteps, s => Assert.Equal("done", s.GetProperty("status").GetString()));
        Assert.False(File.Exists(Path.Combine(changeDir, "change.yml")));
        Assert.True(File.Exists(Path.Combine(changeDir, "change.jsonc")));
        Assert.StartsWith("```jsonc", File.ReadAllText(Path.Combine(changeDir, "intent.md")));

        // Second run has zero pending document steps.
        var second = TestGitFixture.RunCli(ws.Ws, "migrate", "--json", "--write");
        Assert.Equal(0, second.ExitCode);
        Assert.DoesNotContain(second.Json!.Value.GetProperty("steps").EnumerateArray(), s => s.GetProperty("step").GetString() == "document");
    }

    // =========================================================================
    // 4. Phase 3 Exit Gate: Real Intent Repo & All TS YAML Examples End-to-End
    // =========================================================================

    [Fact]
    public void Phase3ExitGate_RealPart2IntentRepo_MigratesCleanlyPassesCheckAndMatchesTsReadSemantics()
    {
        var part2Src = Path.Combine(TsHoshinRoot, "tools", "test", "fixtures", "part-2");
        if (!Directory.Exists(part2Src))
        {
            return;
        }

        using var tmp = new TempDir("hoshin-exit-part2-");
        var repo = Path.Combine(tmp.Path, "master");
        var changeDir = Path.Combine(repo, "intent", "kata", "2026-10-19-ai-sdlc-part-2");
        Directory.CreateDirectory(Path.Combine(changeDir, "build"));

        TestGitFixture.Write(Path.Combine(repo, ".hoshin.json"), """
            {
              "version": 1,
              "systems": {
                "kata": {
                  "repos": ["kata"]
                }
              }
            }
            """);

        // Copy real YAML Layout 1 documents verbatim from the TypeScript Hoshin repository.
        File.Copy(Path.Combine(part2Src, "spec.md"), Path.Combine(changeDir, "spec.md"));
        File.Copy(Path.Combine(part2Src, "plan.md"), Path.Combine(changeDir, "plan.md"));
        File.Copy(Path.Combine(part2Src, "build", "kata.md"), Path.Combine(changeDir, "build", "kata.md"));
        TestGitFixture.Write(Path.Combine(changeDir, "change.yml"), """
            # Written by the hoshin program (change start, change rename, change.done); do not edit by hand.
            version: 1
            branch: "change/2026-10-19-ai-sdlc-part-2"
            type: feat
            scope: "sdlc"
            base: "main"
            """);

        // Capture pre-migration semantic baseline from TypeScript hoshin.ts.
        var tsTool = Path.Combine(TsHoshinRoot, "tools", "hoshin.ts");
        var tsSpec = RunTsRead(tsTool, Path.Combine(changeDir, "spec.md"));
        var tsPlan = RunTsRead(tsTool, Path.Combine(changeDir, "plan.md"));
        var tsLog = RunTsRead(tsTool, Path.Combine(changeDir, "build", "kata.md"));

        // Run `hoshin migrate` on the copied repository.
        var mig = TestGitFixture.RunCli(tmp.Path, "migrate", repo, "--json");
        Assert.True(mig.ExitCode == 0, $"migrate failed: {mig.Stdout}\n{mig.Stderr}");
        Assert.False(File.Exists(Path.Combine(changeDir, "change.yml")));
        Assert.True(File.Exists(Path.Combine(changeDir, "change.jsonc")));

        // Run `hoshin check` across the entire migrated change (spec + plan + build log + change.jsonc).
        var check = TestGitFixture.RunCli(
            repo,
            "check",
            Path.Combine(changeDir, "spec.md"),
            Path.Combine(changeDir, "plan.md"),
            Path.Combine(changeDir, "build", "kata.md"),
            Path.Combine(changeDir, "change.jsonc"));
        Assert.True(check.ExitCode == 0, $"check reported problems:\n{check.Stdout}\n{check.Stderr}");

        // Compare `hoshin read --json` semantic output against the pre-migration TypeScript baseline.
        var netSpec = RunNetRead(repo, Path.Combine(changeDir, "spec.md"));
        var netPlan = RunNetRead(repo, Path.Combine(changeDir, "plan.md"));
        var netLog = RunNetRead(repo, Path.Combine(changeDir, "build", "kata.md"));

        Assert.Empty(netSpec.GetProperty("problems").EnumerateArray());
        Assert.Empty(netPlan.GetProperty("problems").EnumerateArray());
        Assert.Empty(netLog.GetProperty("problems").EnumerateArray());

        // Spec semantic parity: every requirement ID, title, scenario ID, title, outline, steps, and examples match identically.
        var tsReqs = tsSpec.GetProperty("document").GetProperty("requirements").EnumerateArray().ToList();
        var netReqs = netSpec.GetProperty("document").GetProperty("requirements").EnumerateArray().ToList();
        Assert.Equal(tsReqs.Count, netReqs.Count);
        for (var i = 0; i < tsReqs.Count; i++)
        {
            Assert.Equal(tsReqs[i].GetProperty("id").GetString(), netReqs[i].GetProperty("id").GetString());
            Assert.Equal(tsReqs[i].GetProperty("title").GetString(), netReqs[i].GetProperty("title").GetString());
            var tsSc = tsReqs[i].GetProperty("scenarios").EnumerateArray().ToList();
            var netSc = netReqs[i].GetProperty("scenarios").EnumerateArray().ToList();
            Assert.Equal(tsSc.Count, netSc.Count);
            for (var j = 0; j < tsSc.Count; j++)
            {
                Assert.Equal(tsSc[j].GetProperty("id").GetString(), netSc[j].GetProperty("id").GetString());
                Assert.Equal(tsSc[j].GetProperty("title").GetString(), netSc[j].GetProperty("title").GetString());
                Assert.Equal(tsSc[j].GetProperty("outline").GetBoolean(), netSc[j].GetProperty("outline").GetBoolean());
                Assert.Equal(tsSc[j].GetProperty("steps").GetRawText(), netSc[j].GetProperty("steps").GetRawText());
                Assert.Equal(tsSc[j].GetProperty("examples").GetRawText(), netSc[j].GetProperty("examples").GetRawText());
            }
        }

        // Plan semantic parity: all 30 tasks match id, name, repo, depends_on, and section titles/bodies.
        var tsTasks = tsPlan.GetProperty("document").GetProperty("tasks").EnumerateArray().ToList();
        var netTasks = netPlan.GetProperty("document").GetProperty("tasks").EnumerateArray().ToList();
        Assert.Equal(tsTasks.Count, netTasks.Count);
        for (var i = 0; i < tsTasks.Count; i++)
        {
            Assert.Equal(tsTasks[i].GetProperty("id").GetString(), netTasks[i].GetProperty("id").GetString());
            Assert.Equal(tsTasks[i].GetProperty("name").GetString(), netTasks[i].GetProperty("name").GetString());
            Assert.Equal(tsTasks[i].GetProperty("repo").GetString(), netTasks[i].GetProperty("repo").GetString());
            Assert.Equal(tsTasks[i].GetProperty("depends_on").GetRawText(), netTasks[i].GetProperty("depends_on").GetRawText());
            Assert.Equal(tsTasks[i].GetProperty("sections").GetRawText(), netTasks[i].GetProperty("sections").GetRawText());
        }

        // Build-log semantic parity: all 30 entries match task, commits, departure, and red.
        var tsEntries = tsLog.GetProperty("document").GetProperty("entries").EnumerateArray().ToList();
        var netEntries = netLog.GetProperty("document").GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(tsEntries.Count, netEntries.Count);
        for (var i = 0; i < tsEntries.Count; i++)
        {
            Assert.Equal(tsEntries[i].GetProperty("task").GetString(), netEntries[i].GetProperty("task").GetString());
            Assert.Equal(tsEntries[i].GetProperty("commits").GetRawText(), netEntries[i].GetProperty("commits").GetRawText());
            Assert.Equal(tsEntries[i].GetProperty("departure").GetRawText(), netEntries[i].GetProperty("departure").GetRawText());
            Assert.Equal(tsEntries[i].GetProperty("red").GetRawText(), netEntries[i].GetProperty("red").GetRawText());
        }

        // Second `hoshin migrate` run is 100% idempotent.
        var mig2 = TestGitFixture.RunCli(tmp.Path, "migrate", repo, "--json");
        Assert.Equal(0, mig2.ExitCode);
        Assert.All(mig2.Json!.Value.GetProperty("files").EnumerateArray(), f => Assert.Equal("current", f.GetProperty("status").GetString()));
    }

    [Fact]
    public void Phase3ExitGate_AllValidTsLayout2YamlExamples_MigrateAndMatchExpectedBaseline()
    {
        var examplesRoot = Path.Combine(TsHoshinRoot, "tools", "examples");
        if (!Directory.Exists(examplesRoot))
        {
            return;
        }

        var cases = new (string RelMd, string TargetRel, string RelExpected)[]
        {
            (@"spec\2\valid.md", @"spec\spec.md", @"spec\2\valid.expected.json"),
            (@"plan\2\valid.md", @"plan\plan.md", @"plan\2\valid.expected.json"),
            (@"plan\2\yaml-example-in-content.md", @"plan-prose\plan.md", @"plan\2\yaml-example-in-content.expected.json"),
            (@"plan\2\fenced-record-in-longer-fence.md", @"plan-fence\plan.md", @"plan\2\fenced-record-in-longer-fence.expected.json"),
            (@"build-log\2\valid.md", @"log-valid\build\app.md", @"build-log\2\valid.expected.json"),
            (@"build-log\2\continued-after-upgrade.md", @"log-hybrid\build\app.md", @"build-log\2\continued-after-upgrade.expected.json"),
            (@"build-log\2\red.md", @"log-red\build\app.md", @"build-log\2\red.expected.json"),
            (@"build-log\2\with-notes.md", @"log-notes\build\app.md", @"build-log\2\with-notes.expected.json"),
            (@"build-log\2\yaml-example-in-content.md", @"log-prose\build\app.md", @"build-log\2\yaml-example-in-content.expected.json"),
            (@"review\2\valid.md", @"rev-valid\build\review.md", @"review\2\valid.expected.json"),
            (@"review\2\with-repo-and-concern.md", @"rev-concern\build\review.md", @"review\2\with-repo-and-concern.expected.json"),
            (@"review\2\no-findings.md", @"rev-empty\build\review.md", @"review\2\no-findings.expected.json"),
        };

        using var tmp = new TempDir("hoshin-exit-examples-");
        foreach (var (relMd, targetRel, _) in cases)
        {
            var dest = Path.Combine(tmp.Path, targetRel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(Path.Combine(examplesRoot, relMd), dest);
        }

        // Also copy `change/2/valid` as a complete multi-file change.
        var validChangeSrc = Path.Combine(examplesRoot, "change", "2", "valid");
        var validChangeDest = Path.Combine(tmp.Path, "valid-change");
        Directory.CreateDirectory(Path.Combine(validChangeDest, "build"));
        File.Copy(Path.Combine(validChangeSrc, "spec.md"), Path.Combine(validChangeDest, "spec.md"));
        File.Copy(Path.Combine(validChangeSrc, "plan.md"), Path.Combine(validChangeDest, "plan.md"));
        File.Copy(Path.Combine(validChangeSrc, "build", "app.md"), Path.Combine(validChangeDest, "build", "app.md"));

        // Migrate entire tree in one command.
        var mig = TestGitFixture.RunCli(tmp.Path, "migrate", tmp.Path, "--json");
        Assert.True(mig.ExitCode == 0, $"migrate failed: {mig.Stdout}\n{mig.Stderr}");

        // Every migrated valid example passes `check` with 0 problems and matches its TS baseline semantically.
        foreach (var (relMd, targetRel, relExpected) in cases)
        {
            var file = Path.Combine(tmp.Path, targetRel);
            var chk = TestGitFixture.RunCli(tmp.Path, "check", file);
            Assert.True(chk.ExitCode == 0, $"{relMd}: check failed:\n{chk.Stdout}\n{chk.Stderr}");

            var net = RunNetRead(tmp.Path, file);
            using var expDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(examplesRoot, relExpected)));
            var exp = expDoc.RootElement;

            Assert.Equal(exp.GetProperty("kind").GetString(), net.GetProperty("kind").GetString());
            Assert.Equal(2, net.GetProperty("layout").GetInt32());
            Assert.Empty(net.GetProperty("problems").EnumerateArray());

            AssertSemanticDocumentEqual(exp.GetProperty("kind").GetString()!, exp.GetProperty("document"), net.GetProperty("document"), relMd);
        }

        // Cross-check migrated `change/2/valid` passes with 0 errors.
        var changeCheck = TestGitFixture.RunCli(
            tmp.Path,
            "check",
            Path.Combine(validChangeDest, "spec.md"),
            Path.Combine(validChangeDest, "plan.md"),
            Path.Combine(validChangeDest, "build", "app.md"));
        Assert.True(changeCheck.ExitCode == 0, $"change/2/valid check failed:\n{changeCheck.Stdout}\n{changeCheck.Stderr}");

        // Second migrate run is strictly idempotent.
        var mig2 = TestGitFixture.RunCli(tmp.Path, "migrate", tmp.Path, "--json");
        Assert.Equal(0, mig2.ExitCode);
        Assert.All(mig2.Json!.Value.GetProperty("files").EnumerateArray(), f => Assert.Equal("current", f.GetProperty("status").GetString()));
    }

    private static JsonElement RunTsRead(string tsTool, string file)
    {
        var r = ProcessCommandRunner.Default.Run(new CommandRequest(Path.GetDirectoryName(file)!, ["node", tsTool, "read", "--json", file]));
        Assert.True(r.ExitCode == 0, $"TS read failed for {file}: {r.Stdout}\n{r.Stderr}");
        using var doc = JsonDocument.Parse(r.Stdout);
        return doc.RootElement.Clone();
    }

    private static JsonElement RunNetRead(string cwd, string file)
    {
        var r = TestGitFixture.RunCli(cwd, "read", "--json", file);
        Assert.True(r.ExitCode == 0, $".NET read failed for {file}: {r.Stdout}\n{r.Stderr}");
        Assert.NotNull(r.Json);
        return r.Json!.Value;
    }

    private static string Canonical(JsonElement element) => JsonSerializer.Serialize(element);

    private static void AssertSemanticDocumentEqual(string kind, JsonElement expected, JsonElement actual, string label)
    {
        switch (kind)
        {
            case "spec":
            {
                var expReqs = expected.GetProperty("requirements").EnumerateArray().ToList();
                var actReqs = actual.GetProperty("requirements").EnumerateArray().ToList();
                Assert.Equal(expReqs.Count, actReqs.Count);
                for (var i = 0; i < expReqs.Count; i++)
                {
                    Assert.Equal(expReqs[i].GetProperty("id").GetString(), actReqs[i].GetProperty("id").GetString());
                    Assert.Equal(expReqs[i].GetProperty("title").GetString(), actReqs[i].GetProperty("title").GetString());
                    Assert.Equal(Canonical(expReqs[i].GetProperty("from")), Canonical(actReqs[i].GetProperty("from")));
                    var expSc = expReqs[i].GetProperty("scenarios").EnumerateArray().ToList();
                    var actSc = actReqs[i].GetProperty("scenarios").EnumerateArray().ToList();
                    Assert.Equal(expSc.Count, actSc.Count);
                    for (var j = 0; j < expSc.Count; j++)
                    {
                        Assert.Equal(expSc[j].GetProperty("id").GetString(), actSc[j].GetProperty("id").GetString());
                        Assert.Equal(expSc[j].GetProperty("title").GetString(), actSc[j].GetProperty("title").GetString());
                        Assert.Equal(expSc[j].GetProperty("outline").GetBoolean(), actSc[j].GetProperty("outline").GetBoolean());
                        Assert.Equal(Canonical(expSc[j].GetProperty("steps")), Canonical(actSc[j].GetProperty("steps")));
                        Assert.Equal(Canonical(expSc[j].GetProperty("examples")), Canonical(actSc[j].GetProperty("examples")));
                    }
                }

                break;
            }
            case "plan":
            {
                Assert.Equal(Canonical(expected.GetProperty("shared_decisions")), Canonical(actual.GetProperty("shared_decisions")));
                var expTasks = expected.GetProperty("tasks").EnumerateArray().ToList();
                var actTasks = actual.GetProperty("tasks").EnumerateArray().ToList();
                Assert.Equal(expTasks.Count, actTasks.Count);
                for (var i = 0; i < expTasks.Count; i++)
                {
                    foreach (var prop in new[] { "id", "name", "repo", "depends_on", "uses", "files", "covers", "supports", "test", "compile", "sections" })
                    {
                        Assert.True(
                            Canonical(expTasks[i].GetProperty(prop)) == Canonical(actTasks[i].GetProperty(prop)),
                            $"{label} task {i} property '{prop}' differed:\nexpected: {Canonical(expTasks[i].GetProperty(prop))}\nactual:   {Canonical(actTasks[i].GetProperty(prop))}");
                    }
                }

                break;
            }
            case "build-log":
            {
                var expEntries = expected.GetProperty("entries").EnumerateArray().ToList();
                var actEntries = actual.GetProperty("entries").EnumerateArray().ToList();
                Assert.Equal(expEntries.Count, actEntries.Count);
                for (var i = 0; i < expEntries.Count; i++)
                {
                    foreach (var prop in new[] { "task", "commits", "departure", "red" })
                    {
                        Assert.True(
                            Canonical(expEntries[i].GetProperty(prop)) == Canonical(actEntries[i].GetProperty(prop)),
                            $"{label} entry {i} property '{prop}' differed:\nexpected: {Canonical(expEntries[i].GetProperty(prop))}\nactual:   {Canonical(actEntries[i].GetProperty(prop))}");
                    }
                }

                break;
            }
            case "review":
            {
                Assert.Equal(Canonical(expected.GetProperty("reviews")), Canonical(actual.GetProperty("reviews")));
                break;
            }
        }
    }
}
