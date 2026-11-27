using System.Text.Json;
using System.Text.RegularExpressions;
using Hoshin.Cli.Hooks;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Engine;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Migration;
using Xunit;

namespace Hoshin.Tests.Unit;

/// <summary>
/// Focused unit tests exercising edge-case branches across pure engine, migration, text truncation,
/// classifier, and hook-detection helpers.
/// </summary>
public sealed class BranchCoverageTests
{
    private static WorkflowDocState Doc(string name, bool exists = true, bool inHead = true, bool uncommitted = false, string? working = "accepted", string? head = "accepted", string? pin = "holds") =>
        new($"/repo/.hoshin/{name}.md", exists, inHead, uncommitted, working, head, pin);

    private static WorkflowPlan SamplePlan(params WorkflowTask[] tasks) =>
        new("2026-11-22-demo", "demo", "main", tasks, [], HasPlan: true);

    private static WorkflowState MakeState(
        WorkflowPlan? plan = null,
        WorkflowDocState? intent = null,
        WorkflowDocState? spec = null,
        WorkflowDocState? planDoc = null,
        RepoLedger? ledger = null,
        ChangeStateItem? changeState = null,
        bool isLightTrack = false) =>
        new(
            "/repo",
            "/repo/.hoshin",
            plan ?? SamplePlan(new WorkflowTask("T1", "Task 1", "Do 1", ["dotnet", "test"])),
            "aaaa1111",
            ledger ?? LedgerStore.Empty(),
            null,
            intent ?? Doc("intent"),
            spec ?? Doc("spec"),
            planDoc ?? Doc("plan"),
            ChangeState: changeState,
            IsLightTrack: isLightTrack);

    private static FixFacts MakeFixFacts(
        string? intentHead = "draft",
        string? specHead = "draft",
        string? planHead = "draft",
        List<string>? unrecorded = null,
        bool ownLinesOnly = false,
        string accepted = "none",
        bool acceptanceLast = false,
        bool built = false) =>
        new(
            new DocumentFacts(intentHead is not null, intentHead, intentHead),
            new DocumentFacts(specHead is not null, specHead, specHead),
            new DocumentFacts(planHead is not null, planHead, planHead),
            unrecorded ?? [],
            ownLinesOnly,
            accepted,
            acceptanceLast,
            built);

    private static ChangeStateItem MakeChangeState(ReadinessMap? ready = null, FixStateResult? fix = null, Dictionary<string, string>? buildLogs = null)
    {
        var ok = new SkillReadiness(false, null, []);
        return new ChangeStateItem(
            "2026-11-22-demo",
            "/repo",
            "/repo/intent/demo/2026-11-22-demo",
            false,
            "Demo",
            fix is not null ? "fix" : "full",
            new DocumentsState(
                new DocumentState(true, true, "accepted", "accepted", false),
                new PinnedDocumentState(true, true, "accepted", "accepted", false, "holds"),
                new PinnedDocumentState(true, true, "accepted", "accepted", false, "holds")),
            1,
            null,
            null,
            buildLogs ?? [],
            new ReviewSummary(0, "none"),
            fix,
            [],
            ready ?? new ReadinessMap(ok, ok, ok, ok, ok, ok),
            null);
    }

