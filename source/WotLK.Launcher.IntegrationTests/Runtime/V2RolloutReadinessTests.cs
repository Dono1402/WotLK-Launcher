using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class V2RolloutReadinessTests
{
    internal static async Task<int> RunAsync(string? captureDirectory = null)
    {
        await RunWpfHarnessAsync(captureDirectory);
        Console.WriteLine("Audit automatisé V2 terminé (04B.4). Les gates DPI, UAC et production restent manuelles.");
        return 0;
    }

    private static async Task RunWpfHarnessAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasV2RolloutReadinessHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void RunWpfHarness(
        TaskCompletionSource completion,
        string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(dispatcher));
        Exception? failure = null;
        dispatcher.UnhandledException += (_, args) =>
        {
            failure ??= args.Exception;
            args.Handled = true;
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        };
        _ = RunAsync();
        Dispatcher.Run();
        if (failure is null)
        {
            completion.TrySetResult();
        }
        else
        {
            completion.TrySetException(failure);
        }

        async Task RunAsync()
        {
            Application? application = null;
            LauncherShellV2? window = null;
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);

                Stopwatch startup = Stopwatch.StartNew();
                window = new LauncherShellV2(
                    GamePreviewScenario.Ready,
                    AccountPreviewScenario.Profile)
                {
                    Width = 1080,
                    Height = 680,
                    Left = -20000,
                    Top = -20000,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                window.Show();
                await PumpAsync(DispatcherPriority.ApplicationIdle);
                startup.Stop();

                True(window.IsPreviewMode, "Le harnais readiness doit rester en preview isolée.");
                True(!window.HasRealAuthenticationAttached, "Le preview ne doit pas attacher l’authentification réelle.");
                True(!window.HasRealAddonsAttached, "Le preview ne doit pas attacher les addons réels.");
                True(!window.HasRealActivityAttached, "Le preview ne doit pas attacher l’activité réelle.");

                ShellUiState shellState = window.ShellState;
                GameUiState gameState = window.GameState;
                AddonsUiState addonsState = window.AddonsState;
                DashboardUiState dashboardState = window.DashboardState;
                FriendsUiState friendsState = window.FriendsState;
                SettingsUiState settingsState = window.SettingsState;
                AccountUiState accountState = window.AccountState;

                Button gameNavigation = Required<Button>(window, "GameNavigationButton");
                Button addonsNavigation = Required<Button>(window, "AddonsNavigationButton");
                Button settingsNavigation = Required<Button>(window, "SettingsButton");
                Button profileButton = Required<Button>(window, "ProfileButton");
                Button manageAccount = Required<Button>(window.ProfileOverlay, "ManageAccountButton");

                Stopwatch navigation = Stopwatch.StartNew();
                for (int index = 0; index < 25; index++)
                {
                    RaiseClick(gameNavigation);
                    Equal(LauncherShellPage.Game, window.CurrentPage, "La navigation longue doit atteindre Jeu.");
                    RaiseClick(addonsNavigation);
                    Equal(LauncherShellPage.Addons, window.CurrentPage, "La navigation longue doit atteindre Addons.");
                    RaiseClick(settingsNavigation);
                    Equal(LauncherShellPage.Settings, window.CurrentPage, "La navigation longue doit atteindre Paramètres.");
                    RaiseClick(profileButton);
                    Equal(ShellOverlayKind.Profile, window.CurrentOverlay, "Le menu profil doit rester disponible après navigation.");
                    RaiseClick(manageAccount);
                    Equal(LauncherShellPage.Account, window.CurrentPage, "La navigation longue doit atteindre Compte.");
                    Equal(ShellOverlayKind.None, window.CurrentOverlay, "Le menu profil doit se fermer en ouvrant Compte.");
                }
                await PumpAsync(DispatcherPriority.ApplicationIdle);
                navigation.Stop();

                True(ReferenceEquals(shellState, window.ShellState), "La navigation ne doit pas recréer ShellUiState.");
                True(ReferenceEquals(gameState, window.GameState), "La navigation ne doit pas recréer GameUiState.");
                True(ReferenceEquals(addonsState, window.AddonsState), "La navigation ne doit pas recréer AddonsUiState.");
                True(ReferenceEquals(dashboardState, window.DashboardState), "La navigation ne doit pas recréer DashboardUiState.");
                True(ReferenceEquals(friendsState, window.FriendsState), "La navigation ne doit pas recréer FriendsUiState.");
                True(ReferenceEquals(settingsState, window.SettingsState), "La navigation ne doit pas recréer SettingsUiState.");
                True(ReferenceEquals(accountState, window.AccountState), "La navigation ne doit pas recréer AccountUiState.");
                True(navigation.Elapsed < TimeSpan.FromSeconds(10), "Cent changements de page ne doivent pas bloquer l’UI.");

                RaiseClick(gameNavigation);
                await PumpAsync(DispatcherPriority.Render);
                GameViewV2 game = Required<GameViewV2>(window, "GameView");
                ScrollViewer gameScroll = Required<ScrollViewer>(game, "GameScrollViewer");
                True(gameScroll.ScrollableWidth <= 0.5, "Jeu ne doit pas avoir de défilement horizontal à 1080 x 680.");
                AssertInsideWindow(Required<Button>(game, "PrimaryActionButton"), window, "Le bouton principal Jeu");
                AssertInsideWindow(Required<Button>(game, "LatestPatchNoteAction"), window, "Le bouton Mises à jour");
                True(game.FindName("InstallCard") is null && game.FindName("NewsCard") is null,
                    "La page Jeu immersive ne doit plus contenir les anciennes cartes inférieures.");
                SavePng(window, captureDirectory, "game-immersive-1080x680.png", 1080, 680);

                RaiseClick(addonsNavigation);
                await PumpAsync(DispatcherPriority.Render);
                True(
                    Descendants<ScrollViewer>(window.AddonsPage).All(scroll => scroll.ScrollableWidth <= 0.5),
                    "Addons ne doit pas déborder horizontalement à 1080 x 680.");

                RaiseClick(settingsNavigation);
                await PumpAsync(DispatcherPriority.Render);
                True(window.SettingsPage.ScrollHost.ScrollableWidth <= 0.5, "Paramètres ne doit pas déborder horizontalement à 1080 x 680.");

                RaiseClick(profileButton);
                RaiseClick(manageAccount);
                await PumpAsync(DispatcherPriority.Render);
                True(window.AccountPage.ScrollHost.ScrollableWidth <= 0.5, "Compte ne doit pas déborder horizontalement à 1080 x 680.");
                AssertInsideWindow(Required<Button>(window, "CloseWindowButton"), window, "Le bouton Fermer");

                window.Width = 1920;
                window.Height = 1080;
                RaiseClick(gameNavigation);
                await PumpAsync(DispatcherPriority.Render);
                Border contentFrame = Required<Border>(game, "ContentFrame");
                True(Math.Abs(contentFrame.ActualWidth - game.ActualWidth) <= 0.5,
                    "Jeu doit utiliser toute la largeur de l'onglet à 1920 x 1080.");
                SavePng(window, captureDirectory, "game-immersive-1920x1080.png", 1920, 1080);

                RaiseClick(Required<Button>(game, "LatestPatchNoteAction"));
                await DelayAndPumpAsync(180);
                Equal(ShellOverlayKind.PatchNote, window.CurrentOverlay, "Le lecteur de note doit être l’overlay actif.");
                SavePng(window, captureDirectory, "readiness-patch-note-1440x860.png", 1440, 860);
                Stopwatch activityOpen = Stopwatch.StartNew();
                RaiseClick(Required<Button>(window, "ActivityButton"));
                await PumpAsync(DispatcherPriority.Input);
                activityOpen.Stop();
                Equal(ShellOverlayKind.Activity, window.CurrentOverlay, "Activité doit remplacer le lecteur de note.");
                True(!window.PatchNoteState.IsOpen, "Le lecteur de note ne doit pas rester invisible et ouvert.");

                RaiseClick(Required<Button>(window, "FriendsButton"));
                await PumpAsync(DispatcherPriority.Input);
                Equal(ShellOverlayKind.Friends, window.CurrentOverlay, "Amis doit remplacer Activité.");
                True(!window.ActivityState.IsOpen, "Activité ne doit pas rester ouverte derrière Amis.");

                Console.WriteLine(
                    $"V2 metrics: startup={startup.Elapsed.TotalMilliseconds:F0} ms; "
                    + $"navigation100={navigation.Elapsed.TotalMilliseconds:F0} ms; "
                    + $"activityOpen={activityOpen.Elapsed.TotalMilliseconds:F0} ms; "
                    + $"managedMemory={GC.GetTotalMemory(false) / 1024d / 1024d:F1} MiB.");
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
            finally
            {
                window?.Close();
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static void AssertInsideWindow(FrameworkElement element, Window window, string label)
    {
        Point origin = element.TransformToAncestor(window).Transform(new Point());
        True(origin.X >= -0.5, $"{label} dépasse à gauche.");
        True(origin.Y >= -0.5, $"{label} dépasse en haut.");
        True(origin.X + element.ActualWidth <= window.ActualWidth + 0.5, $"{label} dépasse à droite.");
        True(origin.Y + element.ActualHeight <= window.ActualHeight + 0.5, $"{label} dépasse en bas.");
    }

    private static void SavePng(
        Window window,
        string? directory,
        string fileName,
        double width,
        double height)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        window.Width = width;
        window.Height = height;
        window.UpdateLayout();
        Directory.CreateDirectory(directory);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        RenderTargetBitmap bitmap = new(
            pixelWidth,
            pixelHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, fileName));
        encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static T Required<T>(FrameworkElement scope, string name)
        where T : FrameworkElement
    {
        return scope.FindName(name) as T
            ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority)
    {
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void LoadV2Resources(Application application)
    {
        string[] resourcePaths =
        [
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        ];
        foreach (string resourcePath in resourcePaths)
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(resourcePath, UriKind.Relative)
            });
        }
    }
}
