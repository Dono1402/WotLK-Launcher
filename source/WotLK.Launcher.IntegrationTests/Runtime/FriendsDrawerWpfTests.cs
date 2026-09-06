using System.IO;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;
using Ellipse = System.Windows.Shapes.Ellipse;

internal static class FriendsDrawerWpfTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizePreviewIsolation();
        CharacterizePreviewData();
        await ValidateWpfLayoutsInteractionsAndCapturesAsync(captureDirectory);
        Console.WriteLine("Atlas friends drawer WPF preview OK (03B.1, isolated presentation only).");
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
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode([]),
            "Le lancement sans argument doit ouvrir la V2 réelle.");
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
        Equal(FriendsPreviewScenario.Avatars, Resolve("--preview-friends=avatars"), "Le scénario avatars est absent.");
        Equal(FriendsPreviewScenario.MixedAvatars, Resolve("--preview-friends=mixed"), "Le scénario mixte est absent.");
        Equal(FriendsPreviewScenario.AvatarChanged, Resolve("--preview-friends=avatar-changed"), "Le changement d’avatar est absent.");
        Equal(FriendsPreviewScenario.NetworkStale, Resolve("--preview-friends=network-stale"), "Le réseau obsolète est absent.");
        Equal(FriendsPreviewScenario.ManyFriends, Resolve("--preview-friends=100"), "Le scénario 100 amis est absent.");
        Equal("ACTIVE CHARACTER", LauncherLocalization.TranslateFromFrench("PERSONNAGE ACTIF"),
            "Le nouveau profil d’ami doit rester traduit en anglais.");

        FriendsUiState populated = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.Populated);
        True(populated.Current.IsPreview, "Les données fictives doivent être marquées preview.");
        Equal(6, populated.Current.Friends.Length, "Le scénario peuplé doit permettre de tester le défilement.");
        True(populated.Current.HasIncomingRequests && populated.Current.HasOutgoingRequests,
            "Les deux types de demandes doivent être représentés.");
        True(populated.Current.Friends.Any(friend => !friend.HasAvatarTheme),
            "Le scénario doit couvrir l’absence d’avatar.");
        True(populated.Current.Friends.Any(friend => friend.HasAvatarImage)
            && populated.Current.Friends.Any(friend => !friend.HasAvatarImage),
            "Le scénario peuplé doit mélanger photos synchronisées et fallback.");

        FriendUiItem profile = populated.Current.Friends.First(friend => friend.AllCharacters.Length == 3);
        Equal(2, profile.OtherCharacters.Length, "Le personnage mis en avant ne doit pas être répété.");
        True(profile.OtherCharacters.All(character => character.Name != profile.FeaturedCharacter?.Name),
            "Les autres personnages doivent exclure le personnage principal.");
        FriendUiItem offline = profile with
        {
            IsOnline = false,
            Characters = profile.AllCharacters.Select(character => character with { IsOnline = false }).ToImmutableArray()
        };
        Equal("DERNIER PERSONNAGE JOUÉ", offline.FeaturedCharacterTitle, "Hors ligne, il ne s'agit pas d'un personnage actif.");
        Equal("LAST CHARACTER PLAYED", LauncherLocalization.TranslateFromFrench(offline.FeaturedCharacterTitle),
            "Le libellé hors ligne doit être traduit.");
        FriendUiItem online = offline with
        {
            IsOnline = true,
            Characters = offline.AllCharacters.Select((character, index) => character with { IsOnline = index == 1 }).ToImmutableArray()
        };
        Equal("Frostoon", online.FeaturedCharacter?.Name ?? "", "Un personnage réellement en ligne doit être prioritaire.");
        Equal("PERSONNAGE ACTIF", online.FeaturedCharacterTitle, "Le personnage actif doit suivre la présence réelle.");
        FriendUiItem emptyProfile = profile with { Characters = [] };
        True(!emptyProfile.HasFeaturedCharacter && !emptyProfile.HasOtherCharacters,
            "Un compte sans personnage ne doit pas créer de fiche vide.");

        FriendsUiState empty = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.Empty);
        True(empty.Current.ShowsGlobalEmpty, "L’état vide doit être explicite.");
        FriendsUiState network = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.NetworkError);
        True(network.Current.ShowsError && network.Current.LoadState == FriendsViewLoadState.Failed,
            "L’erreur réseau fictive doit être contrôlée.");
        FriendsUiState stale = LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.NetworkStale);
        True(stale.Current.IsStale && !stale.Current.ShowsError && stale.Current.HasFriends,
            "Un échec périodique doit conserver les données sans grande erreur.");
        Equal(100, LauncherV2PreviewData.CreateFriends(FriendsPreviewScenario.ManyFriends).Current.Friends.Length,
            "Le scénario de charge doit contenir 100 amis.");

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
                foreach (byte classId in new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 11 })
                {
                    FriendCharacterUiItem character = new("Test", "Classe", 80, "", false, "", classId);
                    BitmapImage icon = new(new Uri("pack://application:,,," + character.ClassIconPath));
                    True(icon.PixelWidth >= 32 && icon.PixelHeight >= 32, "Chaque icône de classe doit être embarquée et décodable.");
                }
                await ValidateScenariosAndCapturesAsync(captureDirectory);
                await ValidateFocusAndClosureAsync();
                await ValidateBusyRequestSurvivesDrawerClosureAsync();
                await ValidateSingleTimerAcrossRepeatedDrawerOpeningsAsync();
                await ValidateSharedAvatarCacheAndStaleCompletionAsync();
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
            (FriendsPreviewScenario.Avatars, 1440, 860, "01-friends-avatars-1440x860.png"),
            (FriendsPreviewScenario.MixedAvatars, 1080, 680, "02-friends-mixed-1080x680.png"),
            (FriendsPreviewScenario.IncomingRequests, 1440, 860, "03-friends-requests-1440x860.png"),
            (FriendsPreviewScenario.AvatarChanged, 1080, 680, "04-friends-avatar-changed-1080x680.png"),
            (FriendsPreviewScenario.NetworkStale, 1440, 860, "05-friends-network-stale-1440x860.png"),
            (FriendsPreviewScenario.ManyFriends, 1440, 860, "06-friends-100-1440x860.png")
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
                if (scenario == FriendsPreviewScenario.Avatars)
                {
                    DpiScale dpi = VisualTreeHelper.GetDpi(window);
                    Console.WriteLine(
                        $"Friends WPF DPI observed: {dpi.PixelsPerInchX:0} x {dpi.PixelsPerInchY:0} "
                        + $"({dpi.DpiScaleX * 100:0}% x {dpi.DpiScaleY * 100:0}%).");
                }
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

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            LauncherShellV2 profileWindow = CreateWindow(
                FriendsPreviewScenario.Populated,
                1440,
                860,
                activate: false);
            profileWindow.Show();
            try
            {
                await DelayAndPumpAsync(240);
                FriendUiItem friend = profileWindow.FriendsState.Current.Friends
                    .First(item => item.HasCharacters && !string.IsNullOrWhiteSpace(item.DisplayBio));
                profileWindow.FriendsState.OpenFriendProfile(friend);
                await DelayAndPumpAsync(120);
                SavePng(
                    profileWindow,
                    Path.Combine(captureDirectory, "07-friend-profile-1440x860.png"));
                profileWindow.Width = 1080;
                profileWindow.Height = 680;
                await DelayAndPumpAsync(120);
                SavePng(profileWindow, Path.Combine(captureDirectory, "08-friend-profile-1080x680.png"));
                LauncherLocalization.SetLocale("en-US");
                await DelayAndPumpAsync(150);
                SavePng(profileWindow, Path.Combine(captureDirectory, "09-friend-profile-en-1080x680.png"));
            }
            finally
            {
                LauncherLocalization.SetLocale("fr-FR");
                profileWindow.Close();
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
        True(drawer.FindName("RefreshFriendsButton") is null,
            "L’actualisation manuelle doit disparaître au profit du timer social.");
        Equal(
            $"{window.FriendsState.Current.Friends.Length} ami{(window.FriendsState.Current.Friends.Length > 1 ? "s" : string.Empty)} · {window.FriendsState.Current.OnlineCount} en ligne",
            window.FriendsState.Current.FriendsSummary,
            "Le résumé compact doit afficher le total avant le nombre en ligne.");
        True(!drawer.IsAddFriendEditorOpen
            && Required<Border>(drawer, "AddFriendPanel").ActualHeight <= 0.5,
            "Le champ d’ajout doit rester replié par défaut.");
        Geometry moreIcon = (Geometry)Application.Current.FindResource("AtlasV2.Icon.MoreHorizontal");
        True(moreIcon.Bounds.Width > 10 && moreIcon.Bounds.Height > 2,
            "L’icône d’actions doit conserver trois points visibles au lieu de s’écraser.");
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
            Equal(Visibility.Visible, Required<StackPanel>(drawer, "OnlineFriendsSection").Visibility,
                "Les amis en jeu doivent avoir leur propre section.");
            Equal(Visibility.Visible, Required<StackPanel>(drawer, "OfflineFriendsSection").Visibility,
                "Les amis hors ligne doivent avoir leur propre section.");
            Button actions = Descendants<Button>(drawer).First(button =>
                string.Equals(AutomationProperties.GetName(button), "Actions pour cet ami", StringComparison.Ordinal));
            True(actions.IsEnabled && actions.Opacity >= 0.9,
                "Le menu d’actions doit rester visible et facile à découvrir.");
            RaiseClick(actions);
            Popup popup = actions.Tag as Popup
                ?? throw new InvalidOperationException("Le menu contextuel de l’ami est absent.");
            True(popup.IsOpen,
                "Le bouton d’actions doit ouvrir le menu contextuel.");
            Button remove = popup.Child as Button
                ?? throw new InvalidOperationException("Le retrait d’ami doit être le seul cadre du menu contextuel.");
            True(string.Equals(
                    AutomationProperties.GetName(remove),
                    "Retirer de mes amis",
                    StringComparison.Ordinal),
                "Le retrait d’ami doit être rangé dans le menu contextuel sans cadre imbriqué.");
            popup.IsOpen = false;
        }
        if (scenario is FriendsPreviewScenario.Avatars
            or FriendsPreviewScenario.MixedAvatars
            or FriendsPreviewScenario.AvatarChanged)
        {
            True(Descendants<Ellipse>(drawer).Any(ellipse =>
                    AutomationProperties.GetName(ellipse) == "Photo de profil Atlas"
                    && ellipse.Visibility == Visibility.Visible
                    && ellipse.Fill is ImageBrush { ImageSource: not null }),
                "Une vraie photo circulaire doit être rendue par le drawer.");
        }
        if (scenario == FriendsPreviewScenario.MixedAvatars)
        {
            True(window.FriendsState.Current.Friends.Any(friend => friend.HasAvatarImage)
                && window.FriendsState.Current.Friends.Any(friend => !friend.HasAvatarImage),
                "Le rendu mixte doit conserver le fallback par initiale.");
        }
        if (scenario == FriendsPreviewScenario.IncomingRequests)
        {
            Equal(Visibility.Visible, Required<StackPanel>(drawer, "IncomingRequestsSection").Visibility,
                "Les demandes reçues doivent avoir leur section.");
            True(Descendants<Button>(drawer).Any(button =>
                    string.Equals(AutomationProperties.GetName(button), "Accepter la demande", StringComparison.Ordinal)
                    && button.Command is not null),
                "Le bouton Accepter doit être lié.");
            True(window.FriendsState.Current.IncomingRequests.Any(request => request.HasAvatarImage),
                "Les demandes reçues doivent également afficher les photos disponibles.");
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
        if (scenario == FriendsPreviewScenario.NetworkStale)
        {
            True(window.FriendsState.Current.IsStale
                && !window.FriendsState.Current.ShowsError
                && window.FriendsState.Current.HasFriends,
                "Le réseau périodique indisponible doit conserver les données connues.");
        }
        if (scenario == FriendsPreviewScenario.ManyFriends)
        {
            Equal(100, window.FriendsState.Current.Friends.Length,
                "Les 100 amis fictifs doivent être présents.");
            True(drawer.ScrollHost.ScrollableHeight > 0,
                "La liste de 100 amis doit défiler sans compression.");
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

            FriendUiItem friend = window.FriendsState.Current.Friends.First(item => item.HasCharacters);
            window.FriendsState.OpenFriendProfile(friend);
            await PumpAsync(DispatcherPriority.DataBind);
            True(drawer.IsFriendProfileOpen
                && Required<Grid>(drawer, "FriendProfilePanel").Visibility == Visibility.Visible,
                "Cliquer sur un ami doit pouvoir ouvrir sa fiche détaillée.");
            Equal(3, window.FriendsState.SelectedFriend?.AllCharacters.Length ?? 0,
                "La fiche doit afficher tous les personnages fournis par Atlas.");
            Equal(2, Required<ItemsControl>(drawer, "OtherCharactersList").Items.Count,
                "La liste WPF doit exclure le personnage déjà présenté dans l'encart principal.");
            FriendCharacterUiItem featured = (FriendCharacterUiItem)Required<ContentControl>(drawer, "FeaturedCharacterCard").Content;
            True(featured.HasClassIcon && featured.IsFeatured, "La fiche doit distinguer le personnage mis en avant avec son icône de classe.");
            True(!string.IsNullOrWhiteSpace(window.FriendsState.SelectedFriend?.DisplayBio),
                "La fiche doit exposer la bio du profil.");
            RaisePreviewKey(window, Key.Escape);
            await PumpAsync(DispatcherPriority.Input);
            True(window.FriendsState.IsOpen && !drawer.IsFriendProfileOpen,
                "Échap doit revenir à la liste avant de fermer le panneau.");

            RaiseClick(Required<Button>(drawer, "AddFriendToggleButton"));
            await DelayAndPumpAsync(180);
            True(drawer.IsAddFriendEditorOpen
                && Required<Border>(drawer, "AddFriendPanel").ActualHeight >= 60,
                "Le bouton plus doit déployer le champ d’ajout avec sa transition.");
            Equal(drawer.SearchInput, Keyboard.FocusedElement,
                "Le champ d’ajout doit recevoir le focus après son ouverture.");

            RaisePreviewKey(window, Key.Escape);
            await DelayAndPumpAsync(180);
            True(window.FriendsState.IsOpen && !drawer.IsAddFriendEditorOpen,
                "Échap doit d’abord replier l’ajout sans fermer la liste.");

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
            RaiseClick(Required<Button>(window.FriendsOverlay, "AddFriendToggleButton"));
            await DelayAndPumpAsync(180);
            window.FriendsState.SearchText = "target";
            window.FriendsOverlay.SearchInput.Focus();
            Keyboard.Focus(window.FriendsOverlay.SearchInput);
            window.FriendsState.SendRequestCommand.Execute(null);
            await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(FriendsViewOperation.SendingRequest, window.FriendsState.Current.Operation,
                "L’envoi réel doit publier un état occupé.");
            True(!window.FriendsState.RefreshCommand.CanExecute(null),
                "Un rafraîchissement concurrent doit être impossible.");
            True(window.FriendsOverlay.SearchInput.IsEnabled,
                "Le champ doit rester éditable pendant l’envoi pour pouvoir corriger un pseudo erroné.");
            Equal(window.FriendsOverlay.SearchInput, Keyboard.FocusedElement,
                "L’envoi ne doit pas déplacer le focus vers le bouton de fermeture.");
            Equal(1, authentication.SendFriendRequestCalls, "Le premier clic doit produire une requête.");
            window.FriendsState.SendRequestCommand.Execute(null);
            Equal(1, authentication.SendFriendRequestCalls, "Un double clic ne doit pas être mis en file.");

            RaisePreviewKey(window, Key.Escape);
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

    private static async Task ValidateSingleTimerAcrossRepeatedDrawerOpeningsAsync()
    {
        FakeLauncherAuthService authentication = new()
        {
            RestoreResult = true,
            Session = FakeLauncherAuthService.CreateSession(),
            EnsureFreshHandler = _ => Task.FromResult(true),
            FriendsHandler = _ => Task.FromResult<IReadOnlyList<LauncherFriend>>([])
        };
        CountingFriendsTimeProvider time = new();
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        using LauncherFriendsCoordinator runtime = new(
            session,
            authentication,
            lifetime.Token,
            () => authentication.Session?.Profile,
            _ => { },
            time);
        FriendsUiState friendsState = new(FriendsStateAdapter.Project(runtime.CurrentSnapshot));
        LauncherShellV2 window = CreateRuntimeWindow(friendsState);
        using FriendsStateAdapter adapter = new(friendsState, runtime, window.Dispatcher);
        using FriendsCommands commands = new(runtime, friendsState, window.Dispatcher);
        window.AttachFriends(commands);
        window.Show();
        try
        {
            Equal(LauncherSessionRestoreStatus.Restored, (await session.RestoreOnceAsync()).Status,
                "La session du test timer doit être restaurée.");
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(1, time.CreateTimerCalls,
                "LauncherFriendsCoordinator doit créer un seul timer social.");
            True(time.Timer.IsEnabled,
                "Le timer doit être actif pendant la session authentifiée.");

            Button friendsButton = Required<Button>(window, "FriendsButton");
            for (int index = 0; index < 10; index++)
            {
                RaiseClick(friendsButton);
                await WaitForAsync(() => friendsState.IsOpen);
                RaisePreviewKey(window, Key.Escape);
                await DelayAndPumpAsync(190);
                True(window.FriendsOverlay.IsFullyClosed,
                    "Chaque fermeture répétée doit finir proprement.");
            }

            Equal(1, time.CreateTimerCalls,
                "Dix ouvertures du drawer ne doivent jamais créer un autre timer.");
            int beforeTick = authentication.GetFriendsCalls;
            time.Timer.Fire();
            await runtime.WaitForIdleAsync(TimeSpan.FromSeconds(1));
            Equal(beforeTick + 1, authentication.GetFriendsCalls,
                "Le timer unique doit réutiliser le pipeline de rafraîchissement.");
        }
        finally
        {
            runtime.BeginShutdown();
            session.BeginShutdown();
            lifetime.Cancel();
            await runtime.WaitForIdleAsync(TimeSpan.FromSeconds(1));
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateSharedAvatarCacheAndStaleCompletionAsync()
    {
        byte[] oldImageBytes = CreateSolidPng(Color.FromRgb(0xD9, 0x55, 0x62));
        byte[] currentImageBytes = CreateSolidPng(Color.FromRgb(0x51, 0xD7, 0xA2));
        Guid avatarId = Guid.Parse("58ca8a4d-f5a4-4e72-b1f8-8f4aeec6ab31");
        AvatarDescriptor oldAvatar = Descriptor(avatarId, 1);
        AvatarDescriptor currentAvatar = Descriptor(avatarId, 2);
        AvatarDescriptor missingAvatar = Descriptor(
            Guid.Parse("d57388e8-096d-4f4e-8d24-c86cbdf35ca6"),
            1);
        TaskCompletionSource oldDownloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseOldDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SocialAvatarMediaClient media = new()
        {
            DownloadHandler = async (descriptor, _, _) =>
            {
                if (descriptor.AvatarId == missingAvatar.AvatarId)
                {
                    return AvatarMediaDownloadResult.NotFound;
                }
                if (descriptor.Version == 1)
                {
                    oldDownloadStarted.TrySetResult();
                    await releaseOldDownload.Task.ConfigureAwait(false);
                    return AvatarMediaDownloadResult.Success(oldImageBytes);
                }
                return AvatarMediaDownloadResult.Success(currentImageBytes);
            }
        };
        FakeLauncherAuthService authentication = new()
        {
            RestoreResult = true,
            Session = FakeLauncherAuthService.CreateSession(),
            EnsureFreshHandler = _ => Task.FromResult(true)
        };
        IReadOnlyList<LauncherFriend> currentFriends = BuildAvatarFriends(oldAvatar, missingAvatar);
        authentication.FriendsHandler = _ => Task.FromResult(currentFriends);
        CountingFriendsTimeProvider time = new();
        using CancellationTokenSource lifetime = new();
        using LauncherSessionCoordinator session = new(authentication, lifetime.Token, _ => { });
        using LauncherFriendsCoordinator runtime = new(
            session,
            authentication,
            lifetime.Token,
            () => authentication.Session?.Profile,
            _ => { },
            time);
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "AtlasFriendsAvatarCacheTest",
            Guid.NewGuid().ToString("N"));
        using AvatarImageCache avatarImages = new(
            media,
            cacheRoot,
            lifetime.Token,
            session.NotifyAuthenticatedRequestUnauthorized);
        FriendsUiState friendsState = new(FriendsStateAdapter.Project(runtime.CurrentSnapshot));
        LauncherShellV2 window = CreateRuntimeWindow(friendsState);
        using FriendsStateAdapter adapter = new(
            friendsState,
            runtime,
            window.Dispatcher,
            avatarImages);
        using FriendsCommands commands = new(runtime, friendsState, window.Dispatcher);
        window.AttachFriends(commands);
        window.Show();
        try
        {
            Equal(LauncherSessionRestoreStatus.Restored, (await session.RestoreOnceAsync()).Status,
                "La session du test avatar doit être restaurée.");
            RaiseClick(Required<Button>(window, "FriendsButton"));
            await WaitForAsync(() => friendsState.Current.LoadState == FriendsViewLoadState.Loaded);
            await oldDownloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            FriendUiItem loading = friendsState.Current.Friends.Single(friend => friend.AccountId == 2);
            True(!loading.HasAvatarImage && loading.Initial == "A",
                "L’initiale doit être visible immédiatement pendant le chargement.");

            FriendsDrawerV2 drawer = window.FriendsOverlay;
            drawer.ScrollHost.ScrollToVerticalOffset(180);
            await PumpAsync(DispatcherPriority.Render);
            double scrollOffset = drawer.ScrollHost.VerticalOffset;
            True(scrollOffset > 0,
                "Le scénario avatar doit être assez long pour vérifier le scroll.");

            currentFriends = BuildAvatarFriends(currentAvatar, missingAvatar);
            await RequiredFriendsCompletion(runtime.TryRefresh());
            await WaitForAsync(() =>
                friendsState.Current.Friends.Single(friend => friend.AccountId == 2).HasAvatarImage);
            FriendUiItem updated = friendsState.Current.Friends.Single(friend => friend.AccountId == 2);
            ImageSource currentImage = updated.AvatarImage
                ?? throw new InvalidOperationException("La variante avatar courante n’a pas été appliquée.");
            Equal(2UL, updated.AvatarVersion,
                "La nouvelle version avatar doit être la seule publiée.");
            True(Math.Abs(drawer.ScrollHost.VerticalOffset - scrollOffset) <= 1,
                "Une mise à jour d’avatar ne doit pas réinitialiser le scroll.");

            await WaitForAsync(() => media.RequestedAvatars.Count(request =>
                request.AvatarId == missingAvatar.AvatarId
                && request.Version == missingAvatar.Version) == 4);
            int requestsBeforeUnchangedRefresh = media.RequestedSizes.Count;
            await RequiredFriendsCompletion(runtime.TryRefresh());
            await DelayAndPumpAsync(80);
            Equal(requestsBeforeUnchangedRefresh, media.RequestedSizes.Count,
                "Un AvatarId/version inchangé, y compris après 404, ne doit pas être rechargé.");

            releaseOldDownload.TrySetResult();
            await DelayAndPumpAsync(180);
            FriendUiItem afterLateDownload = friendsState.Current.Friends.Single(friend => friend.AccountId == 2);
            Equal(2UL, afterLateDownload.AvatarVersion,
                "Un ancien téléchargement ne doit pas rétablir une version obsolète.");
            True(ReferenceEquals(currentImage, afterLateDownload.AvatarImage),
                "Le callback obsolète ne doit pas remplacer l’image courante.");
            True(!friendsState.Current.Friends.Single(friend => friend.AccountId == 3).HasAvatarImage,
                "Un 404 média doit conserver silencieusement le fallback.");
            True(media.RequestedSizes.Count > 0
                && media.RequestedSizes.All(size => size == FriendsStateAdapter.SocialAvatarSize),
                "Le drawer doit demander exclusivement la variante 64 px.");

            await RequiredFriendsCompletion(runtime.TryRemoveFriend(2));
            await PumpAsync(DispatcherPriority.DataBind);
            True(friendsState.Current.Friends.All(friend => friend.AccountId != 2),
                "Un ami retiré ne doit plus rester lié au snapshot social.");
        }
        finally
        {
            releaseOldDownload.TrySetResult();
            runtime.BeginShutdown();
            session.BeginShutdown();
            lifetime.Cancel();
            await runtime.WaitForIdleAsync(TimeSpan.FromSeconds(1));
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
            TryDeleteDirectory(cacheRoot);
        }
    }

    private static LauncherShellV2 CreateRuntimeWindow(FriendsUiState friendsState)
    {
        return new LauncherShellV2(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            friendsState,
            LauncherV2PreviewData.CreateProfile(),
            LauncherV2PreviewData.CreateSettings(),
            LauncherV2PreviewData.CreateAccount(),
            LauncherV2PreviewData.CreateAvatarCrop())
        {
            Width = 1080,
            Height = 680,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
    }

    private static IReadOnlyList<LauncherFriend> BuildAvatarFriends(
        AvatarDescriptor current,
        AvatarDescriptor missing)
    {
        List<LauncherFriend> result =
        [
            new LauncherFriend(2, "avatarfriend", "ice", "accepted", true, "PhotoMage", 80, 8, 1, null, current),
            new LauncherFriend(3, "missingphoto", null, "accepted", false, null, null, null, null, null, missing)
        ];
        result.AddRange(Enumerable.Range(4, 38).Select(index => new LauncherFriend(
            (uint)index,
            $"friend{index:00}",
            index % 2 == 0 ? "emerald" : null,
            "accepted",
            false,
            $"Character{index:00}",
            40,
            1,
            1,
            null)));
        return result;
    }

    private static AvatarDescriptor Descriptor(Guid avatarId, ulong version)
    {
        string root = $"/media/avatars/{avatarId:N}/{version}";
        return new AvatarDescriptor(
            avatarId,
            version,
            $"{root}/32.png",
            $"{root}/64.png",
            $"{root}/128.png",
            $"{root}/256.png");
    }

    private static byte[] CreateSolidPng(Color color)
    {
        const int size = 64;
        byte[] pixels = new byte[size * size * 4];
        for (int index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = 255;
        }

        BitmapSource bitmap = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            size * 4);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async Task<FriendsActionCompletion> RequiredFriendsCompletion(
        FriendsActionStartResult start)
    {
        True(start.IsStarted && start.Completion is not null,
            $"L’opération Amis devait démarrer, statut={start.Status}.");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
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

    private sealed class SocialAvatarMediaClient : IAvatarMediaClient
    {
        internal Func<AvatarDescriptor, int, CancellationToken, Task<AvatarMediaDownloadResult>>?
            DownloadHandler { get; init; }

        internal ConcurrentBag<int> RequestedSizes { get; } = [];

        internal ConcurrentBag<(Guid AvatarId, ulong Version)> RequestedAvatars { get; } = [];

        public Task<AvatarProfileReadResult> GetProfileAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AvatarDescriptor> UploadAvatarAsync(
            AvatarUploadRequest upload,
            IProgress<AvatarUploadTransferProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAvatarAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AvatarMediaDownloadResult> DownloadAvatarAsync(
            AvatarDescriptor descriptor,
            int size,
            CancellationToken cancellationToken)
        {
            RequestedSizes.Add(size);
            RequestedAvatars.Add((descriptor.AvatarId, descriptor.Version));
            return DownloadHandler?.Invoke(descriptor, size, cancellationToken)
                ?? Task.FromResult(AvatarMediaDownloadResult.NotFound);
        }
    }

    private sealed class CountingFriendsTimeProvider : TimeProvider
    {
        internal int CreateTimerCalls { get; private set; }

        internal CountingFriendsTimer Timer { get; private set; } = null!;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            CreateTimerCalls++;
            Timer = new CountingFriendsTimer(callback, state, dueTime, period);
            return Timer;
        }
    }

    private sealed class CountingFriendsTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private bool _isDisposed;

        internal CountingFriendsTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
        }

        internal TimeSpan DueTime { get; private set; }

        internal TimeSpan Period { get; private set; }

        internal bool IsEnabled => !_isDisposed && DueTime != Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            DueTime = dueTime;
            Period = period;
            return true;
        }

        internal void Fire()
        {
            if (IsEnabled)
            {
                _callback(_state);
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            DueTime = Timeout.InfiniteTimeSpan;
            Period = Timeout.InfiniteTimeSpan;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
