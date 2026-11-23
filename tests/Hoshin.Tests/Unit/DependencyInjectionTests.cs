using System.Text;
using Hoshin.Cli;
using Hoshin.Workflow.Changes;
using Hoshin.Workflow.Common;
using Hoshin.Workflow.Documents;
using Hoshin.Workflow.Events;
using Hoshin.Workflow.Git;
using Hoshin.Workflow.Workspace;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void FakeClock_IsHonoredByCheckLogAndHoshinServices()
    {
        var fixedNow = new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero);
        var clock = new FixedTimeProvider(fixedNow);
        using var tmp = new TempDir("hoshin-t34-clock-");
        var logFile = Path.Combine(tmp.Path, "checks.jsonl");
        var problems = new List<string>();
        var log = new CheckLog(logFile, "build.finish", "app", problems, clock);

        log.Start("unit", "head", 30);

        var line = File.ReadAllText(logFile);
        Assert.Contains("2031-04-05T06:07:08.0000000+00:00", line, StringComparison.Ordinal);

        var services = new HoshinServices(new EmbeddedSchemaValidator(), new ProcessCommandRunner(), clock, new PhysicalFileSystem());
        Assert.Same(clock, services.TimeProvider);
    }

    [Fact]
    public void InMemoryFileSystem_DrivesMarkerReaderChangeRecordFrontMatterAndScopeRulesWithoutDiskIo()
    {
        var fs = new InMemoryFileSystem();
        var master = Path.Combine("virtual", "master");
        var folder = Path.Combine(master, "intent", "demo", "2026-04-01-feat");
        var markerPath = Path.Combine(master, ".hoshin.json");
        var intentPath = Path.Combine(folder, "intent.md");

        fs.WriteAllTextAtomic(markerPath, """
            {
              "systems": {
                "demo": { "repos": ["app"], "commitScope": "custom-scope" }
              }
            }
            """);
        fs.WriteAllTextAtomic(intentPath, """
            ```jsonc
            { "layout": 2, "system": "demo", "status": "draft" }
            ```

            # Intent
            """);

        var markerReader = new MarkerReader(fs);
        var records = new ChangeRecordStore(fs);
        var fmEdit = new FrontMatterEdit(fs);
        var scopeRules = new ScopeRules(records, markerReader, fs);

        Assert.Equal("custom-scope", scopeRules.ChangeScope(master, "demo", folder));

        records.Write(folder, new ChangeRecord(1, "change/demo", "feat", "rec-scope", "main", []));
        Assert.Equal("rec-scope", scopeRules.ChangeScope(master, "demo", folder));
        Assert.Equal("change/demo", records.BranchOf(folder));

        fmEdit.Set(intentPath, "status", "accepted");
        Assert.Contains("\"status\": \"accepted\"", fs.ReadAllText(intentPath), StringComparison.Ordinal);
        Assert.False(File.Exists(markerPath));
        Assert.False(File.Exists(intentPath));
    }

    [Fact]
    public void DecoratingGitClient_CountsGitCallsWithoutProductionEnvHook()
    {
        using var ws = TestGitFixture.CreateWorkspace(built: false);
        var counter = new CountingCommandRunner(TestServices.CommandRunner);
        var git = new ProcessGitClient(counter);

        var head = git.CommitOf(ws.Master, "HEAD");
        var branch = git.CurrentBranch(ws.Master);

        Assert.NotNull(head);
        Assert.NotNull(branch);
        Assert.Equal(2, counter.GitCount);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryFileSystem : IFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);

        private static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');

        public bool FileExists(string path) => _files.ContainsKey(Norm(path));

        public bool DirectoryExists(string path) => _dirs.Contains(Norm(path));

        public string ReadAllText(string path) => _files[Norm(path)];

        public void WriteAllTextAtomic(string path, string content)
        {
            var norm = Norm(path);
            _files[norm] = content;
            var dir = Path.GetDirectoryName(norm);
            while (!string.IsNullOrEmpty(dir))
            {
                _dirs.Add(Norm(dir));
                dir = Path.GetDirectoryName(dir);
            }
        }

        public void DeleteFile(string path) => _files.Remove(Norm(path));

        public void CreateDirectory(string path) => _dirs.Add(Norm(path));

        public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*")
        {
            var prefix = Norm(path) + "\\";
            return _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<string> EnumerateDirectories(string path)
        {
            var prefix = Norm(path) + "\\";
            return _dirs.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && !d[prefix.Length..].Contains('\\', StringComparison.Ordinal));
        }
    }
}
