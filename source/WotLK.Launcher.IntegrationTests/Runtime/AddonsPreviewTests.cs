using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class AddonsPreviewTests
{
    private static DpiScale _observedDpi;

    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        CharacterizeStartupIsolation();
        CharacterizeScenariosAndLocalState();
        CharacterizePreviewInteractions();
        await ValidateWpfNavigationLayoutsAndCapturesAsync(captureDirectory);
        Console.WriteLine(
            $"Addons WPF preview OK (04A.1, DPI={_observedDpi.PixelsPerInchX:0}x{_observedDpi.PixelsPerInchY:0}).");
        return 0;
    }

    private static void CharacterizeStartupIsolation()
    {
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-addons=default"]),
            "Le preview Addons sans --ui-v2 doit être refusé.");
        Equal(
            LauncherStartupMode.UiV2AddonsPreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-addons=updates"]),
            "Le preview Addons doit posséder sa branche isolée.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-addons=default", "--preview-auth=login"]),
            "Deux previews dédiés ne doivent pas être composés ensemble.");
        Equal(
            LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--ui-v2", "--preview-addons=many", "--preview-settings=game"]),
            "Addons et Paramètres preview ne doivent pas être combinés.");
        Equal(
            LauncherStartupMode.Legacy,
            App.ResolveStartupMode([]),
            "Le démarrage sans argument doit rester legacy.");
        Equal(
            LauncherStartupMode.UiV2,
            App.ResolveStartupMode(["--ui-v2"]),
            "La V2 réelle doit conserver une branche distincte.");
    }

    private static void CharacterizeScenariosAndLocalState()
    {
        Equal(AddonsPreviewScenario.Default, Resolve("default"), "Le scénario par défaut est absent.");
        Equal(AddonsPreviewScenario.Updates, Resolve("updates"), "Le scénario updates est absent.");
        Equal(AddonsPreviewScenario.Detail, Resolve("detail"), "Le scénario detail est absent.");
        Equal(AddonsPreviewScenario.Installing, Resolve("installing"), "Le scénario installing est absent.");
        Equal(AddonsPreviewScenario.Empty, Resolve("empty"), "Le scénario empty est absent.");
        Equal(AddonsPreviewScenario.Error, Resolve("error"), "Le scénario error est absent.");
        Equal(AddonsPreviewScenario.Many, Resolve("many"), "Le scénario many est absent.");
        Equal(AddonsPreviewScenario.GameRunning, Resolve("game-running"), "Le scénario game-running est absent.");

        AddonsUiState state = AddonsPreviewData.Create(AddonsPreviewScenario.Default);
        Equal(6, state.Current.TotalCount, "Le catalogue normal doit montrer 6 addons locaux.");
        Equal(0, AddonsPreviewData.Create(AddonsPreviewScenario.Empty).Current.TotalCount,
            "Le catalogue vide doit montrer 0 addon.");
        Equal(20, AddonsPreviewData.Create(AddonsPreviewScenario.Updates).Current.TotalCount,
            "Le catalogue de mises à jour doit montrer 20 addons.");
        True(state.Current.VisibleAddons.SequenceEqual(
                state.Current.VisibleAddons.OrderBy(addon => addon.Name, StringComparer.CurrentCultureIgnoreCase)),
            "La projection visible doit être alphabétique.");
        AddonsUiState detailCatalog = AddonsPreviewData.Create(AddonsPreviewScenario.Detail);
        True(detailCatalog.Current.Catalog.Count(addon => addon.HasOfficialIcon) == 13,
            "Les 13 entrées principales doivent réutiliser les logos déjà archivés.");

        True(detailCatalog.UpdateSearch("Questie"), "La recherche preview doit être locale et active.");
        Equal(1, detailCatalog.Current.VisibleAddons.Length, "La recherche par nom doit filtrer le catalogue.");
        Equal("questie", detailCatalog.Current.VisibleAddons[0].Id, "La recherche par nom a retourné le mauvais addon.");
        detailCatalog.UpdateSearch("butins");
        Equal("atlaslootclassic", detailCatalog.Current.VisibleAddons.Single().Id,
            "La recherche doit également utiliser la description.");
        detailCatalog.UpdateSearch(string.Empty);

        state.SelectFilter(AddonCatalogFilter.Installed);
        True(state.Current.VisibleAddons.Length == state.Current.InstalledCount,
            "Le filtre Installés doit refléter le compteur.");
        True(state.Current.VisibleAddons.All(addon => addon.IsInstalled),
            "Le filtre Installés contient une entrée non installée.");
        state.SelectFilter(AddonCatalogFilter.Updates);
        True(state.Current.VisibleAddons.Length == state.Current.UpdateCount,
            "Le filtre Mises à jour doit refléter le compteur.");
        True(state.Current.VisibleAddons.All(addon => addon.NeedsUpdate),
            "Le filtre Mises à jour contient une entrée à jour.");

        AddonsUiState installing = AddonsPreviewData.Create(AddonsPreviewScenario.Installing);
        True(installing.Current.Catalog.Any(addon => addon.VisualState == AddonVisualState.Installing),
            "L'état Installation est absent.");
        True(installing.Current.Catalog.Any(addon => addon.VisualState == AddonVisualState.Updating),
            "L'état Mise à jour active est absent.");
        True(installing.Current.Catalog.Any(addon => addon.VisualState == AddonVisualState.Removing),
            "L'état Suppression est absent.");
        True(installing.Current.Catalog.Where(addon => addon.IsBusy).All(addon => !addon.CanInvokePrimary),
            "Une opération fictive active ne doit pas être redéclenchable.");

        AddonsUiState many = AddonsPreviewData.Create(AddonsPreviewScenario.Many);
        Equal(50, many.Current.TotalCount, "Le scénario de charge doit contenir 50 entrées.");
        True(many.Current.Catalog.Any(addon => !addon.HasOfficialIcon),
            "Le scénario étendu doit couvrir le fallback générique.");

        AddonsUiState runtimePlaceholder = AddonsPreviewData.CreateRuntimePlaceholder();
        True(!runtimePlaceholder.Current.IsPreview, "L'état V2 réel ne doit pas se déclarer preview.");
        True(!runtimePlaceholder.UpdateSearch("Questie"), "La V2 réelle ne doit pas simuler une recherche locale.");
        True(!runtimePlaceholder.SelectFilter(AddonCatalogFilter.Updates), "La V2 réelle ne doit pas simuler les filtres.");
        True(!runtimePlaceholder.UpdateAll(), "La V2 réelle ne doit pas simuler de mise à jour.");
        Equal(0, runtimePlaceholder.Current.TotalCount, "Aucun catalogue réel ne doit être chargé en 04A.1.");

        static AddonsPreviewScenario Resolve(string scenario) =>
            AddonsPreviewArguments.ResolveScenario(["--ui-v2", $"--preview-addons={scenario}"]);
    }

    private static void CharacterizePreviewInteractions()
    {
        AddonsUiState state = AddonsPreviewData.Create(AddonsPreviewScenario.Detail);
        True(state.Current.IsDetailOpen, "Le scénario détail doit ouvrir Questie.");
        Equal("questie", state.Current.SelectedAddon?.Id, "Le panneau détail doit cibler Questie.");
        True(state.RequestRemoveSelected(), "Questie installé doit proposer la suppression fictive.");
        True(state.Current.IsDeleteConfirmationOpen, "La confirmation de suppression doit s'ouvrir.");
        state.CancelRemove();
        True(!state.Current.IsDeleteConfirmationOpen && state.Current.IsDetailOpen,
            "Annuler doit revenir au détail sans fermer le panneau.");
        True(state.RequestRemoveSelected() && state.ConfirmRemove(),
            "La suppression fictive doit pouvoir démarrer après confirmation.");
        Equal(AddonVisualState.Removing, state.Current.SelectedAddon?.VisualState,
            "La confirmation doit produire l'état Suppression local.");

        AddonsUiState defaultState = AddonsPreviewData.Create(AddonsPreviewScenario.Default);
        AddonUiItem notInstalled = defaultState.Current.Catalog.First(addon =>
            addon.VisualState == AddonVisualState.NotInstalled);
        True(defaultState.InvokePrimary(notInstalled.Id), "Installer doit fonctionner localement en preview.");
        Equal(AddonVisualState.Installing,
            defaultState.Current.Catalog.First(addon => addon.Id == notInstalled.Id).VisualState,
            "Installer doit produire une progression fictive.");

        AddonsUiState updates = AddonsPreviewData.Create(AddonsPreviewScenario.Updates);
        int expectedUpdates = updates.Current.UpdateCount;
        True(expectedUpdates > 1 && updates.UpdateAll(), "Tout mettre à jour doit couvrir le lot legacy raisonnable.");
        Equal(expectedUpdates,
            updates.Current.Catalog.Count(addon => addon.VisualState == AddonVisualState.Updating),
            "Tout mettre à jour doit agir uniquement sur les mises à jour visibles dans l'état local.");

        AddonsUiState gameRunning = AddonsPreviewData.Create(AddonsPreviewScenario.GameRunning);
        AddonUiItem action = gameRunning.Current.Catalog.First(addon => addon.CanInvokePrimary);
        True(gameRunning.Current.IsGameRunning && gameRunning.InvokePrimary(action.Id),
            "Le jeu ouvert ne doit pas bloquer fictivement les changements d'addons.");
    }

    private static async Task ValidateWpfNavigationLayoutsAndCapturesAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasAddonsPreviewWpfHarness"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(75));
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
                ValidatePackagedLogos();
                await ValidateRequestedLayoutsAsync(captureDirectory);
                await ValidateNavigationAndLocalControlsAsync();
                await ValidateDeleteConfirmationAsync();
                await ValidateGameRunningStateAsync();
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

    private static async Task ValidateRequestedLayoutsAsync(string? captureDirectory)
    {
        (string FileName, double Width, double Height, AdaptiveLayoutMode Mode, AddonsPreviewScenario Scenario)[] layouts =
        [
            ("01-addons-default-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Default),
            ("02-addons-updates-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Updates),
            ("03-addons-detail-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Detail),
            ("04-addons-compact-1080x680.png", 1080, 680, AdaptiveLayoutMode.Stacked, AddonsPreviewScenario.Default),
            ("05-addons-large-1920x1080.png", 1920, 1080, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Many),
            ("06-addons-installing-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Installing),
            ("07-addons-empty-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Empty),
            ("08-addons-error-1440x860.png", 1440, 860, AdaptiveLayoutMode.Wide, AddonsPreviewScenario.Error)
        ];

        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
        }

        foreach ((string fileName, double width, double height, AdaptiveLayoutMode expectedMode, AddonsPreviewScenario scenario) in layouts)
        {
            LauncherShellV2 window = CreateWindow(width, height, scenario);
            window.Show();
            try
            {
                await DelayAndPumpAsync(220);
                _observedDpi = VisualTreeHelper.GetDpi(window);
                ValidateCommonVisualContract(window, expectedMode, scenario);

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

    private static void ValidateCommonVisualContract(
        LauncherShellV2 window,
        AdaptiveLayoutMode expectedMode,
        AddonsPreviewScenario scenario)
    {
        True(window.IsPreviewMode, "La fenêtre Addons doit rester en mode preview.");
        True(!window.HasRealAuthenticationAttached, "Aucun service d'authentification réel ne doit être attaché.");
        Equal(LauncherShellPage.Addons, window.CurrentPage, "La preview Addons doit ouvrir directement sa page.");
        Equal(expectedMode, window.ShellState.LayoutMode, "L'état adaptatif est incorrect.");
        Equal(Visibility.Collapsed, Required<GameViewV2>(window, "GameView").Visibility,
            "La page Jeu ne doit pas rester visible derrière Addons.");
        Equal(Visibility.Visible, window.AddonsPage.Visibility, "AddonsViewV2 doit être visible.");
        Equal("Active", Required<Button>(window, "AddonsNavigationButton").Tag as string,
            "L'onglet Addons doit montrer l'état actif.");

        AddonsViewV2 view = window.AddonsPage;
        Grid contentFrame = Required<Grid>(view, "ContentFrame");
        True(contentFrame.ActualWidth <= 1360.5, "Le contenu large ne doit pas être étiré excessivement.");
        Equal(ScrollBarVisibility.Disabled,
            ScrollViewer.GetHorizontalScrollBarVisibility(view.ListHost),
            "La liste ne doit jamais créer de barre horizontale.");
        True(VirtualizingPanel.GetIsVirtualizing(view.ListHost), "La liste doit activer la virtualisation.");
        Equal(VirtualizationMode.Recycling,
            VirtualizingPanel.GetVirtualizationMode(view.ListHost),
            "La liste doit recycler ses conteneurs.");

        Rect toolbarBounds = BoundsInAncestor(Required<Border>(view, "SearchField"), window);
        True(toolbarBounds.Left >= 0 && toolbarBounds.Right <= window.ActualWidth + 0.5,
            "La recherche doit rester entièrement accessible.");
        Rect updateBounds = BoundsInAncestor(Required<Button>(view, "UpdateAllButton"), window);
        if (Required<Button>(view, "UpdateAllButton").Visibility == Visibility.Visible)
        {
            True(updateBounds.Right <= window.ActualWidth + 0.5,
                "Tout mettre à jour doit rester dans la fenêtre.");
        }

        if (scenario == AddonsPreviewScenario.Detail)
        {
            Equal(Visibility.Visible, Required<Grid>(view, "DetailLayer").Visibility,
                "Le scénario détail doit afficher son panneau.");
            double expectedWidth = expectedMode == AdaptiveLayoutMode.Stacked ? 360 : 390;
            Near(expectedWidth, view.DetailsHost.ActualWidth, 0.6, "Largeur du détail incorrecte.");
        }
        if (scenario == AddonsPreviewScenario.Empty)
        {
            Equal(Visibility.Visible, Required<Grid>(view, "EmptyState").Visibility,
                "Le catalogue vide doit afficher son état dédié.");
        }
        if (scenario == AddonsPreviewScenario.Error)
        {
            Equal(Visibility.Visible, Required<Border>(view, "CatalogErrorBanner").Visibility,
                "L'erreur de catalogue doit être visible sans exception brute.");
        }
        if (scenario == AddonsPreviewScenario.Installing)
        {
            True(window.AddonsState.Current.Catalog.Count(addon => addon.IsBusy) == 3,
                "La capture progression doit montrer les trois phases prévues.");
        }
        if (scenario == AddonsPreviewScenario.Many)
        {
            Equal(50, view.ListHost.Items.Count, "La liste WPF doit recevoir les 50 entrées.");
            True(view.ListHost.ItemContainerGenerator.ContainerFromIndex(49) is null,
                "La cinquantième ligne hors viewport ne doit pas être matérialisée.");
        }
    }

    private static async Task ValidateNavigationAndLocalControlsAsync()
    {
        LauncherShellV2 window = new(GamePreviewScenario.Ready)
        {
            Width = 1440,
            Height = 860,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        window.Show();
        try
        {
            await DelayAndPumpAsync(160);
            Equal(LauncherShellPage.Game, window.CurrentPage, "Le preview standard doit démarrer sur Jeu.");
            RaiseClick(Required<Button>(window, "AddonsNavigationButton"));
            await PumpAsync(DispatcherPriority.Input);
            Equal(LauncherShellPage.Addons, window.CurrentPage, "L'onglet Addons doit ouvrir AddonsViewV2.");
            True(!window.AddonsState.Current.IsPreview,
                "Le preview Jeu standard ne doit pas inventer un catalogue Addons.");
            RaiseClick(Required<Button>(window, "GameNavigationButton"));
            await PumpAsync(DispatcherPriority.Input);
            Equal(LauncherShellPage.Game, window.CurrentPage, "L'onglet Jeu doit restaurer GameViewV2.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }

        LauncherShellV2 addonsWindow = CreateWindow(1440, 860, AddonsPreviewScenario.Default);
        addonsWindow.Show();
        try
        {
            await DelayAndPumpAsync(160);
            AddonsViewV2 view = addonsWindow.AddonsPage;
            view.SearchBox.Text = "butins";
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(1, addonsWindow.AddonsState.Current.VisibleAddons.Length,
                "La saisie WPF doit filtrer localement la description.");
            view.SearchBox.Text = string.Empty;
            RaiseClick(Required<Button>(view, "UpdatesFilterButton"));
            await PumpAsync(DispatcherPriority.DataBind);
            True(addonsWindow.AddonsState.Current.VisibleAddons.All(addon => addon.NeedsUpdate),
                "Le bouton Mises à jour doit piloter le filtre local.");
            RaiseClick(Required<Button>(view, "AllFilterButton"));
            await PumpAsync(DispatcherPriority.DataBind);
            view.ListHost.SelectedIndex = 0;
            await PumpAsync(DispatcherPriority.Input);
            True(view.IsDetailOpen, "Sélectionner une ligne doit ouvrir le détail superposé.");
            RaisePreviewKey(addonsWindow, Key.Escape);
            await PumpAsync(DispatcherPriority.Input);
            True(!view.IsDetailOpen, "Échap doit fermer le panneau de détail.");
        }
        finally
        {
            addonsWindow.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateDeleteConfirmationAsync()
    {
        LauncherShellV2 window = CreateWindow(1440, 860, AddonsPreviewScenario.Detail);
        window.Show();
        try
        {
            await DelayAndPumpAsync(140);
            AddonsViewV2 view = window.AddonsPage;
            RaiseClick(Required<Button>(view, "RemoveSelectedAddonButton"));
            await PumpAsync(DispatcherPriority.Input);
            True(view.IsDeleteConfirmationOpen, "Supprimer doit demander confirmation.");
            Equal("Supprimer Questie ?", Required<TextBlock>(view, "DeleteConfirmationTitle").Text,
                "Le titre de confirmation est incorrect.");
            TextBlock confirmation = Descendants<TextBlock>(view.DeleteConfirmationHost)
                .First(text => text.Text.StartsWith("Seuls les fichiers", StringComparison.Ordinal));
            Equal("Seuls les fichiers gérés par Atlas seront supprimés.", confirmation.Text,
                "Le périmètre de suppression doit être explicite.");
            RaisePreviewKey(window, Key.Escape);
            await PumpAsync(DispatcherPriority.Input);
            True(!view.IsDeleteConfirmationOpen && view.IsDetailOpen,
                "Échap doit fermer uniquement la confirmation supérieure.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task ValidateGameRunningStateAsync()
    {
        LauncherShellV2 window = CreateWindow(1080, 680, AddonsPreviewScenario.GameRunning);
        window.Show();
        try
        {
            await DelayAndPumpAsync(120);
            AddonsViewV2 view = window.AddonsPage;
            Equal(Visibility.Visible, Required<Border>(view, "GameRunningBanner").Visibility,
                "Le message /reload doit apparaître lorsque le jeu est ouvert.");
            True(window.AddonsState.Current.Catalog.Any(addon => addon.CanInvokePrimary),
                "Le jeu ouvert ne doit pas désactiver les actions du catalogue preview.");
        }
        finally
        {
            window.Close();
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static LauncherShellV2 CreateWindow(
        double width,
        double height,
        AddonsPreviewScenario scenario) =>
        new(GamePreviewScenario.Ready, scenario)
        {
            Width = width,
            Height = height,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };

    private static void ValidatePackagedLogos()
    {
        foreach (AddonUiItem addon in AddonsPreviewData.Create(AddonsPreviewScenario.Detail).Current.Catalog)
        {
            True(addon.HasOfficialIcon, $"Le logo officiel de {addon.Name} doit être déclaré.");
            Uri resourceUri = new(addon.IconPath, UriKind.Relative);
            System.Windows.Resources.StreamResourceInfo? resource = Application.GetResourceStream(resourceUri);
            True(resource is not null && resource.Stream.Length > 0,
                $"Le logo embarqué de {addon.Name} est absent.");
            resource?.Stream.Dispose();
        }
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
        FrameworkElement renderVisual = visual is Window { Content: FrameworkElement content }
            ? content
            : visual;
        InvalidateVisualTree(renderVisual);
        renderVisual.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(renderVisual.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(renderVisual.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(renderVisual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void InvalidateVisualTree(DependencyObject root)
    {
        if (root is UIElement element)
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
            element.InvalidateVisual();
        }

        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            InvalidateVisualTree(VisualTreeHelper.GetChild(root, index));
        }
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

    private static T Required<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        root.FindName(name) as T
        ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void RaisePreviewKey(UIElement target, Key key)
    {
        PresentationSource source = PresentationSource.FromVisual(target)
            ?? throw new InvalidOperationException("La source WPF du contrôle est absente.");
        target.RaiseEvent(new KeyEventArgs(
            Keyboard.PrimaryDevice,
            source,
            Environment.TickCount,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

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

    private static void Near(double expected, double actual, double tolerance, string message)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"{message} Attendu={expected:0.##}; Actuel={actual:0.##}.");
        }
    }
}
