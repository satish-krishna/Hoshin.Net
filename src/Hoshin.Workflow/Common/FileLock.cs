using System.Diagnostics;

namespace Hoshin.Workflow.Common;

/// <summary>Cross-process and cross-thread exclusive lock backed by an OS lock file.</summary>
public static class FileLock
{
    public static FileStream Acquire(string lockPath, TimeSpan? timeout = null)
    {
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (sw.Elapsed < limit)
            {
                Thread.Sleep(25);
            }
        }
    }
}
