using System.Runtime.InteropServices;
using System.IO;

namespace WotLK.Launcher.Installer.Setup;

// Explorer-hosted ShellExecute flow adapted from Microsoft Node.js Tools
// (Apache-2.0): Nodejs/Product/Nodejs/SharedProject/SystemUtilities.cs.
internal static class InstallerUnelevatedProcessLauncher
{
    private const int CsidlDesktop = 0;
    private const int ShellWindowClassDesktop = 8;
    private const int ShellWindowFindNeedDispatch = 1;
    private const uint ShellViewGetItemBackground = 0;
    private const int ShowNormal = 1;

    private static readonly Guid TopLevelBrowserService =
        new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    internal static void Launch(string executablePath, string workingDirectory)
        => Launch(executablePath, string.Empty, workingDirectory);

    internal static void Launch(
        string executablePath,
        string arguments,
        string workingDirectory)
    {
        string executable = Path.GetFullPath(executablePath);
        string directory = Path.GetFullPath(workingDirectory);
        object desktop = CsidlDesktop;
        object unused = new();

        try
        {
            IShellWindows shellWindows = (IShellWindows)new ShellWindows();
            IServiceProvider serviceProvider = (IServiceProvider)shellWindows.FindWindowSW(
                ref desktop,
                ref unused,
                ShellWindowClassDesktop,
                out int shellWindow,
                ShellWindowFindNeedDispatch);
            if (shellWindow == 0)
            {
                throw new InvalidOperationException("Le shell Windows interactif est indisponible.");
            }

            Guid browserInterface = typeof(IShellBrowser).GUID;
            Guid service = TopLevelBrowserService;
            IShellBrowser shellBrowser = (IShellBrowser)serviceProvider.QueryService(
                ref service,
                ref browserInterface);
            IShellView shellView = shellBrowser.QueryActiveShellView();
            Guid dispatchInterface = typeof(IDispatch).GUID;
            IShellFolderViewDual folderView = (IShellFolderViewDual)shellView.GetItemObject(
                ShellViewGetItemBackground,
                ref dispatchInterface);
            IShellDispatch2 shellDispatch = (IShellDispatch2)folderView.Application;
            shellDispatch.ShellExecute(
                executable,
                arguments,
                directory,
                string.Empty,
                ShowNormal);
        }
        catch (COMException exception)
        {
            throw new InvalidOperationException(
                "Windows n'a pas pu démarrer Atlas Launcher avec les permissions utilisateur.",
                exception);
        }
    }

    [ComImport]
    [Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    [ClassInterface(ClassInterfaceType.None)]
    private class ShellWindows
    {
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellWindows
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object locationRoot,
            int windowClass,
            out int windowHandle,
            int findOptions);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object QueryService(ref Guid service, ref Guid interfaceId);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void VTableGap01();
        void VTableGap02();
        void VTableGap03();
        void VTableGap04();
        void VTableGap05();
        void VTableGap06();
        void VTableGap07();
        void VTableGap08();
        void VTableGap09();
        void VTableGap10();
        void VTableGap11();
        void VTableGap12();
        IShellView QueryActiveShellView();
    }

    [ComImport]
    [Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void VTableGap01();
        void VTableGap02();
        void VTableGap03();
        void VTableGap04();
        void VTableGap05();
        void VTableGap06();
        void VTableGap07();
        void VTableGap08();
        void VTableGap09();
        void VTableGap10();
        void VTableGap11();
        void VTableGap12();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetItemObject(uint aspectOfView, ref Guid interfaceId);
    }

    [ComImport]
    [Guid("00020400-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IDispatch
    {
    }

    [ComImport]
    [Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellFolderViewDual
    {
        object Application
        {
            [return: MarshalAs(UnmanagedType.IDispatch)]
            get;
        }
    }

    [ComImport]
    [Guid("A4C6892C-3BA9-11D2-9DEA-00C04FB16162")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IShellDispatch2
    {
        void ShellExecute(
            [MarshalAs(UnmanagedType.BStr)] string file,
            [MarshalAs(UnmanagedType.Struct)] object arguments,
            [MarshalAs(UnmanagedType.Struct)] object directory,
            [MarshalAs(UnmanagedType.Struct)] object operation,
            [MarshalAs(UnmanagedType.Struct)] object showCommand);
    }
}
