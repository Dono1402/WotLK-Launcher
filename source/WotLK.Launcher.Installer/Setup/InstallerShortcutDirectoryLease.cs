using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WotLK.Launcher.Installer.Setup;

// Common shell folders can legitimately be writable by ordinary users. Keep
// their directory chain pinned instead of imposing the Program Files ACL policy.
internal sealed class InstallerShortcutDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];

    internal static InstallerShortcutDirectoryLease Acquire(string directory, bool create = false)
    {
        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full) ?? throw new InvalidDataException("Chemin local absent.");
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Les raccourcis doivent rester sur un disque local.");
        InstallerShortcutDirectoryLease lease = new();
        try
        {
            string current = root;
            lease.Pin(current);
            foreach (string segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                lease.Pin(current);
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static void ValidateExistingParent(string shortcut)
    {
        InstallerPathValidator.DemandNoReparsePoints(shortcut);
        string? parent = Path.GetDirectoryName(Path.GetFullPath(shortcut));
        while (parent is not null && !Directory.Exists(parent)) parent = Path.GetDirectoryName(parent);
        using InstallerShortcutDirectoryLease lease = Acquire(parent ?? throw new DirectoryNotFoundException());
    }

    private void Pin(string directory)
    {
        // FILE_LIST_DIRECTORY participates in delete sharing; attributes-only
        // handles do not prevent a directory rename on Windows.
        SafeFileHandle handle = CreateFileW(directory, 0x81, FileShare.Read | FileShare.Write,
            IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        _handles.Add(handle);
        if (!GetFileInformationByHandle(handle, out FileInformation info))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0
            || (info.Attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException("Le dossier du raccourci ne doit pas être un lien ou une jonction.");
    }

    public void Dispose()
    {
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }

    internal void ReleaseLeaf()
    {
        if (_handles.Count == 0) return;
        _handles[^1].Dispose();
        _handles.RemoveAt(_handles.Count - 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        private System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        private uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share,
        IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);
}
