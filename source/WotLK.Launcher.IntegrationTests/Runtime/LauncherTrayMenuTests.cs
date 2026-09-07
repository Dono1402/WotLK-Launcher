using System.Reflection;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using Forms = System.Windows.Forms;

internal static class LauncherTrayMenuTests
{
    internal static Task<int> RunAsync()
    {
        TaskCompletionSource<int> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            string originalLocale = LauncherLocalization.CurrentLocale;
            int checks = 0;
            try
            {
                // The icon is never published to Explorer. The menu may create
                // an invisible HWND but is never shown, activated or captured.
                // All three Win32 activation calls use this recording fake.
                RecordingInterop interop = new();
                using WindowsLauncherTrayIconHost host = new(interop);
                Forms.ContextMenuStrip menu = Field<Forms.ContextMenuStrip>(host, "_menu");
                Forms.NotifyIcon icon = Field<Forms.NotifyIcon>(host, "_notifyIcon");
                Forms.ToolStripMenuItem open = Field<Forms.ToolStripMenuItem>(host, "_openItem");
                Forms.ToolStripMenuItem exit = Field<Forms.ToolStripMenuItem>(host, "_exitItem");
                int restores = 0, exits = 0;
                host.RestoreRequested += (_, _) => restores++;
                host.ExitRequested += (_, _) => exits++;
                Check(!host.IsVisible && !menu.Visible, "Creating the host does not expose an icon or menu.");
                Check(ReferenceEquals(icon.ContextMenuStrip, menu) && menu.AutoClose,
                    "NotifyIcon retains its standard menu placement, keyboard and outside-click dismissal path.");

                Invoke(host, "Menu_Opened", menu, EventArgs.Empty);
                Invoke(host, "Menu_Closed", menu, new Forms.ToolStripDropDownClosedEventArgs(Forms.ToolStripDropDownCloseReason.Keyboard));
                Check(interop.Calls.Count == 0, "Inactive lifecycle events do not create or activate a native menu handle.");
                IntPtr handle = menu.Handle;
                Check(handle != IntPtr.Zero && !menu.Visible && !host.IsVisible,
                    "Only an invisible test menu handle exists; no desktop popup or tray icon is shown.");

                Invoke(host, "Menu_Opened", menu, EventArgs.Empty);
                Check(interop.Calls.Count == 2 && interop.Calls[0].Operation == "foreground"
                    && interop.Calls[1].Operation == "position", "An opened popup activates before reasserting its Z-order.");
                Check(interop.Calls.All(call => call.Window == handle), "Foreground and topmost target only the popup HWND.");
                NativeCall position = interop.Calls[1];
                Check(position.InsertAfter == new IntPtr(-1), "Popup is placed above other topmost surfaces, including the tray overflow.");
                Check(position.Flags == 0x0213 && position.X == 0 && position.Y == 0 && position.Width == 0 && position.Height == 0,
                    "Z-order operation keeps popup position/size and does not activate or reorder its owner.");
                Check(restores == 0 && exits == 0, "Opening the context menu never restores or exits the main launcher.");

                Invoke(host, "Menu_Closed", menu, new Forms.ToolStripDropDownClosedEventArgs(Forms.ToolStripDropDownCloseReason.AppClicked));
                Check(interop.Calls.Count == 3 && interop.Calls[2].Operation == "post"
                    && interop.Calls[2].Window == handle && interop.Calls[2].Message == 0
                    && interop.Calls[2].WParam == IntPtr.Zero && interop.Calls[2].LParam == IntPtr.Zero,
                    "Closing posts a harmless WM_NULL to complete this popup's activation cycle.");
                Check(interop.Calls.Count(call => call.Operation == "foreground") == 1,
                    "Closing never steals foreground back from the user's selected application.");

                foreach (Forms.ToolStripDropDownCloseReason reason in new[]
                {
                    Forms.ToolStripDropDownCloseReason.Keyboard, Forms.ToolStripDropDownCloseReason.ItemClicked,
                    Forms.ToolStripDropDownCloseReason.AppFocusChange, Forms.ToolStripDropDownCloseReason.CloseCalled
                })
                {
                    int before = interop.Calls.Count;
                    Invoke(host, "Menu_Opened", menu, EventArgs.Empty);
                    Invoke(host, "Menu_Closed", menu, new Forms.ToolStripDropDownClosedEventArgs(reason));
                    Check(interop.Calls.Count == before + 3 && interop.Calls[^1].Operation == "post",
                        "Repeated opening/closing completes exactly one activation cycle for " + reason + ".");
                }
                interop.Succeed = false;
                int beforeDenial = interop.Calls.Count;
                Invoke(host, "Menu_Opened", menu, EventArgs.Empty);
                Invoke(host, "Menu_Closed", menu, new Forms.ToolStripDropDownClosedEventArgs(Forms.ToolStripDropDownCloseReason.Keyboard));
                Check(interop.Calls.Count == beforeDenial + 3,
                    "A Windows foreground refusal remains nonfatal and does not trigger main-window restoration.");

                Invoke(icon, "OnMouseClick", new Forms.MouseEventArgs(Forms.MouseButtons.Right, 1, 0, 0, 0));
                Check(restores == 0 && exits == 0, "Right click only opens the standard menu; it never restores the launcher.");
                Invoke(icon, "OnMouseClick", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Check(restores == 1, "Single left click still requests restoration.");
                // Mirror NotifyIcon's first click and second double-click pair.
                Invoke(icon, "WmMouseDown", Forms.MouseButtons.Left, 1);
                Invoke(icon, "WmMouseUp", Forms.MouseButtons.Left);
                Invoke(icon, "WmMouseDown", Forms.MouseButtons.Left, 2);
                Invoke(icon, "WmMouseUp", Forms.MouseButtons.Left);
                Check(restores == 2, "A double-click sequence retains its single restore request.");
                open.PerformClick(); exit.PerformClick();
                Check(restores == 3 && exits == 1, "Open and Quit menu commands each retain their intended action.");
                LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
                Check(open.Text == "Open Atlas Launcher" && exit.Text == "Quit", "The menu still localizes to English.");
                LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
                Check(open.Text == "Ouvrir Atlas Launcher" && exit.Text == "Quitter", "The menu still localizes back to French.");
                int beforeNotification = interop.Calls.Count;
                host.ShowNotification("Test", "Silent notification on an unpublished icon", playSound: false);
                Check(interop.Calls.Count == beforeNotification && !host.IsVisible && !menu.Visible,
                    "Notifications do not engage menu foreground activation or publish a hidden test icon.");

                host.Dispose(); host.Dispose();
                int afterDispose = interop.Calls.Count;
                Invoke(host, "Menu_Opened", menu, EventArgs.Empty);
                Invoke(host, "Menu_Closed", menu, new Forms.ToolStripDropDownClosedEventArgs(Forms.ToolStripDropDownCloseReason.CloseCalled));
                host.ShowNotification("Test", "Disposed", playSound: true);
                Check(interop.Calls.Count == afterDispose && menu.IsDisposed,
                    "Disposal is idempotent; late lifecycle callbacks cannot activate or recreate a disposed menu.");
                Invoke(icon, "OnMouseClick", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0));
                Check(restores == 3 && exits == 1, "Disposal detaches icon action handlers.");
                LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
                Check(open.Text == "Ouvrir Atlas Launcher", "Disposal detaches the locale event.");
                Console.WriteLine($"Tray menu PASS: {checks} assertions. Invisible NotifyIcon/menu and recording Win32 interop only; Explorer overflow, desktop Z-order and physical clicks were not exercised.");
                completed.SetResult(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); completed.SetResult(1); }
            finally { LauncherLocalization.SetLocale(originalLocale); }

            void Check(bool value, string message)
            {
                if (!value) throw new InvalidOperationException(message);
                checks++;
            }
        }) { IsBackground = true, Name = "AtlasInertTrayMenuTests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }

    private static T Field<T>(object value, string name) where T : class =>
        typeof(WindowsLauncherTrayIconHost).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) as T
        ?? throw new InvalidOperationException("Missing tray field: " + name);

    private static void Invoke(object target, string method, params object[] args) =>
        (target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing tray callback: " + method)).Invoke(target, args);

    private sealed record NativeCall(string Operation, IntPtr Window, IntPtr InsertAfter = default,
        int X = 0, int Y = 0, int Width = 0, int Height = 0, uint Flags = 0,
        uint Message = 0, IntPtr WParam = default, IntPtr LParam = default);

    private sealed class RecordingInterop : ILauncherTrayMenuInterop
    {
        internal List<NativeCall> Calls { get; } = [];
        internal bool Succeed { get; set; } = true;
        public bool SetForegroundWindow(IntPtr window) { Calls.Add(new("foreground", window)); return Succeed; }
        public bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags)
        { Calls.Add(new("position", window, insertAfter, x, y, width, height, flags)); return Succeed; }
        public bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        { Calls.Add(new("post", window, Message: message, WParam: wParam, LParam: lParam)); return Succeed; }
    }
}
