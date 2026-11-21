using Hoshin.Workflow.Git;

namespace Hoshin.Workflow.Events;

public sealed class ScriptFolders : IDisposable
{
    private readonly Dictionary<string, string> _layers;

    public ScriptFolders(string root, Dictionary<string, string> layers)
    {
        Root = root;
        _layers = layers;
    }

    public string Root { get; }

    public string FolderOf(string layer) => Path.Combine([Root, .. _layers[layer].Split('/')]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }
}

/// <summary>Exports hook scripts from the master repository's base branch into a temporary directory.</summary>
public sealed class HookScriptExporter(IGitClient? git = null)
{
    public static HookScriptExporter Default { get; } = new();

    private readonly IGitClient _git = git ?? ProcessGitClient.Default;

    public ScriptFolders Export(string master, string? revision, string system)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hoshin-hooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var layers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["global"] = "intent/hooks",
            ["system"] = $"intent/{system}/hooks",
        };

        if (revision is not null)
        {
            foreach (var folder in layers.Values)
            {
                foreach (var file in _git.ListFiles(master, revision, folder))
                {
                    var r = _git.Run(master, ["cat-file", "blob", $"{revision}:{file.Path}"]);
                    if (!r.Ok)
                    {
                        continue;
                    }

                    var target = Path.Combine([root, .. file.Path.Split('/')]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, r.Stdout);
                    if (!OperatingSystem.IsWindows() && file.Mode == "100755")
                    {
                        File.SetUnixFileMode(
                            target,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                }
            }
        }

        return new ScriptFolders(root, layers);
    }
}
