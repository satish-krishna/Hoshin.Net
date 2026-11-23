using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NJsonSchema;
using Xunit;

namespace Hoshin.Tests.Conformance;

public sealed class ExampleConformanceTests
{
    private static string HoshinDll => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "hoshin.dll"));
    private static string ExamplesRoot => Path.Combine(AppContext.BaseDirectory, "Examples");

    [Fact]
    public async Task EveryLayout2ExampleMatchesExpectedJsonAndReadResultSchema()
    {
        var schemaStream = typeof(Hoshin.Workflow.Common.EmbeddedSchemaValidator).Assembly
            .GetManifestResourceStream("Hoshin.Workflow.Documents.Schemas.read-result.schema.json")!;
        using var reader = new StreamReader(schemaStream);
        var schema = await JsonSchema.FromJsonAsync(await reader.ReadToEndAsync());

        var count = 0;
        foreach (var kind in new[] { "spec", "plan", "build-log", "review" })
        {
            var dir = Path.Combine(ExamplesRoot, kind, "2");
            Assert.True(Directory.Exists(dir), $"Missing examples folder: {dir}");

            foreach (var docPath in Directory.GetFiles(dir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
            {
                count++;
                var expectedPath = docPath[..^3] + ".expected.json";
                Assert.True(File.Exists(expectedPath), $"Missing expected file: {expectedPath}");
                var expectedText = await File.ReadAllTextAsync(expectedPath);
                var expectedErrors = schema.Validate(expectedText);
                Assert.Empty(expectedErrors);

                var stage = Path.Combine(Path.GetTempPath(), $"hoshin-ex-{Guid.NewGuid():N}");
                try
                {
                    var name = Path.GetFileName(docPath);
                    var rel = kind switch
                    {
                        "spec" => "spec.md",
                        "plan" => "plan.md",
                        "review" => "build/review.md",
                        _ => $"build/{name}",
                    };
                    var dest = Path.Combine(stage, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(docPath, dest);

                    var (exitCode, stdout, stderr) = RunHoshin(stage, "read", "--json", rel);
                    Assert.True(exitCode is 0 or 1, $"{docPath}: exit {exitCode}, stderr: {stderr}");

                    var actualErrors = schema.Validate(stdout);
                    Assert.True(actualErrors.Count == 0, $"{docPath}: schema errors: {string.Join("; ", actualErrors.Select(e => e.ToString()))}");

                    var actualJson = NormalizeJson(stdout);
                    var expectedJson = NormalizeJson(expectedText);
                    Assert.True(actualJson == expectedJson, $"Mismatch for {docPath}\nExpected:\n{expectedJson}\nActual:\n{actualJson}");
                }
                finally
                {
                    Directory.Delete(stage, recursive: true);
                }
            }
        }

        Assert.True(count >= 35, $"Expected at least 35 Layout 2 examples, found {count}");
    }

    [Fact]
    public void EveryLayout2ChangeExampleMatchesExpectedProblems()
    {
        var changeRoot = Path.Combine(ExamplesRoot, "change", "2");
        Assert.True(Directory.Exists(changeRoot));

        var count = 0;
        foreach (var dir in Directory.GetDirectories(changeRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            count++;
            var stage = Path.Combine(Path.GetTempPath(), $"hoshin-chg-ex-{Guid.NewGuid():N}");
            try
            {
                CopyDirectory(dir, stage);
                var files = new List<string>();
                if (File.Exists(Path.Combine(stage, "spec.md")))
                {
                    files.Add("spec.md");
                }

                if (File.Exists(Path.Combine(stage, "plan.md")))
                {
                    files.Add("plan.md");
                }
                var buildDir = Path.Combine(stage, "build");
                if (Directory.Exists(buildDir))
                {
                    files.AddRange(Directory.GetFiles(buildDir, "*.md")
                        .Select(f => "build/" + Path.GetFileName(f))
                        .OrderBy(f => f, StringComparer.Ordinal));
                }

                var args = new[] { "check" }.Concat(files).ToArray();
                var (exitCode, stdout, stderr) = RunHoshin(stage, args);
                using var expectedDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "expected.json")));
                var expected = expectedDoc.RootElement.GetProperty("problems").EnumerateArray()
                    .Select(p => $"{p.GetProperty("file").GetString()}:{p.GetProperty("line").GetInt32()}: {p.GetProperty("message").GetString()}")
                    .ToList();
                var actual = stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();

                Assert.Equal(expected, actual);
                Assert.Equal(expected.Count == 0 ? 0 : 1, exitCode);
            }
            finally
            {
                Directory.Delete(stage, recursive: true);
            }
        }

        Assert.True(count >= 2);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunHoshin(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.ArgumentList.Add(HoshinDll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, stdout, stderr);
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static string NormalizeJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(doc.RootElement, IndentedJson);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(source))
        {
            CopyDirectory(sub, Path.Combine(destination, Path.GetFileName(sub)));
        }
    }
}
