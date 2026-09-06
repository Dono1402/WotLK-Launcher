using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;

internal static class LauncherAutostartWpfTests
{
    internal static async Task RunAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = VerifyAsync();
            Dispatcher.Run();

            async Task VerifyAsync()
            {
                try
                {
                    LauncherSettingsRuntimeTests.CharacterizeStartupRegistration();
                    VerifyRegistrationMigration();
                    await VerifyWindowAsync();
                    completion.TrySetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }
        }) { IsBackground = true, Name = "AtlasAutostartIsolatedTests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("Démarrage Windows : migration simulée, lancement manuel normal, réduction WPF native sans activation et sans accès au registre réel OK.");
    }

    private static void VerifyRegistrationMigration()
    {
        const string name = "Atlas Launcher Autostart Test";
        const string executable = @"C:\Atlas Autostart Test\AtlasLauncher.exe";
        string legacy = WindowsLauncherStartupRegistration.QuoteExecutable(executable);
        string expected = legacy + " --autostart";
        foreach (var (enabled, existing, failWrites) in new[]
        {
            (true, legacy, false),
            (true, expected, false),
            (false, (string?)null, false),
            (true, legacy, true)
        })
        {
            MemoryStartupRegistry registry = new() { Command = existing, FailWrites = failWrites };
            WindowsLauncherStartupRegistration registration = new(executable, registry, name);
            LauncherSettings settings = new() { StartWithWindows = enabled };
            using LauncherOperationCoordinator operations = new();
            int saves = 0;
            List<string> log = [];
            using LauncherSettingsCoordinator coordinator = new(settings, operations, _ => saves++, _ => { }, log.Add,
                readInstantQuestText: _ => false, writeInstantQuestText: (_, _) => true);
            Window owner = new() { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                using (CreateCommands()) { }
                True(settings.StartWithWindows == enabled && coordinator.CurrentSnapshot.StartWithWindows == enabled && saves == 0,
                    "La migration ne doit ni désactiver la préférence ni réécrire les paramètres.");
                if (failWrites)
                {
                    True(registry.Command == legacy && registry.Writes == 1 && log.Count > 0,
                        "Un refus du registre doit garder la commande antérieure et signaler l'échec sans perdre la préférence.");
                }
                else
                {
                    True(registry.Command == (enabled ? expected : null),
                        "Le démarrage actif doit migrer vers --autostart ; une préférence inactive doit rester inactive.");
                    int writes = registry.Writes;
                    using (CreateCommands()) { }
                    True(registry.Writes == writes && writes == (enabled && existing == legacy ? 1 : 0),
                        "Une commande déjà migrée ne doit pas être réécrite au démarrage suivant.");
                }
            }
            finally { owner.Close(); }

            SettingsCommands CreateCommands() => new(new SettingsUiState(SettingsUiState.Empty.Current), coordinator,
                new FakeSettingsLocalActions(), owner, log.Add, new FakeSettingsFolderPicker(@"C:\Atlas Autostart Test"),
                new FakeSettingsLocaleApplier(), new FakeSettingsGameConfigAccess(), registration);
        }
    }

    private static async Task VerifyWindowAsync()
    {
        Window manual = new();
        App.ConfigureStartupWindow(manual, startMinimized: false);
        True(manual.WindowState == WindowState.Normal && manual.ShowActivated && manual.ShowInTaskbar,
            "Un démarrage manuel doit conserver une fenêtre normale et son activation habituelle.");
        manual.Close();

        Window automatic = new()
        {
            Width = 320, Height = 180, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new TextBlock { Text = "Isolated automatic startup verification" }
        };
        IInputElement? initialFocus = Keyboard.FocusedElement;
        int activations = 0;
        automatic.Activated += (_, _) => activations++;
        automatic.AddHandler(Keyboard.PreviewGotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) => e.Handled = true), true);
        automatic.SourceInitialized += (_, _) =>
        {
            IntPtr own = new WindowInteropHelper(automatic).Handle;
            SetWindowLongPtr(own, -20, (IntPtr)(GetWindowLongPtr(own, -20).ToInt64() | 0x08000000));
        };
        try
        {
            App.ConfigureStartupWindow(automatic, startMinimized: true);
            True(automatic.WindowState == WindowState.Minimized && !automatic.ShowActivated && automatic.ShowInTaskbar && !automatic.IsVisible,
                "Le démarrage automatique doit préparer la réduction dans la barre des tâches avant tout affichage.");

            // Hide only the test fixture's taskbar entry; the production configuration above must retain it.
            automatic.ShowInTaskbar = false;
            automatic.Show();
            await automatic.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Task.Delay(100);
            await automatic.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            True(automatic.WindowState == WindowState.Minimized && IsIconic(new WindowInteropHelper(automatic).Handle),
                "La fenêtre de test doit être réellement réduite au niveau Win32 après Show.");
            True(!automatic.IsActive && activations == 0 && ReferenceEquals(initialFocus, Keyboard.FocusedElement),
                "Le démarrage automatique ne doit déclencher aucune activation ni déplacement du focus.");
        }
        finally { automatic.Close(); }
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class MemoryStartupRegistry : ILauncherStartupRegistry
    {
        internal string? Command { get; set; }
        internal bool FailWrites { get; init; }
        internal int Writes { get; private set; }
        public string? Read(string valueName) => Command;
        public void Write(string valueName, string command)
        {
            Writes++;
            if (FailWrites) throw new UnauthorizedAccessException("Simulated registry rejection.");
            Command = command;
        }
        public void Delete(string valueName) => Command = null;
    }

    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
