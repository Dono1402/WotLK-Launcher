using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;

internal static class ActivityCenterRuntimeWpfTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        ValidateRuntimeProjection();
        await ValidateRuntimeWpfAsync(captureDirectory);
        Console.WriteLine("Atlas activity center runtime WPF wiring OK (04B.2).");
        return 0;
    }

    private static void ValidateRuntimeProjection()
    {
        ActivityViewState game = ActivityStateAdapter.Project(GameDownloadSnapshot());
        True(!game.IsPreview && game.TopBarShowsPercent,
            "La projection réelle doit exposer la progression Jeu dans la top bar.");
        Equal("68 %", game.TopBarPercentText,
            "Le pourcentage réel ne doit pas être reformulé par la vue WPF.");
        True(game.ActiveOperation is
            {
                OperationId: 41,
                ProductName: "WotLK Classic",
                ActionName: "Mise à jour",
                CanUserCancel: true,
                NavigationTarget: ActivityNavigationTarget.Game
            },
            "L'opération Jeu doit conserver identité, action, annulation et navigation.");
        True(game.ActiveOperation!.RateAndEtaText.Contains("/s", StringComparison.Ordinal)
            && game.ActiveOperation.RateAndEtaText.Contains("min restantes", StringComparison.Ordinal),
            "Le débit et l'ETA déjà fournis doivent être formatés sans être recalculés.");

        ActivityViewState batch = ActivityStateAdapter.Project(AddonBatchSnapshot());
        True(batch.HasActiveOperation && batch.HasPendingOperations,
            "Le batch réel doit projeter l'addon courant et sa file restante.");
        Equal("1 sur 4", batch.ActiveOperation!.BatchPosition,
            "La position du batch doit provenir du coordinateur Addons.");
        True(!batch.TopBarShowsPercent,
            "La progression d'un addon ne doit pas devenir un faux pourcentage global du batch.");
        Equal(2, batch.PendingOperations.Length,
            "Seules les opérations explicitement en attente doivent être affichées.");

        ActivityViewState history = ActivityStateAdapter.Project(HistorySnapshot());
        True(history.HasRecentOperations && history.HasRecentFailure,
            "Les résultats terminaux doivent alimenter l'historique et son témoin d'échec.");
        True(history.RecentOperations.All(item => item.CanNavigate),
            "Les résultats Jeu et Addons doivent conserver une destination explicite.");
    }

    private static async Task ValidateRuntimeWpfAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasActivityRuntimeWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void RunWpfHarness(TaskCompletionSource completion, string? captureDirectory)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
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
            try
            {
                application = Application.Current ?? new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };
                LoadV2Resources(application);
                await ValidateWindowAsync(captureDirectory);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                application?.Shutdown();
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        }
    }

    private static async Task ValidateWindowAsync(string? captureDirectory)
    {
        ActivityUiState activityState = new(ActivityStateAdapter.Project(GameDownloadSnapshot()));
        AddonsViewState addonsView = AddonsPreviewData.Create(AddonsPreviewScenario.Detail).Current with
        {
            IsPreview = false,
            IsRuntimeConnected = true,
            CanMutate = false,
            SelectedAddon = null,
            IsDetailOpen = false
        };
        AddonsUiState addonsState = new(addonsView);
        LauncherShellV2 window = new(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            addonsState,
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateFriends(),
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop(),
            activityState)
        {
            Width = 1440,
            Height = 860,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        int cancellationCount = 0;
        using ActivityCancelCommand cancelCommand = new(activityState, () =>
        {
            cancellationCount++;
            LauncherActivityOperationSnapshot active = GameDownloadSnapshot().ActiveOperation! with
            {
                Phase = LauncherActivityPhase.Cancelling,
                CanUserCancel = false,
                IsCancellationRequested = true
            };
            activityState.ApplyRuntimeView(ActivityStateAdapter.Project(
                GameDownloadSnapshot() with { Sequence = 2, ActiveOperation = active }));
            return true;
        });
        window.AttachActivity(cancelCommand);
        window.Show();
        try
        {
            await DelayAndPumpAsync(160);
            True(!window.IsPreviewMode && window.HasRealActivityAttached,
                "Le harnais doit utiliser la branche V2 réelle et sa commande déléguée.");
            Equal("68 %", Required<TextBlock>(window, "ActivityPercentText").Text,
                "La top bar WPF doit afficher la valeur projetée du runtime.");

            RaiseClick(Required<Button>(window, "ActivityButton"));
            await DelayAndPumpAsync(220);
            True(window.ActivityState.IsOpen
                && window.CurrentOverlay == ShellOverlayKind.Activity
                && window.ActivityOverlay.IsHitTestVisible,
                "Le panneau runtime doit s'ouvrir sans remplacer la page Jeu.");
            True(Descendants<TextBlock>(window.ActivityOverlay).Any(text =>
                    text.Text == "Téléchargement des fichiers du client"),
                "La phase métier réelle doit être visible dans le panneau.");

            Button cancel = Descendants<Button>(window.ActivityOverlay).First(button =>
                button.IsVisible
                && string.Equals(
                    AutomationProperties.GetName(button),
                    "Annuler l’opération",
                    StringComparison.Ordinal));
            RaiseClick(cancel);
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(1, cancellationCount,
                "L'annulation WPF doit appeler une seule fois l'autorité déléguée.");
            True(!cancel.IsEnabled && activityState.Current.ActiveOperation is
                { IsCancellationRequested: true, CanUserCancel: false },
                "La source doit désactiver immédiatement toute deuxième annulation.");
            RaiseClick(cancel);
            Equal(1, cancellationCount,
                "Un second clic ne doit pas contourner CanExecute.");

            activityState.ApplyRuntimeView(ActivityStateAdapter.Project(VerifySnapshot()));
            await DelayAndPumpAsync(120);
            True(activityState.Current.TopBarIsIndeterminate
                && Required<TextBlock>(window, "ActivityPercentText").Visibility == Visibility.Collapsed,
                "Une vérification indéterminée doit retirer immédiatement l'ancien pourcentage.");
            Equal(40d, Required<Button>(window, "ActivityButton").ActualWidth,
                "Une activité indéterminée doit conserver l'icône compacte seule.");
            True(Descendants<ProgressBar>(window.ActivityOverlay).Any(progress =>
                    progress.IsVisible && progress.IsIndeterminate),
                "Le panneau runtime doit afficher une vraie progression indéterminée.");
            True(!Descendants<Button>(window.ActivityOverlay).Any(button =>
                    button.IsVisible
                    && string.Equals(
                        AutomationProperties.GetName(button),
                        "Annuler l’opération",
                        StringComparison.Ordinal)),
                "Verify non annulable ne doit présenter aucun bouton Annuler.");

            activityState.ApplyRuntimeView(ActivityStateAdapter.Project(HistorySnapshot()));
            await DelayAndPumpAsync(120);
            True(!activityState.Current.HasActiveOperation
                && activityState.Current.RecentOperations.Length == 2,
                "La fin doit retirer En cours et conserver seulement les contrats terminaux.");
            True(Required<TextBlock>(window, "ActivityPercentText").Visibility == Visibility.Collapsed
                && Required<Button>(window, "ActivityButton").ActualWidth == 40d,
                "La top bar ne doit jamais conserver le dernier pourcentage après terminaison.");
            True(!activityState.IsOpen || window.ActivityOverlay.IsHitTestVisible,
                "Une mise à jour runtime ne doit pas fermer ou désynchroniser le panneau.");

            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                SavePng(window, Path.Combine(captureDirectory, "activity-runtime-1440x860.png"));
            }

            Button addonNavigation = Descendants<Button>(window.ActivityOverlay).First(button =>
                button.IsVisible
                && button.DataContext is ActivityRecentUiItem
                {
                    NavigationTarget: ActivityNavigationTarget.Addons,
                    TargetId: "questie"
                });
            RaiseClick(addonNavigation);
            await DelayAndPumpAsync(240);
            Equal(LauncherShellPage.Addons, window.CurrentPage,
                "Un résultat Addon doit naviguer vers AddonsViewV2.");
            Equal("questie", addonsState.Current.SelectedAddon?.Id,
                "L'historique doit ouvrir le détail de l'addon encore présent au catalogue.");
            True(!activityState.IsOpen,
                "La navigation depuis Récent doit fermer le centre d'activité.");

            RaiseClick(Required<Button>(window, "ActivityButton"));
            await DelayAndPumpAsync(220);
            Button gameNavigation = Descendants<Button>(window.ActivityOverlay).First(button =>
                button.IsVisible
                && button.DataContext is ActivityRecentUiItem
                {
                    NavigationTarget: ActivityNavigationTarget.Game
                });
            RaiseClick(gameNavigation);
            await DelayAndPumpAsync(220);
            Equal(LauncherShellPage.Game, window.CurrentPage,
                "Un résultat Jeu doit naviguer vers GameViewV2.");

            Button friendsButton = Required<Button>(window, "FriendsButton");
            RaiseClick(friendsButton);
            await DelayAndPumpAsync(220);
            window.FriendsOverlay.FocusFirstControl();
            await PumpAsync(DispatcherPriority.Input);
            DependencyObject? focusedBeforeActivityUpdate = Keyboard.FocusedElement as DependencyObject;
            True(window.CurrentOverlay == ShellOverlayKind.Friends
                && window.FriendsOverlay.ContainsKeyboardFocusTarget(focusedBeforeActivityUpdate),
                "Le panneau Amis doit posséder le focus avant le test d'observation en arrière-plan.");
            activityState.ApplyRuntimeView(ActivityStateAdapter.Project(VerifySnapshot()));
            await DelayAndPumpAsync(120);
            True(window.CurrentOverlay == ShellOverlayKind.Friends
                && !activityState.IsOpen,
                "Une activité reçue pendant Amis ne doit pas ouvrir automatiquement le centre.");
            True(window.FriendsOverlay.ContainsKeyboardFocusTarget(
                    Keyboard.FocusedElement as DependencyObject),
                "Une mise à jour Activity ne doit pas voler le focus au panneau Amis.");

            RaiseClick(Required<Button>(window, "ActivityButton"));
            await DelayAndPumpAsync(220);
            True(window.CurrentOverlay == ShellOverlayKind.Activity
                && activityState.IsOpen
                && !window.FriendsState.IsOpen,
                "ShellOverlayCoordinator doit conserver l'exclusivité Activité/Amis en runtime.");
            RaiseClick(Required<Button>(window.ActivityOverlay, "CloseButton"));
            await DelayAndPumpAsync(220);

            window.Width = 1080;
            window.Height = 680;
            await DelayAndPumpAsync(180);
            foreach (string name in new[]
            {
                "ActivityButton",
                "FriendsButton",
                "SettingsButton",
                "ProfileButton",
                "CloseWindowButton"
            })
            {
                Rect bounds = BoundsInAncestor(Required<FrameworkElement>(window, name), window);
                True(bounds.Left >= -0.5 && bounds.Right <= window.ActualWidth + 0.5,
                    $"{name} doit rester accessible avec l'activité runtime à 1080 x 680.");
            }
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static LauncherActivitySnapshot GameDownloadSnapshot() => new(
        Sequence: 1,
        ActiveOperation: new LauncherActivityOperationSnapshot(
            OperationId: 41,
            OperationType: LauncherOperationType.GameUpdate,
            TargetId: "wotlk-classic",
            TargetName: "WotLK Classic",
            DisplayName: "WotLK Classic",
            Phase: LauncherActivityPhase.Downloading,
            ProgressMode: LauncherActivityProgressMode.Determinate,
            Percent: 68,
            BytesProcessed: 68 * 1024 * 1024,
            BytesTotal: 100 * 1024 * 1024,
            BytesPerSecond: 18.4 * 1024 * 1024,
            Eta: TimeSpan.FromMinutes(2),
            FilesProcessed: null,
            FilesTotal: null,
            CanUserCancel: true,
            IsCancellationRequested: false,
            AddonPosition: null,
            AddonTotal: null,
            ErrorCategory: null,
            NavigationTarget: LauncherActivityNavigationTarget.Game),
        PendingItems: ImmutableArray<LauncherActivityPendingItem>.Empty,
        RecentItems: ImmutableArray<LauncherActivityRecentItem>.Empty);

    private static LauncherActivitySnapshot VerifySnapshot() => new(
        Sequence: 2,
        ActiveOperation: new LauncherActivityOperationSnapshot(
            OperationId: 42,
            OperationType: LauncherOperationType.GameVerify,
            TargetId: "wotlk-classic",
            TargetName: "WotLK Classic",
            DisplayName: "WotLK Classic",
            Phase: LauncherActivityPhase.LoadingManifest,
            ProgressMode: LauncherActivityProgressMode.Indeterminate,
            Percent: null,
            BytesProcessed: null,
            BytesTotal: null,
            BytesPerSecond: null,
            Eta: null,
            FilesProcessed: null,
            FilesTotal: null,
            CanUserCancel: false,
            IsCancellationRequested: false,
            AddonPosition: null,
            AddonTotal: null,
            ErrorCategory: null,
            NavigationTarget: LauncherActivityNavigationTarget.Game),
        PendingItems: ImmutableArray<LauncherActivityPendingItem>.Empty,
        RecentItems: ImmutableArray<LauncherActivityRecentItem>.Empty);

    private static LauncherActivitySnapshot AddonBatchSnapshot() => new(
        Sequence: 2,
        ActiveOperation: new LauncherActivityOperationSnapshot(
            51,
            LauncherOperationType.AddonBatchUpdate,
            "questie",
            "Questie",
            "Questie",
            LauncherActivityPhase.Downloading,
            LauncherActivityProgressMode.Determinate,
            35,
            35,
            100,
            8,
            TimeSpan.FromSeconds(5),
            null,
            null,
            true,
            false,
            1,
            4,
            null,
            LauncherActivityNavigationTarget.Addons),
        PendingItems:
        [
            new("dbm", "Deadly Boss Mods", LauncherOperationType.AddonBatchUpdate,
                LauncherActivityNavigationTarget.Addons),
            new("details", "Details!", LauncherOperationType.AddonBatchUpdate,
                LauncherActivityNavigationTarget.Addons)
        ],
        RecentItems: ImmutableArray<LauncherActivityRecentItem>.Empty);

    private static LauncherActivitySnapshot HistorySnapshot() => new(
        Sequence: 3,
        ActiveOperation: null,
        PendingItems: ImmutableArray<LauncherActivityPendingItem>.Empty,
        RecentItems:
        [
            new(
                52,
                LauncherOperationType.AddonUpdate,
                LauncherOperationOutcome.Succeeded,
                DateTimeOffset.Now,
                "questie",
                "Questie",
                null,
                LauncherActivityNavigationTarget.Addons),
            new(
                41,
                LauncherOperationType.GameUpdate,
                LauncherOperationOutcome.Failed,
                DateTimeOffset.Now.AddMinutes(-1),
                "wotlk-classic",
                "WotLK Classic",
                "Network",
                LauncherActivityNavigationTarget.Game)
        ]);

    private static void LoadV2Resources(Application application)
    {
        foreach (string path in new[]
        {
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
            "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
            "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
        })
        {
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(path, UriKind.Relative)
            });
        }
    }

    private static void SavePng(FrameworkElement visual, string path)
    {
        visual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static Rect BoundsInAncestor(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
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
        where T : FrameworkElement =>
        scope.FindName(name) as T
        ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; Actuel={actual}.");
        }
    }
}
