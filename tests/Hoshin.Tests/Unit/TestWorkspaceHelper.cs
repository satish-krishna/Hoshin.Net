using System.Diagnostics;
using System.Text.Json;
using Hoshin.Cli;
using Hoshin.Workflow.Git;
using Xunit;

namespace Hoshin.Tests.Unit;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix)
    {
        var raw = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(raw);
        Path = PathHelpers.RealPath(raw);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    RemoveReadOnly(Path);
                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void RemoveReadOnly(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch
            {
                // Ignore best-effort cleanup errors.
            }
        }
    }
}

internal sealed class CountingCommandRunner(ICommandRunner inner) : ICommandRunner
{
    public int GitCount { get; private set; }

    public CommandOutcome Run(CommandRequest request)
    {
        if (request.Argv.Count > 0 && string.Equals(request.Argv[0], "git", StringComparison.OrdinalIgnoreCase))
        {
            GitCount++;
        }

        return inner.Run(request);
    }

    public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Argv.Count > 0 && string.Equals(request.Argv[0], "git", StringComparison.OrdinalIgnoreCase))
        {
            GitCount++;
        }

        return inner.RunAsync(request, cancellationToken);
    }
}

internal static class TestGitFixture
{
    public const string ChangeName = "2026-11-01-greeting";
    public const string IntentText = """
        ```jsonc
        {
          "title": "Greeting",
          "system": "demo",
          "author": "t",
          "date": "2026-11-01",
          "status": "accepted",
          "scope": "demo"
        }
        ```

        # Intent: Greeting
        """;