    // -------------------------------------------------------------------------
    // 1. RedClassifier Windows/Node 22 escaped missing-module output
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(@"Error [ERR_MODULE_NOT_FOUND]: Cannot find module 'C:\\Users\\runneradmin\\AppData\\Local\\Temp\\app\\src\\app.mjs' imported from x")]
    [InlineData(@"error: 'Error [ERR_MODULE_NOT_FOUND]: Cannot find module \'C:\\Users\\runneradmin\\AppData\\Local\\Temp\\app\\src\\app.mjs\' imported from x'")]
    [InlineData("Error [ERR_MODULE_NOT_FOUND]: missing\n  url: 'file:///C:/Users/runneradmin/Temp/src/app.mjs'")]
    public void RedClassifier_RecognizesWindowsEscapedAndFileUrlMissingTargetModule(string output)
    {
        var res = RedClassifier.Classify(new AllowListedCommandResult(1, output, false, TimeSpan.Zero), ["src/app.mjs"]);
        Assert.Equal(RedKind.MissingSymbol, res.Kind);
        Assert.True(res.IsValidRed);
    }

    // -------------------------------------------------------------------------
    // 2. WorkflowGate & WorkflowEngine (all FixState rows, reject/reopen, post-build)
    // -------------------------------------------------------------------------

    [Fact]
    public void WorkflowEngine_And_Gate_CoverAllFixStateRowsAndStandalonePaths()
    {
        foreach (var row in new[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var facts = row switch
            {
                1 => MakeFixFacts(intentHead: "rejected"),
                2 => MakeFixFacts(planHead: "superseded"),
                3 => MakeFixFacts(unrecorded: ["spec"]),
                4 => MakeFixFacts(ownLinesOnly: true),
                5 => MakeFixFacts(intentHead: "accepted", accepted: "leading", acceptanceLast: true),
                6 => MakeFixFacts(accepted: "none"),
                7 => MakeFixFacts(intentHead: "accepted", specHead: "accepted", accepted: "leading", built: false),
                _ => MakeFixFacts(intentHead: "accepted", specHead: "accepted", planHead: "accepted", accepted: "all"),
            };
            var st = MakeState(changeState: MakeChangeState(fix: new FixStateResult(row, facts)), isLightTrack: true);
            var next = WorkflowEngine.Next(st);
            var fixGate = WorkflowEngine.Gate(st, "fix");
            var acceptGate = WorkflowEngine.GateFixAccept(st);

            if (row == 1)
            {
                Assert.IsType<Done>(next);
                Assert.NotNull(fixGate);
                Assert.NotNull(acceptGate);
            }
            else if (row == 2)
            {
                Assert.IsType<Done>(next);
                Assert.NotNull(fixGate);
                Assert.NotNull(acceptGate);
            }
            else if (row == 3)
            {
                Assert.IsType<WriteFix>(next);
                Assert.Null(fixGate);
                Assert.NotNull(acceptGate);
            }
            else if (row is 4 or 5 or 6 or 7)
            {
                Assert.IsType<AwaitAcceptFix>(next);
                Assert.Null(fixGate);
                Assert.Null(acceptGate);
            }
            else
            {
                Assert.IsType<Implement>(next);
                Assert.NotNull(acceptGate);
            }
        }

        // Row 7 with built=true refuses both Next and GateFixAccept.
        var row7Built = MakeState(changeState: MakeChangeState(fix: new FixStateResult(7, MakeFixFacts(built: true))), isLightTrack: true);
        Assert.IsType<Done>(WorkflowEngine.Next(row7Built));
        Assert.NotNull(WorkflowEngine.GateFixAccept(row7Built));

        // Standalone fix track (no ChangeState)
        var standaloneRejected = MakeState(intent: Doc("intent", head: "rejected"), isLightTrack: true);
        Assert.IsType<Done>(WorkflowEngine.Next(standaloneRejected));

        var standaloneUncommitted = MakeState(intent: Doc("intent", uncommitted: true), isLightTrack: true);
        Assert.IsType<WriteFix>(WorkflowEngine.Next(standaloneUncommitted));
        Assert.NotNull(WorkflowEngine.GateFixAccept(standaloneUncommitted));

        var standaloneSpecUnaccepted = MakeState(intent: Doc("intent", head: "accepted"), spec: Doc("spec", head: "draft"), planDoc: Doc("plan", head: "draft"), isLightTrack: true);
        var awaitSpec = Assert.IsType<AwaitAcceptFix>(WorkflowEngine.Next(standaloneSpecUnaccepted));
        Assert.Equal(WorkflowDocument.Spec, awaitSpec.NextDocument);
        Assert.Null(WorkflowEngine.GateFixAccept(standaloneSpecUnaccepted));

        var standalonePlanUnaccepted = MakeState(intent: Doc("intent", head: "accepted"), spec: Doc("spec", head: "accepted"), planDoc: Doc("plan", head: "draft"), isLightTrack: true);
        Assert.Equal(WorkflowDocument.Plan, Assert.IsType<AwaitAcceptFix>(WorkflowEngine.Next(standalonePlanUnaccepted)).NextDocument);

        var standaloneAllAccepted = MakeState(intent: Doc("intent", head: "accepted"), spec: Doc("spec", head: "accepted"), planDoc: Doc("plan", head: "accepted"), isLightTrack: true);
        Assert.IsType<Implement>(WorkflowEngine.Next(standaloneAllAccepted));
    }

    [Fact]
    public void WorkflowGate_CoversStandaloneRejectReopenAndStepOwnerRules()
    {
        Assert.Null(WorkflowEngine.Gate(MakeState(), "setup"));
        Assert.Null(WorkflowEngine.Gate(MakeState(), "context"));

        // Standalone reject: rejected, has verdicts, dirty doc, clean
        Assert.Contains("rejected", WorkflowEngine.Gate(MakeState(intent: Doc("intent", head: "rejected")), "reject")!);
        var withVerdict = LedgerStore.Empty() with { Verdicts = [new WorkflowVerdict("T1", 1, "aaaa1111", true, "observed", [], DateTimeOffset.UtcNow)] };
        Assert.Contains("build work", WorkflowEngine.Gate(MakeState(ledger: withVerdict), "reject")!);
        Assert.Contains("uncommitted", WorkflowEngine.Gate(MakeState(spec: Doc("spec", uncommitted: true)), "reject")!);
        Assert.Null(WorkflowEngine.Gate(MakeState(), "reject"));

        // Standalone reopen: rejected, has build work, dirty doc, none accepted, accepted
        Assert.Contains("rejected", WorkflowEngine.Gate(MakeState(intent: Doc("intent", head: "rejected")), "reopen")!);
        Assert.Contains("build work", WorkflowEngine.Gate(MakeState(ledger: withVerdict), "reopen")!);
        Assert.Contains("uncommitted", WorkflowEngine.Gate(MakeState(planDoc: Doc("plan", uncommitted: true)), "reopen")!);
        Assert.Contains("No accepted", WorkflowEngine.Gate(MakeState(intent: Doc("intent", head: "draft"), spec: Doc("spec", head: "draft"), planDoc: Doc("plan", head: "draft")), "reopen")!);
        Assert.Null(WorkflowEngine.Gate(MakeState(), "reopen"));

        // Review readiness stop bypass when ledger has all verdicts and only unlogged tasks remain
        var ok = new SkillReadiness(false, null, []);
        var unloggedReady = new ReadinessMap(ok, ok, ok, new SkillReadiness(false, "These tasks have no build log entry yet: `app`: T1.", []), ok, ok);
        Assert.Null(WorkflowEngine.Gate(MakeState(ledger: withVerdict, changeState: MakeChangeState(ready: unloggedReady)), "review"));

        var buildStoppedReady = new ReadinessMap(ok, ok, new SkillReadiness(false, "plan not accepted", []), ok, ok, ok);
        Assert.Equal("plan not accepted", WorkflowEngine.Gate(MakeState(changeState: MakeChangeState(ready: buildStoppedReady)), "review"));

        var otherReviewStop = new ReadinessMap(ok, ok, ok, new SkillReadiness(false, "dirty build log", []), ok, ok);
        Assert.Equal("dirty build log", WorkflowEngine.Gate(MakeState(ledger: withVerdict, changeState: MakeChangeState(ready: otherReviewStop)), "review"));

        // Direct plan rejected & draft, post-build checks fail / review high findings / approval rejected / published with URL / blocked schedule
        Assert.IsType<Done>(WorkflowEngine.Next(MakeState(intent: Doc("intent", exists: false, inHead: false), planDoc: Doc("plan", head: "rejected"))));
        Assert.IsType<AwaitAccept>(WorkflowEngine.Next(MakeState(intent: Doc("intent", exists: false, inHead: false), planDoc: Doc("plan", head: "draft"))));

        var failedChecks = withVerdict with { Checks = [new WorkflowCheckRun("aaaa1111", false, "err", DateTimeOffset.UtcNow)] };
        Assert.Contains("checks failed", Assert.IsType<Done>(WorkflowEngine.Next(MakeState(ledger: failedChecks))).Outcome);

        var passedChecks = withVerdict with { Checks = [new WorkflowCheckRun("aaaa1111", true, "ok", DateTimeOffset.UtcNow)] };
        var highReview = passedChecks with { Reviews = [new WorkflowReviewRun("aaaa1111", 1, 2, 1, DateTimeOffset.UtcNow)] };
        Assert.Contains("high findings", Assert.IsType<Done>(WorkflowEngine.Next(MakeState(ledger: highReview))).Outcome);

        var cleanReview = passedChecks with { Reviews = [new WorkflowReviewRun("aaaa1111", 1, 0, 0, DateTimeOffset.UtcNow)] };
        var rejectedApproval = cleanReview with { WorkflowApprovals = [new WorkflowApproval("aaaa1111", false, "nope", DateTimeOffset.UtcNow)] };
        var declinedFix = Assert.IsType<Implement>(WorkflowEngine.Next(MakeState(ledger: rejectedApproval)));
        Assert.Contains("nope", declinedFix.Agent.Prompt);

        var approvedAndPublished = cleanReview with
        {
            WorkflowApprovals = [new WorkflowApproval("aaaa1111", true, "ok", DateTimeOffset.UtcNow)],
            Published = [new WorkflowPublished("aaaa1111", "refs/heads/change/demo", DateTimeOffset.UtcNow, "https://github.com/o/r/pull/9")],
        };
        Assert.Contains("pull/9", Assert.IsType<Done>(WorkflowEngine.Next(MakeState(ledger: approvedAndPublished))).Outcome);
        Assert.Null(WorkflowEngine.Gate(MakeState(ledger: cleanReview), "done"));
        Assert.Null(WorkflowEngine.Gate(MakeState(ledger: cleanReview), "review"));

        // Circular cross-repo dependencies -> blocked Done
        var cyclicPlan = SamplePlan(
            new WorkflowTask("T1", "A", "a", ["t"], Repo: "app", DependsOn: ["T2"]),
            new WorkflowTask("T2", "B", "b", ["t"], Repo: "api", DependsOn: ["T1"]));
        Assert.Contains("blocked", Assert.IsType<Done>(WorkflowEngine.Next(MakeState(plan: cyclicPlan))).Outcome);

        // Describe & OwnerOf coverage
        Assert.Equal("accept", WorkflowEngine.OwnerOf(new AwaitAcceptFix("/c", "h", WorkflowDocument.Intent)));
        Assert.Equal("fix", WorkflowEngine.OwnerOf(new WriteFix("/c", "b")));
        Assert.Contains("fix --accept", WorkflowEngine.Describe(new AwaitAcceptFix("/c", "h", WorkflowDocument.Spec)));
        Assert.Contains("drafting the light-track", WorkflowEngine.Describe(new WriteFix("/c", "b")));
        Assert.Contains("reviewing", WorkflowEngine.Describe(new ReviewDocument("/r", "h", "b")));
    }

    // -------------------------------------------------------------------------
    // 3. TextTruncator edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void TextTruncator_CoversSmallBoundsSurrogatesAndPrefix()
    {
        Assert.Equal("", TextTruncator.TruncateMiddle("hello", 0));
        Assert.Equal("", TextTruncator.TruncateMiddle("hello", -5));
        Assert.Equal("hello", TextTruncator.TruncateMiddle("hello", 5));
        Assert.Equal("ab", TextTruncator.TruncateMiddle("abcdef", 2));
        Assert.Equal("", TextTruncator.TruncatePrefix("hello", 0));
        Assert.Equal("hello", TextTruncator.TruncatePrefix("hello", 10));
        Assert.Equal("a😀", TextTruncator.TruncatePrefix("a😀b", 3));
        Assert.Equal("a", TextTruncator.TruncatePrefix("a😀b", 2));

        var longEmoji = string.Concat(Enumerable.Repeat("😀", 60));
        var mid = TextTruncator.TruncateMiddle(longEmoji, 60);
        Assert.Contains("characters omitted", mid);
        Assert.True(mid.Length <= 60);
    }

    // -------------------------------------------------------------------------
    // 4. TestPathClassifier edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void TestPathClassifier_CoversDoubleStarWildcardsAndRelativeNormalization()
    {
        Assert.True(TestPathClassifier.MatchesGlob("src/a/b/foo.cs", "src/**"));
        Assert.True(TestPathClassifier.MatchesGlob("src/a/b/foo.cs", "src/**/*.cs"));
        Assert.True(TestPathClassifier.MatchesGlob("src/a1.test.js", "src/a?.test.js"));
        Assert.True(TestPathClassifier.MatchesGlob("src/foo/bar", "src/**bar"));
        Assert.False(TestPathClassifier.MatchesGlob("src/a12.test.js", "src/a?.test.js"));

        var extracted = TestPathClassifier.ExtractTestFilesFromArgv(
            ["node", "--test", "./test/a.test.mjs", "../escape.test.mjs", "/abs/a.test.mjs", "C:/win/a.test.mjs", "test/*.test.mjs", "", "README.md"],
            repoRoot: null);
        Assert.Equal(["test/a.test.mjs"], extracted);
    }

    // -------------------------------------------------------------------------
    // 5. SkillInvocationDetector edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void SkillInvocationDetector_CoversPrefixesSkillFilesAndNonMatches()
    {
        var adapter = TestServices.HookAdapter;

        // Antigravity unhandled event
        var agyStop = adapter.Parse("""{"hookEventName":"Stop","conversationId":"c1","cwd":"/r","fullyIdle":true}""");
        Assert.Null(SkillInvocationDetector.Detect(agyStop));

        // Antigravity PostToolUse read_file with IsSkillFile=true on generic skills/<name>/SKILL.md
        var agyGenericSkill = adapter.Parse("""{"hookEventName":"PostToolUse","conversationId":"c1","cwd":"/r","toolCall":{"id":"1","name":"view_file","args":{"AbsolutePath":"/p/skills/plan/SKILL.md","IsSkillFile":true}}}""");
        var detected = SkillInvocationDetector.Detect(agyGenericSkill);
        Assert.NotNull(detected);
        Assert.Equal("plan", detected!.Skill);
        Assert.Equal(SkillInvocationSource.Model, detected.Source);

        // Antigravity PostToolUse read_file with non-skill file
        var agyOtherFile = adapter.Parse("""{"hookEventName":"PostToolUse","conversationId":"c1","cwd":"/r","toolCall":{"id":"1","name":"read_file","args":{"Path":"/p/README.md"}}}""");
        Assert.Null(SkillInvocationDetector.Detect(agyOtherFile));

        // Antigravity PreInvocation blank / non-slash / hoshin-net: prefix / unknown
        Assert.Null(SkillInvocationDetector.Detect(adapter.Parse("""{"hookEventName":"PreInvocation","conversationId":"c1","cwd":"/r","lastUserInput":"   "}""")));
        Assert.Null(SkillInvocationDetector.Detect(adapter.Parse("""{"hookEventName":"PreInvocation","conversationId":"c1","cwd":"/r","lastUserInput":"hello"}""")));
        var agyPrefixed = SkillInvocationDetector.Detect(adapter.Parse("""{"hookEventName":"PreInvocation","conversationId":"c1","cwd":"/r","lastUserInput":"/hoshin-net:spec arg1"}"""));
        Assert.Equal("spec", agyPrefixed?.Skill);
        Assert.Equal("arg1", agyPrefixed?.Args);

        // Claude Code missing command_name / unknown skill / hoshin-net: prefix
        Assert.Null(SkillInvocationDetector.Detect(adapter.Parse("""{"hook_event_name":"UserPromptExpansion","session_id":"s1","cwd":"/r"}""")));
        Assert.Null(SkillInvocationDetector.Detect(adapter.Parse("""{"hook_event_name":"UserPromptExpansion","session_id":"s1","cwd":"/r","command_name":"other:foo"}""")));
        var ccPrefixed = SkillInvocationDetector.Detect(adapter.Parse("""{"hook_event_name":"UserPromptExpansion","session_id":"s1","cwd":"/r","command_name":"hoshin-net:review"}"""));
        Assert.Equal("review", ccPrefixed?.Skill);
    }

    // -------------------------------------------------------------------------
    // 6. GuardTamperingProtection edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void GuardTamperingProtection_CoversShellAndMissingRunMarkerCases()
    {
        var writes = new Regex(@">|\brm\b|\btee\b", RegexOptions.IgnoreCase);
        Assert.Contains(".git/hooks", GuardTamperingProtection.CheckShellCommand("echo x > .git/hooks/pre-commit", writes, null)!);
        Assert.Null(GuardTamperingProtection.CheckShellCommand("cat .git/hooks/pre-commit", writes, null));

        using var tmp = new TempDir("hoshin-cov-tamper-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string> { ["README.md"] = "# hi\n" });
        var repo = TestServices.FindRepo(repoDir)!;

        // References repo.HoshinDir directly
        Assert.Contains(repo.HoshinDir, GuardTamperingProtection.CheckShellCommand($"rm -rf \"{repo.HoshinDir}\"", writes, repo)!);

        // Missing run.json with corrupt ledger -> tampering detected for owning session
        var stopEv = TestServices.HookAdapter.Parse("""{"hook_event_name":"Stop","session_id":"s1","stop_hook_active":false}""");
        Directory.CreateDirectory(repo.HoshinDir);
        repo.WriteStep(new CurrentStep(StepKind.Implement, "T1", 1, repo.Head(), DateTimeOffset.UtcNow, SessionId: "s1", ChangeFolder: repo.ChangeFolder));
        Directory.CreateDirectory(repo.LedgerFolder);
        File.WriteAllText(repo.LedgerPath, "{ not valid json");
        Assert.Contains("could not be read", GuardTamperingProtection.DetectMissingRunMarkerTampering(repo, stopEv, TestServices.WorkflowStateReader, null)!);

        // Empty ledger (no records) -> no tampering
        repo.WriteLedger(LedgerStore.Empty());
        Assert.Null(GuardTamperingProtection.DetectMissingRunMarkerTampering(repo, stopEv, TestServices.WorkflowStateReader, null));

        // Ledger with records but no plan (state == null) -> returns null
        repo.WriteLedger(LedgerStore.Empty() with { Verdicts = [new WorkflowVerdict("T1", 1, "abc", true, "ok", [], DateTimeOffset.UtcNow)] });
        Assert.Null(GuardTamperingProtection.DetectMissingRunMarkerTampering(repo, stopEv, TestServices.WorkflowStateReader, null));
    }

    // -------------------------------------------------------------------------
    // 7. FrontMatterMigrator & YamlJsoncEmitter edge cases
    // -------------------------------------------------------------------------

    [Fact]
    public void Migration_FrontMatterAndYamlEmitter_CoverEdgeCases()
    {
        var converter = new YamlToJsoncConverter();
        var migrator = new DocumentMigrator(converter);

        // Unclosed YAML front matter fails closed
        Assert.Throws<FormatException>(() =>
            migrator.MigrateMarkdownText("---\nlayout: 2\nsystem: demo\n# never closed\n", "spec.md", out _));

        // Existing JSONC front matter upgraded to Layout 2 across empty/inline/multiline/layout:1 forms
        var emptyOut = migrator.MigrateMarkdownText("```jsonc\n{}\n```\n\n# Build log\n", "build/app.md", out var emptyChanged);
        Assert.True(emptyChanged);
        Assert.Contains("\"layout\": 2", emptyOut);

        var inlineOut = migrator.MigrateMarkdownText("```jsonc\n{ \"repo\": \"web\" }\n```\n\n# Build log\n", "build/web.md", out var inlineChanged);
        Assert.True(inlineChanged);
        Assert.Contains("\"layout\": 2", inlineOut);

        var layout1Out = migrator.MigrateMarkdownText("```jsonc\n{\n  \"layout\": 1,\n  \"repo\": \"api\"\n}\n```\n\n# Build log\n", "build/api.md", out var l1Changed);
        Assert.True(l1Changed);
        Assert.Contains("\"layout\": 2", layout1Out);

        var multiOut = migrator.MigrateMarkdownText("```jsonc\n{\n  \"repo\": \"svc\"\n}\n```\n\n# Build log\n", "build/svc.md", out var multiChanged);
        Assert.True(multiChanged);
        Assert.Contains("\"layout\": 2", multiOut);

        var noFmOut = migrator.MigrateMarkdownText("# Build log without front matter\n", "build/raw.md", out var noFmChanged);
        Assert.True(noFmChanged);
        Assert.Contains("\"layout\": 2", noFmOut);

        // YamlJsoncEmitter: empty, comment-only (inside & outside), empty mapping with comment, root sequence, root scalar
        Assert.Equal("{}", converter.ConvertBlock(""));
        Assert.Equal("[]", converter.ConvertBlock("", new YamlConversionOptions(ExpectArray: true)));
        Assert.Contains("// note", converter.ConvertBlock("# note\n", new YamlConversionOptions(RootLeadingCommentsOutside: true)));
        Assert.Contains("// note", converter.ConvertBlock("# note\n", new YamlConversionOptions(RootLeadingCommentsOutside: false)));
        Assert.Contains("// seq", converter.ConvertBlock("# seq\n", new YamlConversionOptions(ExpectArray: true)));
        Assert.Equal("[\n  \"a\",\n  \"b\"\n]", converter.ConvertBlock("- a\n- b\n", new YamlConversionOptions(ExpectArray: true)).Replace("\r\n", "\n"));
        Assert.Equal("\"scalar\"", converter.ConvertBlock("scalar\n"));
        Assert.Contains("// inside", converter.ConvertBlock("{}\n# inside\n"));
    }

    // -------------------------------------------------------------------------
    // 8. BaseBranchChecksReader object-shaped hooks & invalid JSONC
    // -------------------------------------------------------------------------

    [Fact]
    public void BaseBranchChecksReader_ReadsObjectShapedHooksAndRefusesInvalidJsonc()
    {
        using var tmp = new TempDir("hoshin-cov-checks-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        TestGitFixture.InitRepo(repoDir, new Dictionary<string, string>
        {
            [".hoshin/hooks.jsonc"] = """
                {
                  "hooks": {
                    "build.finish": [
                      { "id": "lint", "run": ["npm", "run", "lint"] },
                      { "id": "soft", "blocking": false, "run": ["echo", "skip"] }
                    ]
                  }
                }
                """,
        });
        var repo = TestServices.FindRepo(repoDir)!;
        var checks = BaseBranchChecksReader.Read(repo, "main", "demo");
        var only = Assert.Single(checks);
        Assert.Equal(["npm", "run", "lint"], only);

        TestGitFixture.Write(Path.Combine(repoDir, ".hoshin", "hooks.jsonc"), "{ broken ");
        TestGitFixture.Git(repoDir, "add", ".");
        TestGitFixture.Git(repoDir, "commit", "-q", "-m", "break hooks");
        Assert.Throws<CouldNotRunException>(() => BaseBranchChecksReader.Read(repo, "main", "demo"));
    }

    // -------------------------------------------------------------------------
    // 9. TaskVerdictPolicy error/rollback branches with fakes (zero processes)
    // -------------------------------------------------------------------------

    private sealed class StubGitCommitter(Func<IReadOnlyList<string>, GitCommitOutcome> onCommit) : IGitCommitter
    {
        public int Resets { get; private set; }
        public GitCommitOutcome CommitPaths(string dir, IReadOnlyList<string> paths, IReadOnlyList<string> paragraphs) => onCommit(paths);
        public GitResult ResetMixed(string dir, string rev) { Resets++; return new GitResult(true, 0, "", ""); }
        public GitResult Push(string dir, string remote, string refSpec) => new(true, 0, "", "");
    }

    private sealed class StubGitWorktrees : IGitWorktrees
    {
        public string? TopLevel(string dir) => dir;
        public string MainCheckout(string dir) => dir;
        public GitDirsInfo? GitDirs(string dir) => new(Path.Combine(dir, ".git"), Path.Combine(dir, ".git"));
        public string GitPath(string dir, string path, bool common = false) => Path.Combine(dir, ".git", path);
        public List<GitWorktree> Worktrees(string dir) => [];
        public string? CheckedOutIn(string dir, string branch) => null;
        public GitResult EnsureDetachedWorktree(string repoRoot, string worktreeDir, string rev) => new(true, 0, "", "");
        public GitResult CleanDetachedWorktree(string worktreeDir, string rev) => new(true, 0, "", "");
    }

    private sealed class SequenceRunner(params AllowListedCommandResult[] results) : IAllowListedCommandRunner
    {
        private readonly Queue<AllowListedCommandResult> _queue = new(results);
        public AllowListedCommandResult Run(string workingDirectory, IReadOnlyList<string> argv, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? extraEnv = null) =>
            _queue.Count > 0 ? _queue.Dequeue() : new AllowListedCommandResult(0, "ok", false, TimeSpan.Zero);
    }

    [Fact]
    public void TaskVerdictPolicies_CoverAllFailureAndRollbackBranchesWithoutProcesses()
    {
        using var tmp = new TempDir("hoshin-cov-policy-");
        var repoDir = Path.Combine(tmp.Path, "repo");
        Directory.CreateDirectory(repoDir);
        var repo = new WorkflowRepo(repoDir, Path.Combine(repoDir, ".git"), Path.Combine(repoDir, ".hoshin"), TestServices.Git, TestServices.Git, TestServices.FileSystem, TestServices.Ledger);
        var now = DateTimeOffset.UtcNow;
        var wt = new StubGitWorktrees();

        // NoTestPolicy: commit failure resets mixed
        var noTestTask = new WorkflowTask("T1", "Docs", "Update docs", Test: null);
        var noTestState = MakeState(plan: SamplePlan(noTestTask));
        var failCommitter = new StubGitCommitter(_ => new GitCommitOutcome(false, null, "hook failed", ""));
        var noTestRes = new NoTestPolicy().EvaluateAndCommit(new TaskEvaluationContext(
            repo, noTestState, noTestTask, 1, "start", [new WorkingTreeChange("docs/a.md", false, false)], wt, failCommitter, new SequenceRunner(), now));
        Assert.False(noTestRes.Passed);
        Assert.Equal(1, failCommitter.Resets);

        // RedGreenPolicy: missing test & missing prod partition
        var rgTask = new WorkflowTask("T1", "Code", "Add code", Test: ["dotnet", "test"], Compile: ["dotnet", "build"]);
        var rgState = MakeState(plan: SamplePlan(rgTask));
        var rgPolicy = new RedGreenPolicy();

        var missingBoth = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", [], wt, failCommitter, new SequenceRunner(), now));
        Assert.False(missingBoth.Passed);
        Assert.Equal(2, missingBoth.Reasons.Count);

        var bothChanges = new List<WorkingTreeChange>
        {
            new("tests/T1Tests.cs", false, false),
            new("src/Code.cs", false, false),
        };

        // Compile failure & compile timeout
        var compileFail = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", bothChanges, wt, failCommitter,
            new SequenceRunner(new AllowListedCommandResult(1, "CS1002", false, TimeSpan.Zero)), now));
        Assert.Contains("compile:", compileFail.Reasons[0]);

        var compileTimeout = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", bothChanges, wt, failCommitter,
            new SequenceRunner(new AllowListedCommandResult(-1, "", true, TimeSpan.FromSeconds(5))), now));
        Assert.Contains("timed out", compileTimeout.Reasons[0]);

        // Green pre-check failure & timeout
        var greenPreFail = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", bothChanges, wt, failCommitter,
            new SequenceRunner(
                new AllowListedCommandResult(0, "compiled", false, TimeSpan.Zero),
                new AllowListedCommandResult(1, "failed test", false, TimeSpan.Zero)), now));
        Assert.Contains("green:", greenPreFail.Reasons[0]);

        // Test commit failure -> reset
        var testCommitFail = new StubGitCommitter(_ => new GitCommitOutcome(false, null, "bad test commit", ""));
        var resTestCommit = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", bothChanges, wt, testCommitFail,
            new SequenceRunner(
                new AllowListedCommandResult(0, "compiled", false, TimeSpan.Zero),
                new AllowListedCommandResult(0, "green", false, TimeSpan.Zero),
                new AllowListedCommandResult(0, "compiled red", false, TimeSpan.Zero),
                new AllowListedCommandResult(1, "Assert.Equal failed", false, TimeSpan.Zero)), now));
        Assert.False(resTestCommit.Passed);
        Assert.Equal(1, testCommitFail.Resets);

        // Prod commit failure -> reset
        var commitCount = 0;
        var prodCommitFail = new StubGitCommitter(_ => ++commitCount == 1
            ? new GitCommitOutcome(true, "testsha", null, "")
            : new GitCommitOutcome(false, null, "bad prod commit", ""));
        var resProdCommit = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", bothChanges, wt, prodCommitFail,
            new SequenceRunner(
                new AllowListedCommandResult(0, "compiled", false, TimeSpan.Zero),
                new AllowListedCommandResult(0, "green", false, TimeSpan.Zero),
                new AllowListedCommandResult(0, "compiled red", false, TimeSpan.Zero),
                new AllowListedCommandResult(1, "Assert.Equal failed", false, TimeSpan.Zero)), now));
        Assert.False(resProdCommit.Passed);
        Assert.Equal(1, prodCommitFail.Resets);

        // Green HEAD failure -> reset
        var okCommitter = new StubGitCommitter(_ => new GitCommitOutcome(true, "sha12345", null, ""));
        var resGreenHead = rgPolicy.EvaluateAndCommit(new TaskEvaluationContext(
            repo, rgState, rgTask, 1, "start", bothChanges, wt, okCommitter,
            new SequenceRunner(
                new AllowListedCommandResult(0, "compiled", false, TimeSpan.Zero),
                new AllowListedCommandResult(0, "green", false, TimeSpan.Zero),
                new AllowListedCommandResult(0, "compiled red", false, TimeSpan.Zero),
                new AllowListedCommandResult(1, "Assert.Equal failed", false, TimeSpan.Zero),
                new AllowListedCommandResult(1, "green at head failed", false, TimeSpan.Zero)), now));
        Assert.False(resGreenHead.Passed);
        Assert.Equal(1, okCommitter.Resets);
    }
}
