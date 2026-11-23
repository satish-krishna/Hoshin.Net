using System.Text;

namespace Hoshin.Workflow.Common;

/// <summary>Minimal filesystem seam for domain stores and readers (CONTRIBUTING.md §3.6 D1).</summary>
public interface IFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string ReadAllText(string path);

    void WriteAllTextAtomic(string path, string content);

    void DeleteFile(string path);

    void CreateDirectory(string path);

    IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*");

    IEnumerable<string> EnumerateDirectories(string path);
}

/// <summary>Default <see cref="IFileSystem"/> backed by <see cref="System.IO"/>.</summary>
public sealed class PhysicalFileSystem : IFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllTextAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, content, Utf8NoBom);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (IOException)
                {
                    // Best-effort cleanup of temp file.
                }
            }
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*") =>
        Directory.EnumerateFiles(path, searchPattern);

    public IEnumerable<string> EnumerateDirectories(string path) =>
        Directory.EnumerateDirectories(path);
}