    public static readonly IReadOnlyDictionary<string, string> GitEnv = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = "t",
        ["GIT_AUTHOR_EMAIL"] = "t@example.com",
        ["GIT_COMMITTER_NAME"] = "t",
        ["GIT_COMMITTER_EMAIL"] = "t@example.com",
        ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        ["GIT_CONFIG_NOSYSTEM"] = "1",
    };

    public static string Git(string cwd, params string[] args)
    {
        var r = ProcessCommandRunner.Default.Run(new CommandRequest(cwd, ["git", .. args], ExtraEnvironment: GitEnv));
        Assert.Equal(0, r.ExitCode);
        return r.Stdout.Trim();
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static string Commit(string dir, Dictionary<string, string> files, string message = "docs(demo): change")
    {
        foreach (var (name, text) in files)
        {
            Write(Path.Combine(dir, name), text);
        }

        Git(dir, "add", ".");
        Git(dir, "commit", "-q", "-m", message, "-m", "Generated-By: hoshin");
        return Git(dir, "rev-parse", "HEAD");
    }

    public static string InitRepo(string path, Dictionary<string, string>? files = null)
    {
        Directory.CreateDirectory(path);
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test User");
        Git(path, "config", "user.email", "test@example.com");
        foreach (var (name, text) in files ?? new Dictionary<string, string> { ["README.md"] = "# repo\n" })
        {
            Write(Path.Combine(path, name), text);
        }

        Git(path, "add", ".");
        Git(path, "commit", "-q", "-m", "chore: initial");
        var remote = path + ".remote.git";
        Git(Path.GetDirectoryName(path)!, "init", "-q", "--bare", "-b", "main", remote);
        Git(path, "remote", "add", "origin", remote);
        Git(path, "push", "-q", "-u", "origin", "main");
        Git(path, "remote", "set-head", "origin", "main");
        return path;
    }

    public sealed record WorkspaceFixture(TempDir Temp, string Ws, string Master, string App) : IDisposable
    {
        public void Dispose() => Temp.Dispose();
    }

    public sealed record SingleRepoFixture(TempDir Temp, string Root, string Workspace, string Repo, string Remote) : IDisposable
    {
        public string Git(params string[] args) => TestGitFixture.Git(Repo, args);
        public void Dispose() => Temp.Dispose();
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public static WorkspaceFixture CreateWorkspace(object? markerExtra = null, string[]? repos = null, bool built = false)
    {
        var temp = new TempDir("hoshin-ws-");
        var repoList = repos ?? ["app"];
        var markerObj = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["systems"] = new Dictionary<string, object?>
            {
                ["demo"] = new Dictionary<string, object?> { ["repos"] = repoList },
            },
        };
        if (markerExtra is Dictionary<string, object?> extra)
        {
            foreach (var (k, v) in extra)
            {
                markerObj[k] = v;
            }
        }

        var folder = $"intent/demo/{ChangeName}";
        var masterFiles = new Dictionary<string, string>
        {
            [".hoshin.json"] = JsonSerializer.Serialize(markerObj, IndentedJson) + "\n",
            [$"{folder}/intent.md"] = IntentText + "\n",
        };
        if (built)
        {
            var examplesDir = Path.Combine(AppContext.BaseDirectory, "Examples", "change", "2", "valid");
            masterFiles[$"{folder}/spec.md"] = File.ReadAllText(Path.Combine(examplesDir, "spec.md"));
            masterFiles[$"{folder}/plan.md"] = File.ReadAllText(Path.Combine(examplesDir, "plan.md"));
            masterFiles[$"{folder}/build/app.md"] = BuildLog(["T1", "T2", "T3"]);
        }

        var master = InitRepo(Path.Combine(temp.Path, "master"), masterFiles);
        foreach (var r in repoList)
        {
            InitRepo(Path.Combine(temp.Path, r), new Dictionary<string, string> { ["README.md"] = $"# {r}\n" });
        }

        var app = Path.Combine(temp.Path, "app");
        if (built)
        {
            Git(app, "checkout", "-q", "-b", $"change/{ChangeName}");
        }

        return new WorkspaceFixture(temp, temp.Path, master, app);
    }

    public static SingleRepoFixture CreateSingleRepo(Dictionary<string, string>? apps = null)
    {
        var temp = new TempDir("hoshin-single-");
        var appMap = apps ?? new Dictionary<string, string> { ["api"] = "apps/api", ["web"] = "apps/web" };
        var ws = Path.Combine(temp.Path, "ws");
        var repoPath = Path.Combine(ws, "mono");
        var files = new Dictionary<string, string>
        {
            ["README.md"] = "# mono\n",
            [".hoshin.json"] = JsonSerializer.Serialize(new
            {
                version = 1,
                layout = "single-repo",
                systems = new { demo = new { apps = appMap } },
            }, IndentedJson) + "\n",
            ["intent/demo/policies.md"] = "# Policies\n",
        };
        foreach (var folder in appMap.Values)
        {
            if (folder != ".")
            {
                files[$"{folder}/README.md"] = $"# {folder}\n";
            }
        }

        InitRepo(repoPath, files);
        return new SingleRepoFixture(temp, temp.Path, ws, repoPath, repoPath + ".remote.git");
    }

    public static string BuildLog(string[] tasks)
    {
        var entries = string.Concat(tasks.Select(t => $"\n### {t}\n\n```jsonc\n{{\n  \"task\": \"{t}\",\n  \"commits\": [\"abc1234\"]\n}}\n```\n"));
        return "```jsonc\n{ \"layout\": 2 }\n```\n\n# Build log: app\n" + entries;
    }

    public static (int ExitCode, string Stdout, string Stderr, JsonElement? Json) RunCli(string cwd, params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "hoshin.dll");
        var env = new Dictionary<string, string>(GitEnv) { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" };
        var result = ProcessCommandRunner.Default.Run(new CommandRequest(cwd, ["dotnet", dll, .. args], ExtraEnvironment: env));
        JsonElement? lastJson = null;
        foreach (var line in result.Stdout.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                lastJson = doc.RootElement.Clone();
            }
            catch
            {
                // Non-JSON line.
            }
        }

        return (result.ExitCode ?? -1, result.Stdout, result.Stderr, lastJson);
    }
}
