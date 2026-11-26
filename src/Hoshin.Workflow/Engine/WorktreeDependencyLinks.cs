using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hoshin.Workflow.Engine;

/// <summary>
/// Temporarily links gitignored dependency directories (<c>node_modules</c>, <c>.venv</c>, <c>venv</c>, and any
/// marker-configured <c>build.linkDirs</c>) from the main checkout into the detached Red worktree without copying
/// files, and unlinks them before git cleanup so main-checkout dependencies are never mutated or committed.
/// </summary>
internal sealed class WorktreeDependencyLinks : IDisposable
{
    public static readonly string[] DefaultLinkDirs = ["node_modules", ".venv", "venv"];

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupAndReparse = 0x02000000 | 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    private readonly List<string> _createdLinks;
    private bool _disposed;

    private WorktreeDependencyLinks(List<string> createdLinks) => _createdLinks = createdLinks;

    public static WorktreeDependencyLinks Link(string repoRoot, string worktreeDir, IReadOnlyList<string>? extraLinkDirs)
    {
        var created = new List<string>();
        var candidates = CollectCandidates(extraLinkDirs);
        foreach (var rel in candidates)
        {
            var src = Path.Combine(repoRoot, rel);
            var dst = Path.Combine(worktreeDir, rel);
            if (!Directory.Exists(src) || Directory.Exists(dst) || File.Exists(dst))
            {
                continue;
            }

            if (TryCreateDirectoryLink(dst, src))
            {
                created.Add(dst);
            }
        }

        return new WorktreeDependencyLinks(created);
    }

    /// <summary>Removes any leftover top-level directory symlinks or junctions before running <c>git clean -fdx</c>.</summary>
    public static void RemoveLeftoverLinks(string worktreeDir, IReadOnlyList<string>? extraLinkDirs = null)
    {
        if (!Directory.Exists(worktreeDir))
        {
            return;
        }

        foreach (var rel in CollectCandidates(extraLinkDirs))
        {
            var dst = Path.Combine(worktreeDir, rel);
            RemoveLinkIfReparsePoint(dst);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var link in _createdLinks)
        {
            RemoveLinkIfReparsePoint(link);
        }
    }

    private static List<string> CollectCandidates(IReadOnlyList<string>? extraLinkDirs)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var raw in DefaultLinkDirs.Concat(extraLinkDirs ?? []))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var cleaned = raw.Trim().Trim('/', '\\').Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (cleaned.Length == 0 || Path.IsPathRooted(cleaned) || cleaned.Split(Path.DirectorySeparatorChar).Contains(".."))
            {
                continue;
            }

            if (seen.Add(cleaned))
            {
                result.Add(cleaned);
            }
        }

        return result;
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        var parent = Path.GetDirectoryName(linkPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is UnauthorizedAccessException or IOException)
        {
            return TryCreateWindowsJunction(linkPath, targetPath);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void RemoveLinkIfReparsePoint(string path)
    {
        try
        {
            if (Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of link entry only; target directory is never touched.
        }
    }

    [ExcludeFromCodeCoverage]
    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
        var fullTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var substitute = @"\??\" + fullTarget;
        var subBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(fullTarget);
        var dataLength = 8 + subBytes.Length + 2 + printBytes.Length + 2;
        var buffer = new byte[8 + dataLength];

        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), IoReparseTagMountPoint);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10, 2), (ushort)subBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12, 2), (ushort)(subBytes.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14, 2), (ushort)printBytes.Length);
        subBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, 16 + subBytes.Length + 2);

        Directory.CreateDirectory(linkPath);
        using var handle = CreateFile(linkPath, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupAndReparse, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            TryDeleteEmptyDir(linkPath);
            return false;
        }

        if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            TryDeleteEmptyDir(linkPath);
            return false;
        }

        return true;
    }

    [ExcludeFromCodeCoverage]
    private static void TryDeleteEmptyDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (IOException)
        {
            // Ignore cleanup failure on an already-removed temp directory.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time", Justification = "Hoshin.Workflow intentionally compiles without AllowUnsafeBlocks.")]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time", Justification = "Hoshin.Workflow intentionally compiles without AllowUnsafeBlocks.")]
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        [In] byte[] lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
