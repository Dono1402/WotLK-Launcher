using System.Runtime.InteropServices;

namespace Atlas.WindowsShell;

internal static class ShellIconRefresh
{
    internal static void NotifyItem(string path)
    {
        if (OperatingSystem.IsWindows())
            SHChangeNotify(0x00002000, 0x0005 | 0x2000, System.IO.Path.GetFullPath(path), IntPtr.Zero);
    }

    internal static void NotifyApplicationReplaced(string executable)
    {
        if (!OperatingSystem.IsWindows()) return;
        NotifyItem(executable);
        // Replacing an EXE at the same path can leave Explorer's old icon cached.
        // Notify the shell; do not delete cache databases or restart Explorer.
        SHChangeNotify(0x08000000, 0x2000, null, IntPtr.Zero);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int events, uint flags, string? item, IntPtr unused);
}
