using System.Text.Json;
using Hoshin.Cli;
using Hoshin.Tests.Architecture.Samples;
using Hoshin.Workflow.Hosting;
using Hoshin.Workflow.Engine;
using Xunit;
using Xunit.Abstractions;

namespace Hoshin.Tests.Architecture;

public sealed class ArchitectureTests(ITestOutputHelper output)
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SrcRoot = Path.Combine(RepoRoot, "src");
    private static readonly string BaselinePath = Path.Combine(RepoRoot, "tests", "Hoshin.Tests", "Architecture", "baseline.json");

    // =========================================================================
    // 1. Sample-driven tests proving each rule detects violations
    // =========================================================================

    [Fact]
    public void Rule_A1_ReportsDisallowedAssemblyReference()
    {
        // Hoshin.Tests references xunit/NJsonSchema/Hoshin.Cli — passing it as a "workflow" assembly must report violations.
        var violations = ArchitectureRules.CheckA1(typeof(ArchitectureTests).Assembly);
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal("A1", v.Rule));
    }

    [Fact]
    public void Rule_A2_ReportsHarnessPayloadOutsideHooksOrCommands()
    {
        var sample = new SourceFile("src/Hoshin.Workflow/Engine/Sample.cs", "class X { const string K = \"hook_event_name\"; }");
        var violations = ArchitectureRules.CheckA2([sample]);
        Assert.Contains(violations, v => v.FullKey == "A2:src/Hoshin.Workflow/Engine/Sample.cs#hook_event_name");
    }

    [Fact]
    public void Rule_S2_ReportsImpureCallsInPureEngineFiles()
    {
        var sample = new SourceFile("src/Hoshin.Workflow/Engine/WorkflowEngine.cs", "class X { void M() { var t = DateTimeOffset.UtcNow; File.ReadAllText(\"a\"); } }");
        var violations = ArchitectureRules.CheckS2([sample]);
        Assert.Contains(violations, v => v.FullKey.StartsWith("S2:", StringComparison.Ordinal) && v.Key.Contains("DateTimeOffset.UtcNow", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.FullKey.StartsWith("S2:", StringComparison.Ordinal) && v.Key.Contains("File.", StringComparison.Ordinal));
    }

    [Fact]
    public void Rule_S3_ReportsFilesOver400Lines()
    {
        var sample = new SourceFile("src/Hoshin.Workflow/TooLong.cs", string.Join('\n', Enumerable.Repeat("// line", 401)));
        var violations = ArchitectureRules.CheckS3FileLength([sample]);
        Assert.Contains(violations, v => v.FullKey == "S3:src/Hoshin.Workflow/TooLong.cs");
    }

    [Fact]
    public void Rule_S3_ReportsCa1502Suppressions()
    {
        var sample = new SourceFile(
            "src/Hoshin.Workflow/Complex.cs",
            "[SuppressMessage(\"Maintainability\", \"CA1502:Avoid excessive complexity\", Justification = \"test\")]\npublic void TooComplex() {}");
        var violations = ArchitectureRules.CheckS3Ca1502([sample]);
        Assert.Contains(violations, v => v.FullKey == "CA1502:src/Hoshin.Workflow/Complex.cs#TooComplex");
    }

    [Fact]
    public void Rule_I1_ReportsInterfaceWithMoreThan10Members()
    {
        var violations = ArchitectureRules.CheckI1ForTypes([typeof(IFatSampleInterface)]);
        Assert.Single(violations);
        Assert.Equal("I1", violations[0].Rule);
    }

    [Fact]
    public void Rule_I2_ReportsCommandConstructorTakingHoshinServices()
    {
        var violations = ArchitectureRules.CheckI2ForTypes([typeof(SampleCommandTakingServices)]);
        Assert.Single(violations);
        Assert.Equal("I2", violations[0].Rule);
    }

    [Fact]
    public void Rule_D1_ReportsDirectClockAccessOutsideClockAdapter()
    {
        var sample = new SourceFile("src/Hoshin.Workflow/Engine/Sample.cs", "class X { object M() => DateTimeOffset.UtcNow; }");
        var violations = ArchitectureRules.CheckD1([sample]);
        Assert.Contains(violations, v => v.FullKey == "D1:src/Hoshin.Workflow/Engine/Sample.cs#DateTimeOffset.UtcNow");
    }

    [Fact]
    public void Rule_D2_ReportsOptionalConstructorDependency()
    {
        var violations = ArchitectureRules.CheckD2ForTypes([typeof(SampleOptionalDependency)]);
        Assert.Single(violations);
        Assert.Equal("D2", violations[0].Rule);
    }

    [Fact]
    public void Rule_D3_ReportsCoalesceFallbackAndUnlistedStaticDefault()
    {
        var sample = new SourceFile("src/Hoshin.Workflow/Sample.cs", "class X { object _a = x ?? new Foo(); object _b = y ?? Bar.Default; }");
        var fallbacks = ArchitectureRules.CheckD3Fallbacks([sample]);
        Assert.Contains(fallbacks, v => v.Key.EndsWith("#new Foo", StringComparison.Ordinal));
        Assert.Contains(fallbacks, v => v.Key.EndsWith("#Bar.Default", StringComparison.Ordinal));

        var defaults = ArchitectureRules.CheckD3DefaultPropertiesForTypes([typeof(SampleStaticDefault)]);
        Assert.Single(defaults);
        Assert.Equal("D3", defaults[0].Rule);
    }

    [Fact]
    public void Rule_D4_ReportsMutableStaticField()
    {
        var violations = ArchitectureRules.CheckD4ForTypes([typeof(SampleMutableStatic)]);
        Assert.Single(violations);
        Assert.Equal("D4", violations[0].Rule);
    }

    [Fact]
    public void Rule_C3_ReportsSilentCatchBlock()
    {
        var sample = new SourceFile("src/Hoshin.Workflow/Sample.cs", "class X { void M() { try { } catch { } try { } catch (Exception) { return; } } }");
        var violations = ArchitectureRules.CheckC3([sample]);
        Assert.Equal(2, violations.Count);
        Assert.All(violations, v => Assert.Equal("C3", v.Rule));
    }

    [Fact]
    public void Rule_BaselineDiff_ReportsNewViolationsAndStaleEntries()
    {
        var baseline = new HashSet<string>(StringComparer.Ordinal) { "D1:old.cs#DateTime.Now", "D1:still.cs#DateTime.Now" };
        var actual = new Dictionary<string, ArchitectureViolation>(StringComparer.Ordinal)
        {
            ["D1:still.cs#DateTime.Now"] = new("D1", "still.cs#DateTime.Now", "still there"),
            ["D1:new.cs#DateTime.UtcNow"] = new("D1", "new.cs#DateTime.UtcNow", "new violation"),
        };

        var errors = CompareBaseline(baseline, actual);
        Assert.Equal(2, errors.Count);
        Assert.Contains("CONTRIBUTING.md", errors[0], StringComparison.Ordinal);
        Assert.Contains("D1:new.cs#DateTime.UtcNow", errors[0], StringComparison.Ordinal);
        Assert.Contains("remove it from the baseline", errors[1], StringComparison.Ordinal);
        Assert.Contains("D1:old.cs#DateTime.Now", errors[1], StringComparison.Ordinal);
    }

    private static List<string> CompareBaseline(
        HashSet<string> baseline,
        Dictionary<string, ArchitectureViolation> actualByKey)
    {
        var added = actualByKey.Keys
            .Where(k => !baseline.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var stale = baseline
            .Where(k => !actualByKey.ContainsKey(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var messages = new List<string>();
        if (added.Count > 0)
        {
            messages.Add(
                $"Found {added.Count} new architecture violation(s) not in baseline.json (see CONTRIBUTING.md §3–§5):\n" +
                string.Join("\n", added.Select(k => $"  + {k} — {actualByKey[k].Detail}")));
        }

        if (stale.Count > 0)
        {
            messages.Add(
                $"Found {stale.Count} fixed baseline entry/entries that no longer violate — remove it from the baseline (tests/Hoshin.Tests/Architecture/baseline.json):\n" +
                string.Join("\n", stale.Select(k => $"  - {k}")));
        }

        return messages;
    }

    // =========================================================================
    // 2. Shrinking baseline gate over src/
    // =========================================================================

    [Fact]
    public void ArchitectureBaseline_MatchesCurrentCodeWithNoNewAndNoStaleViolations()
    {
        var sourceFiles = Directory.GetFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new SourceFile(Path.GetRelativePath(RepoRoot, p).Replace('\\', '/'), File.ReadAllText(p)))
            .ToList();

        Assert.True(File.Exists(BaselinePath), $"Missing architecture baseline file: {BaselinePath}");
        using var doc = JsonDocument.Parse(File.ReadAllText(BaselinePath));
        var allowList = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("defaultPropertyAllowList", out var allowProp))
        {
            foreach (var p in allowProp.EnumerateObject())
            {
                allowList[p.Name] = p.Value.GetString() ?? "";
            }
        }

        var baseline = doc.RootElement.GetProperty("violations")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var actual = ArchitectureRules.EvaluateAll(
            typeof(WorkflowEngine).Assembly,
            typeof(Hoshin.Cli.Program).Assembly,
            sourceFiles,
            allowList);

        var actualByKey = actual
            .GroupBy(v => v.FullKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var countsByRule = actualByKey.Values
            .GroupBy(v => v.Rule, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}");
        output.WriteLine($"Architecture baseline counts (total {actualByKey.Count}): {string.Join(", ", countsByRule)}");

        var messages = CompareBaseline(baseline, actualByKey);
        Assert.True(messages.Count == 0, string.Join("\n\n", messages));
    }
}
