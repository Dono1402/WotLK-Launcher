using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class FriendsDrawerWpfTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizePreviewIsolation();
        CharacterizePreviewData();
        await ValidateWpfLayoutsInteractionsAndCapturesAsync(captureDirectory);
        Console.WriteLine("Atlas friends drawer WPF preview OK (03B, isolated presentation only).");
        return 0;
    }

    private static void CharacterizePreviewIsolation()
    {
        Equal(LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-friends=populated"]),
            "preview-friends sans --ui-v2 doit être refusé avant composition.");
        Equal(LauncherStartupMode.UiV2FriendsPreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-friends=incoming"]),
            "preview-friends doit utiliser une branche isolée.");
        Equal(LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-friends", "--preview-auth=login"]),
            "Amis et Auth ne doivent jamais composer deux previews.");
        Equal(LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-friends", "--preview-account=profile"]),
            "Amis et Compte ne doivent jamais composer deux previews.");
        Equal(LauncherStartupMode.Legacy, App.ResolveStartupMode([]),
            "Le lancement sans argument doit rester legacy.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode(["--ui-v2"]),
            "La V2 réelle doit rester distincte du preview Amis.");
    }

    private static void CharacterizePreviewData()
    {
        Equal(FriendsPreviewScenario.Populated, Resolve("--preview-friends"), "Le scénario par défaut est incorrect.");
        Equal(FriendsPreviewScenario.Empty, Resolve("--preview-friends=empty"), "Le scénario vide est absent.");
        Equal(FriendsPreviewScenario.IncomingRequests, Resolve("--preview-friends=incoming"), "Les demandes reçues sont absentes.");
        Equal(FriendsPreviewScenario.OutgoingRequests, Resolve("--preview-friends=outgoing"), "Les demandes envoyées sont absentes.");
        Equal(FriendsPreviewScenario.AddFriend, Resolve("--preview-friends=add-friend"), "L’ajout réussi est absent.");
        Equal(FriendsPreviewScenario.AddFriendError, Resolve("--preview-friends=add-friend-error"), "L’erreur d’ajout est absente.");
        Equal(FriendsPreviewScenario.AvatarFallback, Resolve("--preview-friends=avatar-fallback"), "Le fallback avatar est absent.");
        Equal(FriendsPreviewScenario.NetworkError, Resolve("--preview-friends=network-error"), "L’erreur réseau est absente.");

        FriendsUiState populated = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.Populated);
        True(populated.Current.IsPreview, "Les données fictives doivent être marquées preview.");
        Equal(6, populated.Current.Friends.Length, "Le scénario peuplé doit permettre de tester le défilement.");
        True(populated.Current.HasIncomingRequests && populated.Current.HasOutgoingRequests,
            "Les deux types de demandes doivent être représentés.");
        True(populated.Current.Friends.Any(friend => !friend.HasAvatarTheme),
            "Le scénario doit couvrir l’absence d’avatar.");

        FriendsUiState empty = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.Empty);
        True(empty.Current.ShowsGlobalEmpty, "L’état vide doit être explicite.");
        FriendsUiState network = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.NetworkError);
        True(network.Current.ShowsError && network.Current.LoadState == FriendsViewLoadState.Failed,
            "L’erreur réseau fictive doit être contrôlée.");

        FriendsViewState before = populated.Current;
        populated.RefreshCommand.Execute(null);
        populated.SendRequestCommand.Execute(null);
        populated.AcceptRequestCommand.Execute(12u);
        populated.RemoveFriendCommand.Execute(2u);
        True(ReferenceEquals(before, populated.Current),
            "Les commandes preview ne doivent appeler aucun service ni muter les données.");

        static FriendsPreviewScenario Resolve(string argument) =>
            FriendsPreviewArguments.ResolveScenario(["--ui-v2", argument]);
    }

    private static async Task ValidateWpfLayoutsInteractionsAndCapturesAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasFriendsDrawerWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
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
                await ValidateScenariosAndCapturesAsync(captureDirectory);
                await ValidateFocusAndClosureAsync();
                await ValidateBusyRequestSurvivesDrawerClosureAsync();
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

    private static async Task ValidateScenariosAndCapturesAsync(string? captureDirectory)
    {
        (FriendsPreviewScenario Scenario, double Width, double Height, string FileName)[] scenarios =
        [
            (FriendsPreviewScenario.Populated, 1440, 860, "01-friends-populated-1440x860.png"),
            (FriendsPreviewScenario.IncomingRequests, 1080, 680, "02-friends-incoming-1080x680.png"),
            (FriendsPreviewScenario.Empty, 1440, 860, "03-friends-empty-1440x860.png"),
            (FriendsPreviewScenario.NetworkError, 1440, 860, "04-friends-network-error-1440x860.png"),
            (FriendsPreviewScenario.AvatarFallback, 1080, 680, "05-friends-avatar-fallback-1080x680.png")
        ];
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((FriendsPreviewScenario scenario, double width, double height, string fileName) in scenarios)
        {
            LauncherShellV2 window = CreateWindow(scenario, width, height, activate: false);
            window.Show();
            try
            {
                await DelayAndPumpAsync(240);
                ValidateCommonContract(window, scenario);
                if (!string.IsNullOrWhiteSpace(captureDirectory))
                {
                    SavePng(window, Path.Combine(captureDirectory, fileName));
                }
            }
            finally
            {
                window.Close();
                await PumpAsync(DispatcherPriority.Background);
            }
        }
    }

    private static void ValidateCommonContract(
        LauncherShellV2 window,
        FriendsPreviewScenario scenario)
    {
        FriendsDrawerV2 drawer = window.FriendsOverlay;
        True(window.IsPreviewMode, "Le drawer doit rester dans une fenêtre preview isolée.");
        True(!window.HasRealAuthenticationAttached, "Le preview Amis ne doit créer aucun service réel.");
        True(window.FriendsState.Current.IsPreview, "Le drawer doit utiliser uniquement les données fictives.");
        Equal(ShellOverlayKind.Friends, window.CurrentOverlay, "Le drawer doit s’ouvrir directement.");
        True(window.FriendsState.IsOpen && drawer.Visibility == Visibility.Visible,
            "Le panneau Amis doit être visible.");
        Equal(ScrollBarVisibility.Disabled, drawer.ScrollHost.HorizontalScrollBarVisibility,
            "Aucune barre horizontale n’est autorisée.");
        True(drawer.ScrollHost.ScrollableWidth <= 0.5, "Le contenu ne doit pas déborder horizontalement.");
        True(Math.Abs(Required<Border>(drawer, "DrawerPanel").ActualWidth - 360) <= 1,
            "Le drawer doit conserver une largeur proche de 360 px.");
        True(Required<Button>(window, "MinimizeWindowButton").IsVisible
            && Required<Button>(window, "MaximizeWindowButton").IsVisible
            && Required<Button>(window, "CloseWindowButton").IsVisible,
            "Les commandes de fenêtre doivent rester accessibles.");

        TextBox search = drawer.SearchInput;
        Button send = Required<Button>(drawer, "SendFriendRequestButton");
        True(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(search)),
            "La recherche doit exposer un nom accessible.");
        True(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(send)),
            "L’envoi doit exposer un nom accessible.");

        if (scenario == FriendsPreviewScenario.Populated)
        {
            True(drawer.ScrollHost.ScrollableHeight > 0,
                "Une liste longue doit défiler verticalement sans compresser les lignes.");
            TextBlock longName = Descendants<TextBlock>(drawer)
                .First(text => text.Text.StartsWith("nerya-", StringComparison.Ordinal));
            Equal(TextTrimming.CharacterEllipsis, longName.TextTrimming,
                "Les noms longs doivent utiliser une ellipse.");
            True(Descendants<Button>(drawer).Any(button =>
                    string.Equals(AutomationProperties.GetName(button), "Retirer de mes amis", StringComparison.Ordinal)
                    && button.IsEnabled),
                "Le retrait d’ami doit rester disponible via la confirmation locale.");
        }
        if (scenario == FriendsPreviewScenario.IncomingRequests)
        {
            Equal(Visibility.Visible, Required<StackPanel>(drawer, "IncomingRequestsSection").Visibility,
                "Les demandes reçues doivent avoir leur section.");
            True(Descendants<Button>(drawer).Any(button =>
                    string.Equals(AutomationProperties.GetName(button), "Accepter la demande", StringComparison.Ordinal)
                    && button.Command is not null),
                "Le bouton Accepter doit être lié.");
        }
        if (scenario == FriendsPreviewScenario.Empty)
        {
            Equal(Visibility.Visible, Required<StackPanel>(drawer, "FriendsEmptyState").Visibility,
                "L’état vide doit rester lisible.");
        }
        if (scenario == FriendsPreviewScenario.NetworkError)
        {
            True(Descendants<TextBlock>(drawer).Any(text =>
                    text.Text == "Impossible de joindre Atlas pour le moment."),
                "L’erreur réseau contrôlée doit être visible.");
        }
        if (window.Width <= 1080)
        {
            Rect playBounds = BoundsInAncestor(
                (FrameworkElement)Required<GameViewV2>(window, "GameView").PrimaryActionFocusTarget,
                window);
            True(playBounds.Bottom <= window.ActualHeight + 0.5,
                "Le bouton Jouer doit rester dans la fenêtre à 1080 × 680.");
        }
    }

    private static async Task ValidateFocusAndClosureAsync()
    {
        LauncherShellV2 window = CreateWindow(FriendsPreviewScenario.Populated, 1440, 860, activate: true);
        window.Show();
        try
        {
            await DelayAndPumpAsync(240);
            FriendsDrawerV2 drawer = window.FriendsOverlay;
            Button friendsButton = Required<Button>(window, "FriendsButton");
            Button profileButton = Required<Button>(window, "ProfileButton");
            drawer.FocusFirstControl();
            await PumpAsync(DispatcherPriority.Input);
            True(drawer.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Le focus initial doit entrer dans le drawer.");

            Required<Button>(drawer, "CloseButton")
                .MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));
            await PumpAsync(DispatcherPriority.Input);
            True(drawer.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Shift+Tab doit rester cyclique dans le drawer.");

            profileButton.Focus();
            await PumpAsync(DispatcherPriority.Input);
            True(drawer.ContainsKeyboardFocusTarget(Keyboard.FocusedElement as DependencyObject),
                "Le focus clavier ne doit pas passer derrière le drawer.");

            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(220);
            True(drawer.IsFullyClosed, "Échap doit retirer le voile et le hit-test.");
            Equal(friendsButton, Keyboard.FocusedElement, "Le focus doit revenir au bouton Amis.");

            RaiseClick(friendsButton);
            await DelayAndPumpAsync(220);
            True(window.FriendsState.IsOpen, "Le bouton Amis doit rouvrir le drawer.");
            RaiseMouseDown(Required<Border>(drawer, "Scrim"));
            await DelayAndPumpAsync(220);
            True(drawer.IsFullyClosed, "Un clic extérieur doit fermer le drawer.");

            RaiseClick(friendsButton);
            RaiseClick(friendsButton);
            RaiseClick(friendsButton);
            await DelayAndPumpAsync(240);
            True(window.FriendsState.IsOpen && !drawer.IsFullyClosed,
                "Des ouvertures/fermetures rapides doivent finir dans un état stable.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateBusyRequestSurvivesDrawerClosureAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            RestoreResult = true,
            Session = FakeLauncherAuthService.CreateSession(),
            EnsureFreshHandler = _ => Task.FromResult(true)
        };
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        using LauncherFriendsCoordinator runtime = new(
            session,
            authentication,
            lifetime.Token,
            () => authentication.Session?.Profile,
            _ => { });
        FriendsUiState friendsState = new(FriendsStateAdapter.Project(runtime.CurrentSnapshot));
        LauncherShellV2 window = new(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            friendsState,
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop())
        {
            Width = 1440,
            Height = 860,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        using FriendsStateAdapter adapter = new(
            friendsState,
            runtime,
            window.Dispatcher);
        using FriendsCommands commands = new(
            runtime,
            friendsState,
            window.Dispatcher);
        window.AttachFriends(commands);
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        authentication.SendFriendRequestHandler = async (_, _) =>
        {
            requestStarted.TrySetResult();
            await releaseRequest.Task.ConfigureAwait(false);
            return "OK";
        };
        authentication.FriendsHandler = _ => Task.FromResult<IReadOnlyList<LauncherFriend>>([]);
        window.Show();
        try
        {
            await DelayAndPumpAsync(220);
            Equal(LauncherSessionRestoreStatus.Restored, (await session.RestoreOnceAsync()).Status,
                "La session du scénario busy doit être restaurée.");
            await PumpAsync(DispatcherPriority.DataBind);
            True(!window.IsPreviewMode, "Le branchement des commandes doit être testé dans une coque réelle.");
            RaiseClick(Required<Button>(window, "FriendsButton"));
            await WaitForAsync(() => window.FriendsState.Current.LoadState == FriendsViewLoadState.Loaded);
            Equal(1, authentication.GetFriendsCalls,
                "L’ouverture du drawer réel doit charger la liste exactement une fois.");
            window.FriendsState.SearchText = "target";
            window.FriendsState.SendRequestCommand.Execute(null);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(FriendsViewOperation.SendingRequest, window.FriendsState.Current.Operation,
                "L’envoi réel doit publier un état occupé.");
            True(!Required<Button>(window.FriendsOverlay, "RefreshFriendsButton").IsEnabled,
                "Un rafraîchissement concurrent doit être impossible.");
            True(!window.FriendsOverlay.SearchInput.IsEnabled,
                "L’ajout doit être bloqué pendant une opération.");
            Equal(1, authentication.SendFriendRequestCalls, "Le premier clic doit produire une requête.");
            window.FriendsState.SendRequestCommand.Execute(null);
            Equal(1, authentication.SendFriendRequestCalls, "Un double clic ne doit pas être mis en file.");

            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(220);
            True(window.FriendsOverlay.IsFullyClosed,
                "Le drawer doit pouvoir se fermer pendant la requête sans l’annuler brutalement.");
            releaseRequest.TrySetResult();
            await WaitForAsync(() => window.FriendsState.Current.Operation == FriendsViewOperation.None);
            True(window.FriendsOverlay.IsFullyClosed && !window.FriendsState.IsOpen,
                "La fin tardive de la requête ne doit pas rouvrir le drawer.");
            Equal(string.Empty, window.FriendsState.SearchText,
                "Le champ doit être vidé après un envoi réellement réussi.");
        }
        finally
        {
            releaseRequest.TrySetResult();
            runtime.BeginShutdown();
            session.BeginShutdown();
            lifetime.Cancel();
            await runtime.WaitForIdleAsync(TimeSpan.FromSeconds(1));
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static LauncherShellV2 CreateWindow(
        FriendsPreviewScenario scenario,
        double width,
        double height,
        bool activate)
    {
        LauncherShellV2 window = new(GamePreviewScenario.Ready, scenario)
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = activate
        };
        return window;
    }

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

    private static void RaisePreviewKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("La source WPF du contrôle est absente.");
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private static void RaiseMouseDown(UIElement target)
    {
        target.RaiseEvent(new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = target
        });
    }

    private static async Task DelayAndPumpAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await PumpAsync(DispatcherPriority.ApplicationIdle);
    }

    private static async Task PumpAsync(DispatcherPriority priority) =>
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, priority);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Le scénario WPF Amis n’a pas atteint l’état attendu.");
            }
            await DelayAndPumpAsync(20);
        }
    }

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
