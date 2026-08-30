using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher.Game;

namespace WotLK.Launcher;

public partial class MainWindow : Window
{
    private enum ToastKind
    {
        Info,
        Success,
        Warning,
        Error
    }

    private enum LauncherPage
    {
        Game,
        Addons,
        Friends,
        News,
        Server,
        Account,
        Settings
    }

    private const string LauncherUpdateManifestUrl = "http://152.228.225.7/launcher/launcher-update.json";
    private const string AddonCatalogUrl = "https://animeclub.fr/wotlk/addons/catalog.json";
    private const string LauncherUpdateRequestHeader = "X-WotLK-Launcher-Update";
    private const string LauncherUpdateRequestMarker = "1";
    private static readonly TimeSpan LauncherUpdateCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly StringComparer AddonNameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), ignoreCase: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly LegacyMainWindowDependencies _dependencies;
    private readonly ILegacyStartupObserver _startupObserver;
    private readonly GameClientStateReader _gameClientStateReader;
    private readonly ILauncherAuthService _auth;
    private readonly HttpClient _http;
    private readonly LauncherSettings _settings;
    private readonly ILegacyDispatcherTimer _launcherUpdateTimer;
    private readonly ILegacyDispatcherTimer _friendRefreshTimer;
    private readonly ILegacyDispatcherTimer _toastTimer;
    private readonly ObservableCollection<AddonSelectionItem> _addonItems = [];
    private readonly ObservableCollection<LauncherNews> _newsItems = [];
    private readonly ObservableCollection<LauncherDeviceSession> _sessionItems = [];
    private readonly ObservableCollection<LauncherFriend> _friendItems = [];
    private readonly ObservableCollection<LauncherFriend> _incomingFriendItems = [];
    private readonly ObservableCollection<LauncherFriend> _outgoingFriendItems = [];
    private readonly List<AddonSelectionItem> _allAddonItems = [];
    private CancellationTokenSource? _downloadCancellation;
    private LauncherUpdateManifest? _launcherUpdate;
    private AddonCatalog? _addonCatalog;
    private GameAction _gameAction = GameAction.Install;
    private bool _isRefreshingGameAction;
    private bool _isCheckingLauncherUpdate;
    private bool _isLoadingAddonCatalog;
    private bool _isAddonTabActive;
    private bool _isApplyingAddons;
    private bool _isInitializingUi = true;
    private bool _isLoadingAccountData;
    private bool _isLoadingFriends;
    private bool _isLoadingServerStatus;
    private string _selectedAddonCategory = "All";
    private string _selectedAddonView = "Catalog";
    private string _selectedAddonSort = "Name";
    private LauncherPage _currentPage = LauncherPage.Game;
    private string? _announcedLauncherUpdateHash;
    private string? _announcedGameUpdateVersion;

    public MainWindow()
        : this(LegacyMainWindowDependencies.CreateProduction())
    {
    }

    internal MainWindow(LegacyMainWindowDependencies dependencies)
    {
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        _startupObserver = dependencies.StartupObserver;
        _gameClientStateReader = new GameClientStateReader(dependencies.HasPlayableClient);
        InitializeComponent();
        _startupObserver.Record(LegacyStartupEvent.ComponentsInitialized);
        _auth = dependencies.CreateAuthentication();
        _startupObserver.Record(LegacyStartupEvent.AuthenticationCreated);
        _http = dependencies.CreateAuthorizedHttpClient(() => _auth.AccessToken);
        _startupObserver.Record(LegacyStartupEvent.AuthorizedHttpClientCreated);
        AddonItemsControl.ItemsSource = _addonItems;
        NewsItemsControl.ItemsSource = _newsItems;
        SessionsItemsControl.ItemsSource = _sessionItems;
        FriendItemsControl.ItemsSource = _friendItems;
        GameFriendItemsControl.ItemsSource = _friendItems;
        IncomingFriendItemsControl.ItemsSource = _incomingFriendItems;
        OutgoingFriendItemsControl.ItemsSource = _outgoingFriendItems;

        Title = "Arthas Launcher";
        TitleText.Text = "ARTHAS";
        TitleBarText.Text = "WotLK Classic";
        VersionText.Text = GetLauncherVersionText();
        ChromeVersionText.Text = GetLauncherVersionText();

        _settings = dependencies.LoadSettings();
        _startupObserver.Record(LegacyStartupEvent.SettingsLoaded);
        dependencies.SaveSettings(_settings);
        _startupObserver.Record(LegacyStartupEvent.SettingsSaved);
        dependencies.PrepareGameDirectory(_settings.InstallPath);
        _startupObserver.Record(LegacyStartupEvent.GameDirectoryPrepared);
        InstallPathBox.Text = _settings.InstallPath;
        SettingsInstallPathBox.Text = _settings.InstallPath;
        UpdateAddonInstallPathText();
        SetLanguageSelection(_settings.GameLocale);
        SetSettingsLanguageSelection(_settings.GameLocale);
        AutomaticUpdatesCheckBox.IsChecked = _settings.AutomaticLauncherUpdates;
        CloseOnGameStartCheckBox.IsChecked = _settings.CloseLauncherOnGameStart;
        _isInitializingUi = false;

        _launcherUpdateTimer = dependencies.CreateTimer(
            LauncherUpdateCheckInterval,
            DispatcherPriority.Background);
        _startupObserver.Record(LegacyStartupEvent.LauncherUpdateTimerCreated);
        _launcherUpdateTimer.Tick += LauncherUpdateTimer_Tick;
        _friendRefreshTimer = dependencies.CreateTimer(
            TimeSpan.FromSeconds(15),
            DispatcherPriority.Background);
        _startupObserver.Record(LegacyStartupEvent.FriendRefreshTimerCreated);
        _friendRefreshTimer.Tick += FriendRefreshTimer_Tick;
        _toastTimer = dependencies.CreateTimer(
            TimeSpan.FromSeconds(8),
            DispatcherPriority.Normal);
        _startupObserver.Record(LegacyStartupEvent.ToastTimerCreated);
        _toastTimer.Tick += ToastTimer_Tick;

        AppendLog("Launcher prêt.");
        SetInitialGameActionFromDisk();
        _startupObserver.Record(LegacyStartupEvent.InitialGameActionSet);
        NavigateTo(LauncherPage.Game);
        _startupObserver.Record(LegacyStartupEvent.GamePageSelected);
        if (_settings.AutomaticLauncherUpdates)
        {
            _startupObserver.Record(LegacyStartupEvent.LauncherUpdateCheckScheduled);
            _ = CheckLauncherUpdateAsync();
        }
        Loaded += MainWindow_Loaded;
        _startupObserver.Record(LegacyStartupEvent.LoadedSubscribed);
        if (_settings.AutomaticLauncherUpdates)
        {
            _launcherUpdateTimer.Start();
            _startupObserver.Record(LegacyStartupEvent.LauncherUpdateTimerStarted);
        }
        _friendRefreshTimer.Start();
        _startupObserver.Record(LegacyStartupEvent.FriendRefreshTimerStarted);
    }

    protected override void OnClosed(EventArgs e)
    {
        _startupObserver.Record(LegacyStartupEvent.WindowClosing);
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
            _startupObserver.Record(LegacyStartupEvent.OperationCancellationRequested);
        }
        _launcherUpdateTimer.Stop();
        _launcherUpdateTimer.Tick -= LauncherUpdateTimer_Tick;
        _friendRefreshTimer.Stop();
        _friendRefreshTimer.Tick -= FriendRefreshTimer_Tick;
        _toastTimer.Stop();
        _toastTimer.Tick -= ToastTimer_Tick;
        Loaded -= MainWindow_Loaded;
        _http.Dispose();
        _auth.Dispose();
        _startupObserver.Record(LegacyStartupEvent.WindowDisposed);
        base.OnClosed(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RestoreSessionAndAnalyzeAsync();
    }

    private async Task RestoreSessionAndAnalyzeAsync()
    {
        try
        {
            bool sessionRestored;
            _startupObserver.Record(LegacyStartupEvent.SessionRestoreStarted);
            try
            {
                sessionRestored = await _auth.RestoreAsync();
            }
            finally
            {
                _startupObserver.Record(LegacyStartupEvent.SessionRestoreCompleted);
            }

            if (sessionRestored)
            {
                CompleteAuthentication();
                AppendLog($"Session restaurée pour {_auth.Session!.Profile.Username}.");
                _startupObserver.Record(LegacyStartupEvent.InitialRemoteAnalysisStarted);
                try
                {
                    await RefreshGameActionAsync();
                }
                finally
                {
                    _startupObserver.Record(LegacyStartupEvent.InitialRemoteAnalysisCompleted);
                }
                return;
            }
        }
        catch (OperationCanceledException)
        {
            LoginErrorText.Text = "Atlas met trop de temps à répondre. Tu peux réessayer.";
            AppendLog("Restauration de session expirée.");
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            LoginErrorText.Text = "Atlas est temporairement indisponible. Tu peux réessayer.";
            AppendLog("Restauration de session impossible: " + ex.Message);
        }

        ShowLogin();
    }

    private async void LauncherSelfUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadCancellation = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            var manifest = _launcherUpdate ?? await LoadLauncherUpdateManifestAsync(_downloadCancellation.Token);
            await UpdateLauncherAsync(manifest, _downloadCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Annulé.");
            AppendLog("Mise à jour du launcher annulée.");
        }
        catch (Exception ex)
        {
            SetStatus("Erreur.");
            AppendLog("Erreur mise à jour launcher: " + ex.Message);
            ShowToast("Mise à jour du launcher", ex.Message, ToastKind.Error);
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            SetBusy(false);
        }
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteGameActionAsync();
    }

    private async Task ExecuteGameActionAsync()
    {
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
            return;
        }

        if (!EnsureAuthenticated())
        {
            return;
        }

        SaveSettingsFromUi();
        if (!EnsureGameDirectoryWritable())
        {
            return;
        }

        if (_gameAction == GameAction.Play)
        {
            await PlayGameAsync();
            return;
        }

        _downloadCancellation = new CancellationTokenSource();
        SetBusy(true);

        try
        {
            if (!await _auth.EnsureFreshAsync())
            {
                ShowLogin();
                return;
            }

            await InstallOrUpdateAsync(_downloadCancellation.Token);
            await RefreshGameActionAsync();
        }
        catch (OperationCanceledException)
        {
            SetStatus("Annule.");
            AppendLog("Operation annulee.");
        }
        catch (Exception ex)
        {
            SetStatus("Erreur.");
            AppendLog("Erreur: " + ex.Message);
            ShowToast("Erreur du launcher", ex.Message, ToastKind.Error);
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            SetBusy(false);
        }
    }

    private async void BrowseInstallPathButton_Click(object sender, RoutedEventArgs e)
    {
        await BrowseInstallPathAsync();
    }

    private async void SettingsBrowseInstallPathButton_Click(object sender, RoutedEventArgs e)
    {
        await BrowseInstallPathAsync();
    }

    private async Task BrowseInstallPathAsync()
    {
        if (_downloadCancellation is not null)
        {
            return;
        }

        SaveSettingsFromUi();
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir le dossier du client WotLK",
            InitialDirectory = Directory.Exists(_settings.InstallPath)
                ? _settings.InstallPath
                : LauncherSettings.GetDefaultInstallPath()
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.InstallPath = LauncherSettings.NormalizeInstallPath(dialog.FolderName);
        _settings.ManifestUrl = LauncherSettings.GetDefaultManifestUrl();
        _dependencies.SaveSettings(_settings);
        InstallPathBox.Text = _settings.InstallPath;
        SettingsInstallPathBox.Text = _settings.InstallPath;
        UpdateAddonInstallPathText();

        AppendLog("Dossier client: " + _settings.InstallPath);
        SetInitialGameActionFromDisk();
        await RefreshGameActionAsync();
        if (_isAddonTabActive)
        {
            await RefreshAddonCatalogAsync(reloadCatalog: false);
        }
    }

    private void GameLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
        {
            return;
        }

        SaveSettingsFromUi();
        _isInitializingUi = true;
        SetSettingsLanguageSelection(_settings.GameLocale);
        _isInitializingUi = false;
        if (!GameInstallServices.HasPlayableClient(_settings.InstallPath))
        {
            return;
        }

        if (!EnsureGameDirectoryWritable())
        {
            return;
        }

        try
        {
            var configPath = GameInstallServices.EnsureDefaultClientConfig(_settings.InstallPath, _settings.GameLocale);
            AppendLog($"Langue jeu appliquee au prochain lancement: {GetGameLocaleLabel(_settings.GameLocale)} ({configPath})");
        }
        catch (Exception ex)
        {
            AppendLog("Langue jeu non appliquee: " + ex.Message);
        }
    }

    private void ClientTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is null)
        {
            NavigateTo(LauncherPage.Game);
        }
    }

    private async void AddonsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null)
        {
            return;
        }

        if (!EnsureAuthenticated())
        {
            return;
        }

        NavigateTo(LauncherPage.Addons);
        await RefreshAddonCatalogAsync(reloadCatalog: _addonCatalog is null);
    }

    private async void FriendsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAuthenticated())
        {
            return;
        }

        NavigateTo(LauncherPage.Friends);
        await RefreshFriendsAsync();
    }

    private async void FriendRefreshTimer_Tick(object? sender, EventArgs e)
    {
        _startupObserver.Record(LegacyStartupEvent.FriendRefreshTimerTick);
        if ((_currentPage == LauncherPage.Friends || _currentPage == LauncherPage.Game)
            && _auth.Session is not null)
        {
            await RefreshFriendsAsync(showLoadingStatus: false);
        }
    }

    private async void NewsTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAuthenticated())
        {
            return;
        }

        NavigateTo(LauncherPage.News);
        await RefreshNewsAsync();
    }

    private async void ServerTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAuthenticated())
        {
            return;
        }

        NavigateTo(LauncherPage.Server);
        await RefreshServerStatusAsync();
    }

    private async void AccountTabButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureAuthenticated())
        {
            return;
        }

        NavigateTo(LauncherPage.Account);
        await RefreshAccountDataAsync();
    }

    private void SettingsTabButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateTo(LauncherPage.Settings);
        SyncSettingsUi();
    }

    private async void HomePlayButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteGameActionAsync();
    }

    private void HomeAddonsButton_Click(object sender, RoutedEventArgs e)
    {
        AddonsTabButton_Click(sender, e);
    }

    private async void VerifyClientButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null || !EnsureAuthenticated())
        {
            return;
        }

        SetStatus("Vérification du client...");
        ProgressText.Text = "Analyse en cours";
        await RefreshGameActionAsync();
    }

    private void OpenGameFolderButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettingsFromUi();
        Directory.CreateDirectory(_settings.InstallPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = _settings.InstallPath,
            UseShellExecute = true
        });
    }

    private async void AddonApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
            return;
        }

        if (!EnsureAuthenticated())
        {
            return;
        }

        SaveSettingsFromUi();
        if (!GameInstallServices.HasPlayableClient(_settings.InstallPath))
        {
            ShowToast("Client introuvable", "Installe d'abord le client WotLK avant de gérer ses addons.", ToastKind.Warning);
            return;
        }

        if (!EnsureGameDirectoryWritable())
        {
            return;
        }

        if (_addonCatalog is null)
        {
            await RefreshAddonCatalogAsync(reloadCatalog: true);
            if (_addonCatalog is null)
            {
                return;
            }
        }

        _downloadCancellation = new CancellationTokenSource();
        _isApplyingAddons = true;
        SetBusy(true);
        AddonProgress.Value = 0;
        AddonProgressText.Text = string.Empty;

        try
        {
            if (!await _auth.EnsureFreshAsync())
            {
                ShowLogin();
                return;
            }

            var selection = _allAddonItems.ToDictionary(item => item.Id, item => item.IsSelected, StringComparer.OrdinalIgnoreCase);
            var addonTransferStopwatch = Stopwatch.StartNew();
            string currentTransferAddon = string.Empty;
            long previousTransferBytes = 0;
            var progress = new Progress<AddonTransferProgress>(value =>
            {
                if (!string.Equals(currentTransferAddon, value.AddonName, StringComparison.Ordinal)
                    || value.BytesReceived < previousTransferBytes)
                {
                    currentTransferAddon = value.AddonName;
                    previousTransferBytes = 0;
                    addonTransferStopwatch.Restart();
                }

                previousTransferBytes = value.BytesReceived;
                AddonStatusText.Text = "Téléchargement de " + value.AddonName;
                AddonProgress.Value = value.TotalBytes > 0
                    ? Math.Clamp((double)value.BytesReceived / value.TotalBytes * 100, 0, 100)
                    : 0;
                AddonProgressText.Text = FormatTransferProgress(
                    value.BytesReceived,
                    value.TotalBytes > 0 ? value.TotalBytes : null,
                    addonTransferStopwatch.Elapsed);
            });

            await AddonInstallServices.ApplySelectionAsync(
                _http,
                _addonCatalog,
                _settings.InstallPath,
                selection,
                progress,
                AppendLog,
                _downloadCancellation.Token);

            PopulateAddonItemsFromState();
            AddonProgress.Value = 100;
            AddonProgressText.Text = "Terminé";
            var gameIsRunning = GameInstallServices.IsGameRunning(_settings.InstallPath);
            AddonStatusText.Text = gameIsRunning
                ? "Addons prêts - /reload en jeu"
                : "Sélection appliquée";
            AppendLog(gameIsRunning
                ? "Configuration des addons terminée. Utilise /reload dans le jeu."
                : "Configuration des addons terminée.");
            ShowToast(
                "Addons appliqués",
                gameIsRunning ? "Les changements sont prêts. Utilise /reload dans le jeu." : "Ta sélection d'addons est à jour.",
                ToastKind.Success);
        }
        catch (OperationCanceledException)
        {
            AddonStatusText.Text = "Opération annulée";
            AddonProgressText.Text = string.Empty;
            AppendLog("Configuration des addons annulée.");
        }
        catch (Exception ex)
        {
            AddonStatusText.Text = "Erreur lors de la configuration";
            AddonProgressText.Text = string.Empty;
            AppendLog("Erreur addons: " + ex.Message);
            ShowToast("Erreur addons", ex.Message, ToastKind.Error);
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            _isApplyingAddons = false;
            SetBusy(false);
        }
    }

    private void ShowAddonsTab(bool show)
    {
        NavigateTo(show ? LauncherPage.Addons : LauncherPage.Game);
    }

    private void NavigateTo(LauncherPage page)
    {
        _currentPage = page;
        _isAddonTabActive = page == LauncherPage.Addons;

        HomePanel.Visibility = Visibility.Collapsed;
        GamePanel.Visibility = page == LauncherPage.Game ? Visibility.Visible : Visibility.Collapsed;
        AddonsPanel.Visibility = page == LauncherPage.Addons ? Visibility.Visible : Visibility.Collapsed;
        FriendsPanel.Visibility = page == LauncherPage.Friends ? Visibility.Visible : Visibility.Collapsed;
        NewsPanel.Visibility = page == LauncherPage.News ? Visibility.Visible : Visibility.Collapsed;
        ServerPanel.Visibility = page == LauncherPage.Server ? Visibility.Visible : Visibility.Collapsed;
        AccountPanel.Visibility = page == LauncherPage.Account ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == LauncherPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        ClientTabButton.Tag = page == LauncherPage.Game ? "Active" : null;
        AddonsTabButton.Tag = page == LauncherPage.Addons ? "Active" : null;
        FriendsTabButton.Tag = page == LauncherPage.Friends ? "Active" : null;
        NewsTabButton.Tag = page == LauncherPage.News ? "Active" : null;
        ServerTabButton.Tag = page == LauncherPage.Server ? "Active" : null;
        AccountTabButton.Tag = page == LauncherPage.Account ? "Active" : null;
        SettingsTabButton.Tag = page == LauncherPage.Settings ? "Active" : null;

        if (page == LauncherPage.Addons)
        {
            UpdateAddonInstallPathText();
        }
    }

    private IEnumerable<Button> GetAddonCategoryButtons()
    {
        yield return AddonAllCategoryButton;
        yield return AddonCombatCategoryButton;
        yield return AddonInterfaceCategoryButton;
        yield return AddonQuestsCategoryButton;
        yield return AddonInstancesCategoryButton;
        yield return AddonCollectionsCategoryButton;
        yield return AddonEconomyCategoryButton;
        yield return AddonInventoryCategoryButton;
    }

    private IEnumerable<Button> GetAddonViewButtons()
    {
        yield return AddonInstalledTabButton;
        yield return AddonCatalogTabButton;
        yield return AddonUpdatesTabButton;
    }

    private void UpdateAddonCategoryCounts()
    {
        AddonAllCountText.Text = _allAddonItems.Count.ToString(CultureInfo.InvariantCulture);
        AddonCombatCountText.Text = CountAddons("Combat");
        AddonInterfaceCountText.Text = CountAddons("Interface");
        AddonQuestsCountText.Text = CountAddons("Quêtes");
        AddonInstancesCountText.Text = CountAddons("Instances");
        AddonCollectionsCountText.Text = CountAddons("Collections");
        AddonEconomyCountText.Text = CountAddons("Économie");
        AddonInventoryCountText.Text = CountAddons("Inventaire");
        UpdateAddonViewCounts();
    }

    private void UpdateAddonViewCounts()
    {
        int installed = _allAddonItems.Count(item => item.IsInstalled);
        int updates = _allAddonItems.Count(item => item.NeedsUpdate);
        AddonInstalledCountText.Text = $"({installed})";
        AddonCatalogCountText.Text = $"({_allAddonItems.Count})";
        AddonUpdatesCountText.Text = updates.ToString(CultureInfo.InvariantCulture);
        AddonUpdatesBadge.Visibility = updates > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string CountAddons(string category)
    {
        return _allAddonItems.Count(item =>
                string.Equals(item.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToString(CultureInfo.InvariantCulture);
    }

    private async Task RefreshAddonCatalogAsync(bool reloadCatalog)
    {
        if (_isLoadingAddonCatalog || _downloadCancellation is not null)
        {
            return;
        }

        _isLoadingAddonCatalog = true;
        AddonApplyButton.IsEnabled = false;
        AddonStatusText.Text = "Chargement du catalogue...";
        AddonProgressText.Text = string.Empty;

        try
        {
            if (reloadCatalog || _addonCatalog is null)
            {
                _addonCatalog = await AddonInstallServices.LoadCatalogAsync(
                    _http,
                    new Uri(AddonCatalogUrl, UriKind.Absolute),
                    CancellationToken.None);
            }

            PopulateAddonItemsFromState();
            AddonGameRunningNoticeText.Visibility = GameInstallServices.IsGameRunning(_settings.InstallPath)
                ? Visibility.Visible
                : Visibility.Collapsed;
            AddonStatusText.Text = GameInstallServices.HasPlayableClient(_settings.InstallPath)
                ? $"{_addonItems.Count} addons compatibles disponibles"
                : "Client WotLK requis";
        }
        catch (Exception ex)
        {
            _addonItems.Clear();
            AddonStatusText.Text = "Catalogue indisponible";
            AppendLog("Catalogue addons indisponible: " + ex.Message);
        }
        finally
        {
            _isLoadingAddonCatalog = false;
            AddonApplyButton.IsEnabled = _downloadCancellation is null && _addonCatalog is not null && _addonItems.Count > 0;
        }
    }

    private void PopulateAddonItemsFromState()
    {
        if (_addonCatalog is null)
        {
            return;
        }

        var inspections = AddonInstallServices.Inspect(_addonCatalog, _settings.InstallPath);
        _allAddonItems.Clear();
        foreach (var package in _addonCatalog.Addons
                     .OrderBy(package => package.Name, AddonNameComparer)
                     .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase))
        {
            var item = new AddonSelectionItem(package);
            if (inspections.TryGetValue(package.Id, out var inspection))
            {
                item.ApplyInspection(inspection);
            }

            _allAddonItems.Add(item);
        }

        ApplyAddonCategoryFilter();
    }

    private void AddonCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        _selectedAddonCategory = button.CommandParameter?.ToString() ?? "All";
        foreach (Button categoryButton in GetAddonCategoryButtons())
        {
            categoryButton.Tag = ReferenceEquals(categoryButton, button) ? "Active" : null;
        }

        ApplyAddonCategoryFilter();
    }

    private void AddonViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        _selectedAddonView = button.CommandParameter?.ToString() ?? "Catalog";
        foreach (Button viewButton in GetAddonViewButtons())
        {
            viewButton.Tag = ReferenceEquals(viewButton, button) ? "Active" : null;
        }

        ApplyAddonCategoryFilter();
    }

    private void AddonSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (AddonSearchPlaceholder is not null)
        {
            AddonSearchPlaceholder.Visibility = string.IsNullOrEmpty(AddonSearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        ApplyAddonCategoryFilter();
    }

    private void AddonSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
        {
            return;
        }

        if (AddonSortComboBox.SelectedItem is ComboBoxItem item)
        {
            _selectedAddonSort = item.Tag?.ToString() ?? "Name";
            ApplyAddonCategoryFilter();
        }
    }

    private void ApplyAddonCategoryFilter()
    {
        if (_addonItems is null)
        {
            return;
        }

        string search = AddonSearchBox?.Text.Trim() ?? string.Empty;
        IEnumerable<AddonSelectionItem> filtered = _allAddonItems.Where(item =>
        {
            bool viewMatches = _selectedAddonView switch
            {
                "Installed" => item.IsInstalled,
                "Updates" => item.NeedsUpdate,
                _ => true
            };
            bool categoryMatches = _selectedAddonCategory == "All"
                || string.Equals(item.Category, _selectedAddonCategory, StringComparison.OrdinalIgnoreCase);
            bool searchMatches = string.IsNullOrWhiteSpace(search)
                || item.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Description.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || item.Category.Contains(search, StringComparison.CurrentCultureIgnoreCase);
            return viewMatches && categoryMatches && searchMatches;
        });

        filtered = _selectedAddonSort switch
        {
            "Category" => filtered.OrderBy(item => item.Category, AddonNameComparer)
                .ThenBy(item => item.Name, AddonNameComparer),
            "Status" => filtered.OrderBy(item => item.StatusText, AddonNameComparer)
                .ThenBy(item => item.Name, AddonNameComparer),
            _ => filtered.OrderBy(item => item.Name, AddonNameComparer)
        };

        _addonItems.Clear();
        foreach (AddonSelectionItem item in filtered)
        {
            _addonItems.Add(item);
        }

        UpdateAddonCategoryCounts();
        (AddonLibraryTitleText.Text, AddonLibrarySummaryText.Text) = _selectedAddonView switch
        {
            "Installed" => ("Mes addons installés", "Gère les addons déjà présents sur ce client."),
            "Updates" => ("Mises à jour disponibles", "Répare ou actualise les addons qui en ont besoin."),
            _ => ("Catalogue compatible", "Choisis les addons à installer ou retirer.")
        };
        AddonStatusText.Text = _allAddonItems.Count == 0
            ? "Catalogue prêt"
            : $"{_addonItems.Count} addon(s) affiché(s) sur {_allAddonItems.Count}";
    }

    private void UpdateAddonInstallPathText()
    {
        AddonInstallPathText.Text = AddonInstallServices.GetAddonsDirectory(_settings.InstallPath);
    }

    private async void RefreshFriendsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshFriendsAsync();
    }

    private async void SendFriendRequestButton_Click(object sender, RoutedEventArgs e)
    {
        await SendFriendRequestAsync();
    }

    private async void FriendUsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendFriendRequestAsync();
        }
    }

    private async Task SendFriendRequestAsync()
    {
        string username = FriendUsernameBox.Text.Trim();
        if (username.Length is < 2 or > 32)
        {
            FriendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0x8A, 0x91));
            FriendStatusText.Text = "Saisis le nom d'utilisateur Atlas exact.";
            return;
        }

        SendFriendRequestButton.IsEnabled = false;
        try
        {
            string message = await _auth.SendFriendRequestAsync(username);
            FriendUsernameBox.Clear();
            FriendStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            FriendStatusText.Text = message;
            AppendLog(message);
            await RefreshFriendsAsync(showLoadingStatus: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            FriendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0x8A, 0x91));
            FriendStatusText.Text = ex.Message;
        }
        finally
        {
            SendFriendRequestButton.IsEnabled = true;
        }
    }

    private async void AcceptFriendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not uint accountId)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            await _auth.AcceptFriendAsync(accountId);
            FriendStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            FriendStatusText.Text = "Demande d'ami acceptée.";
            await RefreshFriendsAsync(showLoadingStatus: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            FriendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0x8A, 0x91));
            FriendStatusText.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void RemoveFriendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.CommandParameter is not uint accountId
            || button.DataContext is not LauncherFriend friend)
        {
            return;
        }

        if (string.Equals(friend.Relationship, "accepted", StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                this,
                $"Retirer {friend.Username} de tes amis Atlas ?",
                "Retirer un ami",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }
        }

        button.IsEnabled = false;
        try
        {
            await _auth.RemoveFriendAsync(accountId);
            FriendStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            FriendStatusText.Text = string.Equals(friend.Relationship, "accepted", StringComparison.OrdinalIgnoreCase)
                ? $"{friend.Username} a été retiré de tes amis."
                : "Demande d'ami supprimée.";
            await RefreshFriendsAsync(showLoadingStatus: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            FriendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0x8A, 0x91));
            FriendStatusText.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async Task RefreshFriendsAsync(bool showLoadingStatus = true)
    {
        if (_isLoadingFriends || _auth.Session is null)
        {
            return;
        }

        _isLoadingFriends = true;
        if (showLoadingStatus)
        {
            FriendStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            FriendStatusText.Text = "Actualisation de la liste d'amis...";
        }

        try
        {
            IReadOnlyList<LauncherFriend> friends = await _auth.GetFriendsAsync();
            _friendItems.Clear();
            _incomingFriendItems.Clear();
            _outgoingFriendItems.Clear();

            foreach (LauncherFriend friend in friends)
            {
                switch (friend.Relationship.ToLowerInvariant())
                {
                    case "incoming":
                        _incomingFriendItems.Add(friend);
                        break;
                    case "outgoing":
                        _outgoingFriendItems.Add(friend);
                        break;
                    default:
                        _friendItems.Add(friend);
                        break;
                }
            }

            IncomingFriendSection.Visibility = _incomingFriendItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            OutgoingFriendSection.Visibility = _outgoingFriendItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            FriendsEmptyText.Visibility = _friendItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            int onlineCount = _friendItems.Count(friend => friend.Online);
            GameFriendCountText.Text = _friendItems.Count == 0
                ? "Aucun ami Atlas"
                : $"{onlineCount} en jeu sur {_friendItems.Count}";
            FriendStatusText.Foreground = (Brush)FindResource("TextSecondaryBrush");
            FriendStatusText.Text = _friendItems.Count == 0
                ? "Aucun ami Atlas pour le moment."
                : $"{_friendItems.Count} ami(s) · {onlineCount} en jeu";
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            FriendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0x8A, 0x91));
            FriendStatusText.Text = "Liste d'amis indisponible : " + ex.Message;
            AppendLog("Liste d'amis indisponible: " + ex.Message);
        }
        finally
        {
            _isLoadingFriends = false;
        }
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await LoginAsync();
    }

    private async void LoginPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await LoginAsync();
        }
    }

    private async Task LoginAsync()
    {
        LoginErrorText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(LoginUsernameBox.Text)
            || string.IsNullOrEmpty(LoginPasswordBox.Password))
        {
            LoginErrorText.Text = "Renseigne ton nom d'utilisateur et ton mot de passe.";
            return;
        }

        SetAuthBusy(true);
        try
        {
            await _auth.LoginAsync(
                LoginUsernameBox.Text.Trim(),
                LoginPasswordBox.Password);
            LoginPasswordBox.Clear();
            CompleteAuthentication();
            AppendLog($"Connecté au launcher en tant que {_auth.Session!.Profile.Username}.");
            await RefreshGameActionAsync();
        }
        catch (OperationCanceledException)
        {
            LoginErrorText.Text = "Atlas met trop de temps à répondre. Réessaie dans quelques secondes.";
            AppendLog("Connexion au launcher expirée.");
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            LoginErrorText.Text = ex is HttpRequestException
                ? "Impossible de joindre Atlas. Vérifie ta connexion puis réessaie."
                : ex.Message;
        }
        catch (Exception ex)
        {
            LoginErrorText.Text = "Une erreur inattendue est survenue. Le launcher peut rester ouvert.";
            AppendLog("Erreur de connexion inattendue: " + ex.Message);
        }
        finally
        {
            SetAuthBusy(false);
        }
    }

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        RegisterErrorText.Text = string.Empty;
        if (RegisterPasswordBox.Password != RegisterPasswordConfirmBox.Password)
        {
            RegisterErrorText.Text = "Les deux mots de passe ne correspondent pas.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RegisterUsernameBox.Text)
            || string.IsNullOrWhiteSpace(RegisterEmailBox.Text)
            || string.IsNullOrEmpty(RegisterPasswordBox.Password))
        {
            RegisterErrorText.Text = "Tous les champs sont obligatoires.";
            return;
        }

        SetAuthBusy(true);
        try
        {
            await _auth.RegisterAsync(
                RegisterUsernameBox.Text.Trim(),
                RegisterEmailBox.Text.Trim(),
                RegisterPasswordBox.Password);
            RegisterPasswordBox.Clear();
            RegisterPasswordConfirmBox.Clear();
            CompleteAuthentication();
            AppendLog($"Compte {_auth.Session!.Profile.Username} créé et connecté.");
            await RefreshGameActionAsync();
        }
        catch (OperationCanceledException)
        {
            RegisterErrorText.Text = "Atlas met trop de temps à répondre. Réessaie dans quelques secondes.";
            AppendLog("Création du compte expirée.");
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            RegisterErrorText.Text = ex is HttpRequestException
                ? "Impossible de joindre Atlas. Vérifie ta connexion puis réessaie."
                : ex.Message;
        }
        catch (Exception ex)
        {
            RegisterErrorText.Text = "Une erreur inattendue est survenue. Le launcher peut rester ouvert.";
            AppendLog("Erreur de création de compte inattendue: " + ex.Message);
        }
        finally
        {
            SetAuthBusy(false);
        }
    }

    private void ShowRegisterButton_Click(object sender, RoutedEventArgs e)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        RegisterPanel.Visibility = Visibility.Visible;
        RegisterUsernameBox.Text = LoginUsernameBox.Text.Trim();
        RegisterErrorText.Text = string.Empty;
        RegisterUsernameBox.Focus();
    }

    private void ShowLoginButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLogin();
    }

    private async void ProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_auth.Session is null)
        {
            ShowLogin();
            return;
        }

        UpdateProfileUi(_auth.Session.Profile);
        NavigateTo(LauncherPage.Account);
        await RefreshAccountDataAsync();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _auth.LogoutAsync();
        ProfileButton.Visibility = Visibility.Collapsed;
        NavigateTo(LauncherPage.Game);
        ShowLogin();
        AppendLog("Déconnecté du launcher.");
    }

    private void ShowChangeEmailButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeEmailBox.Text = _auth.Session?.Profile.Email ?? string.Empty;
        EmailWarningBorder.Visibility = Visibility.Collapsed;
        ChangeEmailPanel.Visibility = Visibility.Visible;
        ChangeEmailBox.Focus();
    }

    private void CancelChangeEmailButton_Click(object sender, RoutedEventArgs e)
    {
        ChangeEmailPanel.Visibility = Visibility.Collapsed;
        EmailWarningBorder.Visibility = Visibility.Visible;
    }

    private async void SaveEmailButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EmailChangeResult result =
                await _auth.ChangeEmailAsync(ChangeEmailBox.Text.Trim());
            UpdateProfileUi(result.Profile);
            ChangeEmailPanel.Visibility = Visibility.Collapsed;
            AppendLog(result.VerificationMessage);
            ShowToast(
                "Adresse e-mail",
                result.VerificationMessage,
                result.VerificationEmailSent || result.Profile.EmailVerified ? ToastKind.Info : ToastKind.Warning);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            ShowToast("Adresse e-mail", ex.Message, ToastKind.Error);
        }
    }

    private async void ResendVerificationButton_Click(object sender, RoutedEventArgs e)
    {
        ResendVerificationButton.IsEnabled = false;
        try
        {
            string message = await _auth.ResendVerificationAsync();
            LauncherProfile profile = await _auth.RefreshProfileAsync();
            UpdateProfileUi(profile);
            AppendLog(message);
            ShowToast(
                "Validation de l'e-mail",
                message + " Pense à vérifier le dossier des courriers indésirables.",
                ToastKind.Info);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            ShowToast("Validation de l'e-mail", ex.Message, ToastKind.Error);
        }
        finally
        {
            ResendVerificationButton.IsEnabled = true;
        }
    }

    private async void AvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string avatarKey)
        {
            return;
        }

        try
        {
            LauncherProfile profile = await _auth.ChangeAvatarAsync(avatarKey);
            UpdateProfileUi(profile);
            AppendLog("Avatar du compte mis à jour.");
            ShowToast("Profil mis à jour", "Ton avatar Atlas a bien été modifié.", ToastKind.Success);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            ShowToast("Avatar", ex.Message, ToastKind.Error);
        }
    }

    private async void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        PasswordChangeStatusText.Text = string.Empty;
        if (string.IsNullOrEmpty(CurrentPasswordBox.Password)
            || string.IsNullOrEmpty(NewPasswordBox.Password))
        {
            PasswordChangeStatusText.Text = "Renseigne le mot de passe actuel et le nouveau.";
            return;
        }

        if (!string.Equals(NewPasswordBox.Password, ConfirmNewPasswordBox.Password, StringComparison.Ordinal))
        {
            PasswordChangeStatusText.Text = "La confirmation ne correspond pas.";
            return;
        }

        ChangePasswordButton.IsEnabled = false;
        try
        {
            await _auth.ChangePasswordAsync(CurrentPasswordBox.Password, NewPasswordBox.Password);
            CurrentPasswordBox.Clear();
            NewPasswordBox.Clear();
            ConfirmNewPasswordBox.Clear();
            PasswordChangeStatusText.Foreground = (Brush)FindResource("SuccessBrush");
            PasswordChangeStatusText.Text = "Mot de passe modifié.";
            AppendLog("Mot de passe du compte mis à jour.");
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            PasswordChangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF1, 0x8A, 0x91));
            PasswordChangeStatusText.Text = ex.Message;
        }
        finally
        {
            ChangePasswordButton.IsEnabled = true;
        }
    }

    private async void RefreshSessionsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAccountDataAsync();
    }

    private async void RevokeSessionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.CommandParameter is not string sessionId)
        {
            return;
        }

        try
        {
            await _auth.RevokeSessionAsync(sessionId);
            AppendLog("Session distante révoquée.");
            ShowToast("Session révoquée", "L'appareil a été déconnecté de ton compte.", ToastKind.Success);
            await RefreshAccountDataAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            ShowToast("Sessions", ex.Message, ToastKind.Error);
        }
    }

    private async Task RefreshAccountDataAsync()
    {
        if (_isLoadingAccountData || _auth.Session is null)
        {
            return;
        }

        _isLoadingAccountData = true;
        try
        {
            LauncherProfile profile = await _auth.RefreshProfileAsync();
            UpdateProfileUi(profile);
            IReadOnlyList<LauncherDeviceSession> sessions = await _auth.GetSessionsAsync();
            _sessionItems.Clear();
            foreach (LauncherDeviceSession session in sessions)
            {
                _sessionItems.Add(session);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            AppendLog("Sessions du compte indisponibles: " + ex.Message);
        }
        finally
        {
            _isLoadingAccountData = false;
        }
    }

    private async void RefreshNewsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshNewsAsync();
    }

    private async Task RefreshNewsAsync()
    {
        if (_auth.Session is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<LauncherNews> news = await _auth.GetNewsAsync();
            _newsItems.Clear();
            foreach (LauncherNews item in news.OrderByDescending(item => item.PublishedAt))
            {
                _newsItems.Add(item);
            }

            LauncherNews? latest = _newsItems.FirstOrDefault();
            HomeNewsTitleText.Text = latest?.Title ?? "Aucun patch note";
            HomeNewsSummaryText.Text = latest?.Summary ?? string.Empty;
            GamePatchTitleText.Text = latest?.Title ?? "Aucun patch note";
            GamePatchSummaryText.Text = latest?.Summary ?? "Les prochaines modifications du launcher et du royaume apparaîtront ici.";
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            HomeNewsTitleText.Text = "Patch notes indisponibles";
            HomeNewsSummaryText.Text = "Atlas n’a pas pu répondre pour le moment.";
            GamePatchTitleText.Text = "Patch notes indisponibles";
            GamePatchSummaryText.Text = "Atlas n’a pas pu répondre pour le moment.";
            AppendLog("Patch notes indisponibles: " + ex.Message);
        }
    }

    private async void RefreshServerButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshServerStatusAsync();
    }

    private async Task RefreshServerStatusAsync()
    {
        if (_isLoadingServerStatus || _auth.Session is null)
        {
            return;
        }

        _isLoadingServerStatus = true;
        RefreshServerButton.IsEnabled = false;
        try
        {
            LauncherServerStatus status = await _auth.GetStatusAsync();
            bool online = status.Api
                && status.Authentication
                && status.RealmGateway
                && status.WorldGateway
                && status.WorldServer;

            ServerRealmNameText.Text = status.Realm;
            ServerGlobalStatusText.Text = online ? "Tous les services sont en ligne" : "Service dégradé";
            ServerGlobalStatusText.Foreground = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            ServerCheckedText.Text = $"Dernière vérification à {status.CheckedAt.ToLocalTime():HH:mm:ss}";
            HomeServerStatusText.Text = online ? "En ligne" : "Dégradé";
            HomeServerStatusText.Foreground = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            HomeServerDot.Fill = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            HomeServerCheckedText.Text = $"Vérifié à {status.CheckedAt.ToLocalTime():HH:mm:ss}";
            HeaderServerStatusText.Text = online ? "Serveur en ligne" : "Service dégradé";
            HeaderServerStatusText.Foreground = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            HeaderServerDot.Fill = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            GameServerStatusText.Text = online ? "En ligne" : "Service dégradé";
            GameServerStatusText.Foreground = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            GameServerDot.Fill = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
            GameServerCheckedText.Text = $"Vérifié à {status.CheckedAt.ToLocalTime():HH:mm:ss}";

            SetServiceStatus(ApiStatusText, status.Api);
            SetServiceStatus(AuthenticationStatusText, status.Authentication);
            SetServiceStatus(RealmGatewayStatusText, status.RealmGateway);
            SetServiceStatus(WorldGatewayStatusText, status.WorldGateway);
            SetServiceStatus(WorldServerStatusText, status.WorldServer);
        }
        catch (Exception ex) when (ex is HttpRequestException or LauncherAuthException)
        {
            ServerGlobalStatusText.Text = "Statut indisponible";
            HomeServerStatusText.Text = "Indisponible";
            HomeServerDot.Fill = (Brush)FindResource("TextMutedBrush");
            HeaderServerStatusText.Text = "Serveur indisponible";
            HeaderServerStatusText.Foreground = (Brush)FindResource("TextMutedBrush");
            HeaderServerDot.Fill = (Brush)FindResource("TextMutedBrush");
            GameServerStatusText.Text = "Indisponible";
            GameServerStatusText.Foreground = (Brush)FindResource("TextMutedBrush");
            GameServerDot.Fill = (Brush)FindResource("TextMutedBrush");
            AppendLog("Statut Atlas indisponible: " + ex.Message);
        }
        finally
        {
            RefreshServerButton.IsEnabled = true;
            _isLoadingServerStatus = false;
        }
    }

    private async Task RefreshDashboardAsync()
    {
        await Task.WhenAll(RefreshNewsAsync(), RefreshServerStatusAsync(), RefreshFriendsAsync(showLoadingStatus: false));
        HomeClientStatusText.Text = GetGameActionLabel(_gameAction) switch
        {
            "JOUER" => "Prêt à jouer",
            "METTRE A JOUR" => "Mise à jour disponible",
            _ => "Installation requise"
        };
        HomeAddonStatusText.Text = _addonCatalog is null
            ? "Catalogue Atlas disponible"
            : $"{_addonCatalog.Addons.Count} addons disponibles";
    }

    private void SetServiceStatus(TextBlock target, bool online)
    {
        target.Text = online ? "En ligne" : "Hors ligne";
        target.Foreground = (Brush)FindResource(online ? "SuccessBrush" : "GoldHoverBrush");
    }

    private void ApplyAvatarTheme(string? avatarKey)
    {
        AvatarGoldButton.BorderThickness = new Thickness(1);
        AvatarIceButton.BorderThickness = new Thickness(1);
        AvatarEmeraldButton.BorderThickness = new Thickness(1);
        AvatarCrimsonButton.BorderThickness = new Thickness(1);

        Button? selected = avatarKey switch
        {
            "gold" => AvatarGoldButton,
            "ice" => AvatarIceButton,
            "emerald" => AvatarEmeraldButton,
            "crimson" => AvatarCrimsonButton,
            _ => null
        };
        if (selected is not null)
        {
            selected.BorderThickness = new Thickness(3);
            ProfileButton.Background = selected.Background;
            ProfileButton.Foreground = Brushes.Black;
        }
        else
        {
            ProfileButton.ClearValue(BackgroundProperty);
            ProfileButton.ClearValue(ForegroundProperty);
        }
    }

    private bool EnsureAuthenticated()
    {
        if (_auth.IsAuthenticated)
        {
            return true;
        }

        ShowLogin();
        LoginErrorText.Text = "Connecte-toi ou crée un compte pour continuer.";
        return false;
    }

    private void CompleteAuthentication()
    {
        LauncherProfile profile = _auth.Session!.Profile;
        AuthOverlay.Visibility = Visibility.Collapsed;
        ProfileButton.Visibility = Visibility.Visible;
        UpdateProfileUi(profile);
        NavigateTo(LauncherPage.Game);
        _ = RefreshDashboardAsync();
    }

    private void ShowLogin()
    {
        AuthOverlay.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Visible;
        RegisterPanel.Visibility = Visibility.Collapsed;
        LoginUsernameBox.Focus();
    }

    private void UpdateProfileUi(LauncherProfile profile)
    {
        ProfileUsernameText.Text = profile.Username;
        ProfileEmailText.Text = profile.Email;
        AccountEmailValueText.Text = profile.Email;
        ProfileCompletionText.Text = $"{profile.Completion}%";
        ProfileCompletionProgress.Value = profile.Completion;
        ProfileInitialText.Text = string.IsNullOrWhiteSpace(profile.Username)
            ? "?"
            : profile.Username[..1].ToUpperInvariant();
        EmailWarningBorder.Visibility = profile.EmailVerified
            ? Visibility.Collapsed
            : Visibility.Visible;
        ChangeEmailPanel.Visibility = Visibility.Collapsed;
        ProfileEmailCompletionText.Text = profile.EmailVerified ? "Terminé" : "À valider";
        ProfileAvatarCompletionText.Text = string.IsNullOrWhiteSpace(profile.AvatarKey) ? "À choisir" : "Terminé";
        ProfileTwoFactorCompletionText.Text = profile.TwoFactorEnabled ? "Terminé" : "Recommandé";
        ProfileRecoveryCompletionText.Text = profile.RecoveryCodesGenerated ? "Terminé" : "Recommandé";
        TwoFactorStatusText.Text = profile.TwoFactorEnabled ? "Active" : "Non configurée";
        RecoveryCodesStatusText.Text = profile.RecoveryCodesGenerated ? "Générés" : "Non générés";
        ApplyAvatarTheme(profile.AvatarKey);
        HomeAccountStatusText.Text = profile.EmailVerified
            ? $"Profil complété à {profile.Completion}%"
            : $"Profil à {profile.Completion}% · e-mail à valider";
    }

    private void SetAuthBusy(bool busy)
    {
        LoginButton.IsEnabled = !busy;
        RegisterButton.IsEnabled = !busy;
        LoginButton.Content = busy ? "CONNEXION..." : "SE CONNECTER";
        RegisterButton.Content = busy ? "CRÉATION..." : "CRÉER MON COMPTE";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToastCloseButton_Click(object sender, RoutedEventArgs e)
    {
        HideToast();
    }

    private void ToastTimer_Tick(object? sender, EventArgs e)
    {
        HideToast();
    }

    private void ShowToast(string title, string message, ToastKind kind)
    {
        string brushKey = kind switch
        {
            ToastKind.Success => "SuccessBrush",
            ToastKind.Warning => "GoldHoverBrush",
            ToastKind.Error => "DangerBrush",
            _ => "IceBrush"
        };

        ToastTitleText.Text = title;
        ToastMessageText.Text = message;
        ToastAccentBorder.Background = (Brush)FindResource(brushKey);
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastBorder.Visibility = Visibility.Collapsed;
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private async Task PlayGameAsync()
    {
        var wowPath = GameInstallServices.GetGameExecutablePath(_settings.InstallPath);
        var gameLauncherPath = GameInstallServices.GetGameLauncherPath(_settings.InstallPath);
        if (!GameInstallServices.HasPlayableClient(_settings.InstallPath))
        {
            ShowToast("Client introuvable", "Le client Classic ou le lanceur Atlas est introuvable. Installe ou mets à jour le client d'abord.", ToastKind.Warning);
            SetGameAction(GameAction.Install);
            return;
        }

        if (GameInstallServices.IsGameRunning(_settings.InstallPath))
        {
            AppendLog("Le jeu est deja lance.");
            SetStatus("Jeu en cours.");
            return;
        }

        GameInstallServices.EnsureDefaultClientConfig(_settings.InstallPath, _settings.GameLocale);

        GameTicket ticket;
        try
        {
            SetStatus("Préparation de la connexion...");
            if (!await _auth.EnsureFreshAsync())
            {
                ShowLogin();
                return;
            }
            ticket = await _auth.CreateGameTicketAsync();
            GameSingleSignOn.Write(ticket, _settings.GameLocale);
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or LauncherAuthException
                or CryptographicException)
        {
            AppendLog("Connexion automatique impossible: " + ex.Message);
            SetStatus("Connexion requise.");
            ShowToast("Connexion automatique", ex.Message, ToastKind.Error);
            if (ex is LauncherAuthException)
            {
                ShowLogin();
            }
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = gameLauncherPath,
            WorkingDirectory = _settings.InstallPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add("Classic");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(GameInstallServices.GetClassicDirectoryPath(_settings.InstallPath));
        startInfo.ArgumentList.Add("--portal");
        startInfo.ArgumentList.Add(GameInstallServices.PortalAddress);
        startInfo.ArgumentList.Add("--skipcertcheck");
        startInfo.ArgumentList.Add("-launcherlogin");
        startInfo.ArgumentList.Add("-uid");
        startInfo.ArgumentList.Add("wow_classic");
        Process.Start(startInfo);

        AppendLog($"Jeu lancé sur Atlas avec connexion automatique pour {ticket.Username}: {wowPath}");
        if (_settings.CloseLauncherOnGameStart)
        {
            Close();
        }
    }

    private async void LauncherUpdateTimer_Tick(object? sender, EventArgs e)
    {
        _startupObserver.Record(LegacyStartupEvent.LauncherUpdateTimerTick);
        if (!_settings.AutomaticLauncherUpdates)
        {
            return;
        }

        await CheckLauncherUpdateAsync();
        if (_auth.Session is not null && await _auth.EnsureFreshAsync())
        {
            await RefreshGameActionAsync(silentWhenUpToDate: true);
        }
    }

    private async Task CheckLauncherUpdateAsync()
    {
        if (_isCheckingLauncherUpdate)
        {
            return;
        }

        _isCheckingLauncherUpdate = true;
        try
        {
            var manifest = await LoadLauncherUpdateManifestAsync(CancellationToken.None);
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            {
                return;
            }

            var currentHash = await ComputeSha256Async(currentExe, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(manifest.Sha256) &&
                !string.Equals(currentHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase) &&
                IsLauncherManifestVersionEligible(manifest.Version))
            {
                _launcherUpdate = manifest;
                LauncherSelfUpdateButton.Visibility = Visibility.Visible;
                LauncherSelfUpdateButton.ToolTip = string.IsNullOrWhiteSpace(manifest.Version)
                    ? "Une mise a jour du launcher est disponible."
                    : "Mise a jour launcher disponible: " + manifest.Version;

                if (!string.Equals(_announcedLauncherUpdateHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _announcedLauncherUpdateHash = manifest.Sha256;
                    AppendLog(string.IsNullOrWhiteSpace(manifest.Version)
                        ? "Mise a jour launcher disponible."
                        : "Mise a jour launcher disponible: " + manifest.Version);
                }
            }
            else
            {
                LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;
                _launcherUpdate = null;
                _announcedLauncherUpdateHash = null;
            }
        }
        catch (Exception ex)
        {
            if (_launcherUpdate is null)
            {
                LauncherSelfUpdateButton.Visibility = Visibility.Collapsed;
            }

            if (string.IsNullOrWhiteSpace(_announcedLauncherUpdateHash))
            {
                AppendLog("Verification launcher ignoree: " + ex.Message);
            }
        }
        finally
        {
            _isCheckingLauncherUpdate = false;
        }
    }

    private static bool IsLauncherManifestVersionEligible(string manifestVersion)
    {
        if (!Version.TryParse(manifestVersion, out var remoteVersion))
        {
            return true;
        }

        var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        return NormalizeVersion(remoteVersion) >= NormalizeVersion(currentVersion);
    }

    private static Version NormalizeVersion(Version version)
    {
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));
    }

    private async Task UpdateLauncherAsync(LauncherUpdateManifest manifest, CancellationToken cancellationToken)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            throw new InvalidOperationException("Impossible de retrouver l'exécutable du launcher actuel.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Url))
        {
            throw new InvalidOperationException("Le manifeste de mise à jour launcher ne contient pas d'URL.");
        }

        var updateDirectory = Path.Combine(Path.GetTempPath(), "WotLKLauncherUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(updateDirectory);

        var downloadedExe = Path.Combine(updateDirectory, Path.GetFileName(currentExe));
        var scriptPath = Path.Combine(updateDirectory, "apply-launcher-update.ps1");
        var updateUri = BuildLauncherUpdateUri(manifest.Url);

        MainProgress.Value = 0;
        ProgressText.Text = "";
        SetStatus("Mise à jour du launcher...");
        AppendLog("Téléchargement de la mise à jour launcher...");

        await DownloadLauncherBinaryAsync(updateUri, downloadedExe, manifest.Size, cancellationToken);

        if (manifest.Size > 0 && new FileInfo(downloadedExe).Length != manifest.Size)
        {
            File.Delete(downloadedExe);
            throw new InvalidOperationException("Taille invalide pour la mise à jour launcher.");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sha256))
        {
            var downloadedHash = await ComputeSha256Async(downloadedExe, cancellationToken);
            if (!string.Equals(downloadedHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(downloadedExe);
                throw new InvalidOperationException("Hash invalide pour la mise à jour launcher.");
            }
        }

        WriteLauncherUpdateScript(scriptPath, currentExe, downloadedExe, Process.GetCurrentProcess().Id);
        AppendLog("Application de la mise à jour. Une validation administrateur peut être demandée.");

        StartElevatedScript(scriptPath);
        System.Windows.Application.Current.Shutdown();
    }

    private void SetInitialGameActionFromDisk()
    {
        _settings.ManifestUrl = LauncherSettings.GetDefaultManifestUrl();
        InstallPathBox.Text = _settings.InstallPath;

        GameClientLocalState localState = _gameClientStateReader.Read(_settings);
        SetGameAction(localState.Action);
        MainProgress.Value = localState.IsPlayable ? 100 : 0;
        ProgressText.Text = localState.IsPlayable ? "Client à jour" : string.Empty;
    }

    private async Task RefreshGameActionAsync(bool silentWhenUpToDate = false)
    {
        if (_downloadCancellation is not null || _isRefreshingGameAction)
        {
            return;
        }

        _isRefreshingGameAction = true;
        try
        {
            _settings.ManifestUrl = LauncherSettings.GetDefaultManifestUrl();
            InstallPathBox.Text = _settings.InstallPath;

            if (!GameInstallServices.HasPlayableClient(_settings.InstallPath))
            {
                SetGameAction(GameAction.Install);
                if (!silentWhenUpToDate)
                {
                    SetStatus("Pret.");
                    MainProgress.Value = 0;
                    ProgressText.Text = string.Empty;
                }
                return;
            }

            if (GameInstallServices.IsGameRunning(_settings.InstallPath))
            {
                SetGameAction(GameAction.Play);
                SetStatus("Jeu en cours.");
                MainProgress.Value = 100;
                ProgressText.Text = "Jeu en cours";
                return;
            }

            if (!silentWhenUpToDate || _gameAction != GameAction.Update)
            {
                SetGameAction(GameAction.Play);
            }
            if (!silentWhenUpToDate)
            {
                SetStatus("Comparaison du manifeste...");
            }
            var manifest = await LoadManifestAsync(CancellationToken.None);
            if (manifest.Files.Count == 0)
            {
                SetGameAction(GameAction.Play);
                if (!silentWhenUpToDate)
                {
                    SetStatus("Pret.");
                }
                return;
            }

            var missingOrChanged = await FindMissingOrChangedFilesForManifestAsync(manifest, updateProgress: false, CancellationToken.None);
            var removedFiles = FindRemovedFilesForManifest(manifest);
            var changeCount = missingOrChanged.Count + removedFiles.Count;
            if (changeCount == 0)
            {
                SaveInstalledManifestHistory(manifest);
                _announcedGameUpdateVersion = null;
                SetGameAction(GameAction.Play);
                if (!silentWhenUpToDate)
                {
                    RegisterGameApplication(manifest.Version);
                    SetStatus("Client a jour.");
                    MainProgress.Value = 100;
                    ProgressText.Text = "Client à jour";
                }
                else if (_gameAction == GameAction.Play)
                {
                    MainProgress.Value = 100;
                    ProgressText.Text = "Client à jour";
                }
            }
            else
            {
                SetGameAction(GameAction.Update);
                SetStatus("Mise a jour disponible.");
                ProgressText.Text = changeCount + " fichier(s)";

                var gameUpdateKey = string.IsNullOrWhiteSpace(manifest.Version)
                    ? changeCount.ToString(CultureInfo.InvariantCulture)
                    : manifest.Version;
                if (!string.Equals(_announcedGameUpdateVersion, gameUpdateKey, StringComparison.OrdinalIgnoreCase))
                {
                    _announcedGameUpdateVersion = gameUpdateKey;
                    AppendLog(string.IsNullOrWhiteSpace(manifest.Version)
                        ? $"Mise a jour jeu disponible: {changeCount} fichier(s)."
                        : $"Mise a jour jeu disponible: {manifest.Version} ({changeCount} fichier(s)).");
                }
            }
        }
        catch (Exception ex)
        {
            SetGameAction(GameInstallServices.HasPlayableClient(_settings.InstallPath) ? GameAction.Play : GameAction.Install);
            if (!silentWhenUpToDate)
            {
                SetStatus("Pret.");
                ProgressText.Text = string.Empty;
                AppendLog("Analyse client ignoree: " + ex.Message);
            }
        }
        finally
        {
            _isRefreshingGameAction = false;
        }
    }

    private async Task<List<LauncherFile>> FindMissingOrChangedFilesForManifestAsync(LauncherManifest manifest, bool updateProgress, CancellationToken cancellationToken)
    {
        var fromHistory = FindMissingOrChangedFilesFromManifestHistory(manifest);
        if (fromHistory is not null)
        {
            if (updateProgress)
            {
                ProgressText.Text = fromHistory.Count == 0 ? "Historique OK" : fromHistory.Count + " fichier(s)";
            }

            return fromHistory;
        }

        return await FindMissingOrChangedFilesAsync(manifest, updateProgress, cancellationToken);
    }

    private List<LauncherFile>? FindMissingOrChangedFilesFromManifestHistory(LauncherManifest manifest)
    {
        var cachedManifest = LoadInstalledManifestHistory();
        if (cachedManifest is not null && cachedManifest.Files.Count > 0)
        {
            return CompareManifestFiles(manifest, cachedManifest);
        }

        var installedVersion = _gameClientStateReader.ReadInstalledVersion(_settings.InstallPath);
        if (!string.IsNullOrWhiteSpace(manifest.Version) &&
            string.Equals(installedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase) &&
            GameInstallServices.HasPlayableClient(_settings.InstallPath))
        {
            SaveInstalledManifestHistory(manifest);
            return [];
        }

        return null;
    }

    private static List<LauncherFile> CompareManifestFiles(LauncherManifest remoteManifest, LauncherManifest installedManifest)
    {
        var installedFiles = installedManifest.Files
            .GroupBy(file => NormalizeManifestPath(file.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var missingOrChanged = new List<LauncherFile>();
        foreach (var remoteFile in remoteManifest.Files)
        {
            var key = NormalizeManifestPath(remoteFile.Path);
            if (!installedFiles.TryGetValue(key, out var installedFile) ||
                installedFile.Size != remoteFile.Size ||
                !string.Equals(installedFile.Sha256, remoteFile.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                missingOrChanged.Add(remoteFile);
            }
        }

        return missingOrChanged;
    }

    private List<string> FindRemovedFilesForManifest(LauncherManifest manifest)
    {
        var remotePaths = manifest.Files
            .Select(file => NormalizeManifestPath(file.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cachedManifest = LoadInstalledManifestHistory();
        if (cachedManifest is not null && cachedManifest.Files.Count > 0)
        {
            foreach (var cachedFile in cachedManifest.Files)
            {
                var key = NormalizeManifestPath(cachedFile.Path);
                if (!remotePaths.Contains(key))
                {
                    removedPaths.Add(cachedFile.Path);
                }
            }
        }

        AddRetiredDirectoryFilesIfAbsent(remotePaths, removedPaths, "Interface/AddOns/UnBot");
        AddRetiredDirectoryFilesIfAbsent(remotePaths, removedPaths, "Interface/AddOns/MultiBot");
        return removedPaths.ToList();
    }

    private void AddRetiredDirectoryFilesIfAbsent(HashSet<string> remotePaths, HashSet<string> removedPaths, string relativeDirectory)
    {
        var normalizedPrefix = NormalizeManifestPath(relativeDirectory).TrimEnd('/') + "/";
        if (remotePaths.Any(path => path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var directory = GetSafeTargetPath(_settings.InstallPath, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            removedPaths.Add(Path.GetRelativePath(_settings.InstallPath, file).Replace('\\', '/'));
        }
    }

    private int DeleteRemovedClientFiles(List<string> relativePaths, CancellationToken cancellationToken)
    {
        var deletedCount = 0;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(_settings.InstallPath).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var relativePath in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = GetSafeTargetPath(_settings.InstallPath, relativePath);
            if (!File.Exists(target))
            {
                continue;
            }

            DeleteFileWithRetry(target, cancellationToken);
            deletedCount++;

            var currentDirectory = Path.GetDirectoryName(target);
            while (!string.IsNullOrWhiteSpace(currentDirectory))
            {
                var normalizedDirectory = Path.GetFullPath(currentDirectory).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(normalizedDirectory, root, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                directories.Add(normalizedDirectory);
                currentDirectory = Path.GetDirectoryName(normalizedDirectory);
            }
        }

        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectoryIfEmpty(directory);
        }

        return deletedCount;
    }

    private static void DeleteFileWithRetry(string path, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Thread.Sleep(250);
            }
        }

        throw new IOException("Impossible de supprimer le fichier obsolete: " + path, lastError);
    }

    private static void TryDeleteDirectoryIfEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private LauncherManifest? LoadInstalledManifestHistory()
    {
        var historyPath = GetInstalledManifestHistoryPath();
        if (!File.Exists(historyPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(historyPath);
            return JsonSerializer.Deserialize<LauncherManifest>(stream, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveInstalledManifestHistory(LauncherManifest manifest)
    {
        if (!GameDirectoryAccess.CanWrite(_settings.InstallPath))
        {
            return;
        }

        Directory.CreateDirectory(_settings.InstallPath);
        var historyPath = GetInstalledManifestHistoryPath();
        var options = new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(manifest, options);
        File.WriteAllText(historyPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string GetInstalledManifestHistoryPath()
    {
        return Path.Combine(_settings.InstallPath, "client-manifest-cache.json");
    }

    private static string NormalizeManifestPath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/').ToLowerInvariant();
    }

    private async Task<List<LauncherFile>> FindMissingOrChangedFilesAsync(LauncherManifest manifest, bool updateProgress, CancellationToken cancellationToken)
    {
        var missingOrChanged = new List<LauncherFile>();
        var checkedCount = 0;

        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedCount++;
            if (updateProgress)
            {
                ProgressText.Text = $"{checkedCount}/{manifest.Files.Count}";
            }

            var target = GetSafeTargetPath(_settings.InstallPath, file.Path);
            if (!File.Exists(target) || new FileInfo(target).Length != file.Size)
            {
                missingOrChanged.Add(file);
                continue;
            }

            try
            {
                var localHash = await ComputeSha256Async(target, cancellationToken);
                if (!string.Equals(localHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    missingOrChanged.Add(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missingOrChanged.Add(file);
            }
        }

        return missingOrChanged;
    }

    private async Task InstallOrUpdateAsync(CancellationToken cancellationToken)
    {
        _settings.InstallPath = LauncherSettings.NormalizeInstallPath(InstallPathBox.Text);
        InstallPathBox.Text = _settings.InstallPath;
        Directory.CreateDirectory(_settings.InstallPath);

        SetStatus("Chargement du manifeste...");
        AppendLog("Vérification des fichiers du client...");
        var manifest = await LoadManifestAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(manifest.Version))
        {
            AppendLog("Version client: " + manifest.Version);
        }

        if (manifest.Files.Count == 0)
        {
            throw new InvalidOperationException("Le manifeste ne contient aucun fichier.");
        }

        GameInstallServices.StopRunningGameProcesses(_settings.InstallPath);
        AppendLog("Processus WoW ferme si necessaire avant verification.");

        SetStatus("Comparaison du manifeste...");
        var missingOrChanged = await FindMissingOrChangedFilesForManifestAsync(manifest, updateProgress: true, cancellationToken);
        var removedFiles = FindRemovedFilesForManifest(manifest);

        if (missingOrChanged.Count == 0 && removedFiles.Count == 0)
        {
            SaveInstalledManifestHistory(manifest);
            _announcedGameUpdateVersion = null;
            SetGameAction(GameAction.Play);
            RegisterGameApplication(manifest.Version);
            MainProgress.Value = 100;
            ProgressText.Text = "À jour";
            SetStatus("Client à jour.");
            AppendLog("Aucun fichier à télécharger.");
            return;
        }

        var totalBytes = missingOrChanged.Sum(file => Math.Max(file.Size, 0));
        long downloadedBytes = 0;

        if (missingOrChanged.Count > 0)
        {
            AppendLog($"{missingOrChanged.Count} fichier(s) a telecharger, {FormatBytes(totalBytes)}.");
        }
        if (removedFiles.Count > 0)
        {
            AppendLog($"{removedFiles.Count} fichier(s) obsolete(s) a supprimer.");
        }

        if (removedFiles.Count > 0)
        {
            SetStatus("Nettoyage...");
            var deletedCount = DeleteRemovedClientFiles(removedFiles, cancellationToken);
            AppendLog($"Nettoyage: {deletedCount} fichier(s) supprime(s).");
        }

        if (missingOrChanged.Count == 0)
        {
            SaveInstalledManifestHistory(manifest);
            _announcedGameUpdateVersion = null;
            SetGameAction(GameAction.Play);
            RegisterGameApplication(manifest.Version);
            MainProgress.Value = 100;
            ProgressText.Text = "A jour";
            SetStatus("Client a jour.");
            AppendLog("Aucun fichier a telecharger.");
            return;
        }

        SetStatus("Telechargement...");
        var downloadStopwatch = Stopwatch.StartNew();
        var fileIndex = 0;

        foreach (var file in missingOrChanged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileIndex++;
            var target = GetSafeTargetPath(_settings.InstallPath, file.Path);
            var uri = BuildFileUri(manifest, file);

            SetStatus($"Téléchargement {fileIndex}/{missingOrChanged.Count}...");
            AppendLog("Téléchargement: " + file.Path);
            await DownloadFileAsync(uri, target, file.Size, file.Sha256, progressBytes =>
            {
                var current = downloadedBytes + progressBytes;
                MainProgress.Value = totalBytes == 0 ? 0 : Math.Clamp((double)current / totalBytes * 100, 0, 100);
                ProgressText.Text = FormatTransferProgress(current, totalBytes, downloadStopwatch.Elapsed);
            }, cancellationToken);

            downloadedBytes += Math.Max(file.Size, 0);
        }

        SaveInstalledManifestHistory(manifest);
        _announcedGameUpdateVersion = null;
        SetGameAction(GameAction.Play);
        RegisterGameApplication(manifest.Version);
        MainProgress.Value = 100;
        ProgressText.Text = "Terminé";
        SetStatus("Installation terminée.");
        AppendLog("Client prêt: " + _settings.InstallPath);
        ShowToast("Client prêt", "L'installation du client WotLK est terminée.", ToastKind.Success);
    }

    private void RegisterGameApplication(string clientVersion)
    {
        if (!GameDirectoryAccess.CanWrite(_settings.InstallPath))
        {
            return;
        }

        var configPath = GameInstallServices.EnsureDefaultClientConfig(_settings.InstallPath, _settings.GameLocale);
        var uninstallerPath = GameInstallServices.RegisterInstalledGame(_settings.InstallPath, clientVersion);
        AppendLog("Configuration video/langue WotLK ajustee: " + configPath);
        AppendLog("Application Windows WotLK Client enregistree: " + uninstallerPath);
    }

    private async Task<LauncherManifest> LoadManifestAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(_settings.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<LauncherManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Impossible de lire le manifeste.");
    }

    private async Task<LauncherUpdateManifest> LoadLauncherUpdateManifestAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(LauncherUpdateManifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<LauncherUpdateManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Impossible de lire le manifeste de mise à jour launcher.");
    }

    private async Task DownloadFileAsync(Uri uri, string targetPath, long expectedSize, string expectedSha256, Action<long> progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var targetDirectory = Path.GetDirectoryName(targetPath) ?? throw new InvalidOperationException("Chemin cible invalide.");
        Directory.CreateDirectory(targetDirectory);
        var tempPath = Path.Combine(targetDirectory, "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".download");

        try
        {
            await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var local = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long written = 0;

                while (true)
                {
                    var read = await remote.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    progress(written);
                }

                if (expectedSize >= 0 && written != expectedSize)
                {
                    throw new InvalidOperationException($"Taille invalide pour {Path.GetFileName(targetPath)}: {FormatBytes(written)} recu, {FormatBytes(expectedSize)} attendu.");
                }
            }

            var downloadedHash = await ComputeSha256Async(tempPath, cancellationToken);
            if (!string.Equals(downloadedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Hash invalide apres telechargement: " + Path.GetFileName(targetPath));
            }

            await MoveDownloadedFileWithRetryAsync(tempPath, targetPath, cancellationToken);
        }
        catch
        {
            DeleteFileIfExists(tempPath);
            throw;
        }
    }

    private static async Task MoveDownloadedFileWithRetryAsync(string tempPath, string targetPath, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(targetPath))
                {
                    TrySetNormalAttributes(targetPath);
                }

                File.Move(tempPath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(1000, cancellationToken);
            }
        }

        throw new IOException("Impossible de remplacer " + Path.GetFileName(targetPath) + ". Ferme le jeu ou tout programme qui utilise le dossier WotLK, puis relance l'installation.", lastError);
    }

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                TrySetNormalAttributes(path);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TrySetNormalAttributes(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
        }
    }

    private async Task DownloadLauncherBinaryAsync(Uri uri, string targetPath, long expectedSize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(LauncherUpdateRequestHeader, LauncherUpdateRequestMarker);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseSize = response.Content.Headers.ContentLength;
        var totalSize = expectedSize > 0 ? expectedSize : responseSize;
        await using var remote = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var local = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

        var buffer = new byte[1024 * 128];
        long written = 0;
        var downloadStopwatch = Stopwatch.StartNew();

        while (true)
        {
            var read = await remote.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;

            if (totalSize is > 0)
            {
                MainProgress.Value = Math.Clamp((double)written / totalSize.Value * 100, 0, 100);
                ProgressText.Text = FormatTransferProgress(written, totalSize.Value, downloadStopwatch.Elapsed);
            }
            else
            {
                ProgressText.Text = FormatTransferProgress(written, null, downloadStopwatch.Elapsed);
            }
        }

        MainProgress.Value = 100;
        ProgressText.Text = totalSize is > 0
            ? $"{FormatBytes(written)} / {FormatBytes(totalSize.Value)}"
            : FormatBytes(written);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 256, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetSafeTargetPath(string installRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Chemin vide dans le manifeste.");
        }

        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalizedRelative))
        {
            throw new InvalidOperationException("Chemin absolu interdit dans le manifeste: " + relativePath);
        }

        var root = Path.GetFullPath(installRoot);
        var target = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Chemin hors dossier d'installation: " + relativePath);
        }

        return target;
    }

    private static Uri BuildFileUri(LauncherManifest manifest, LauncherFile file)
    {
        if (Uri.TryCreate(file.Url, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        var baseUrl = string.IsNullOrWhiteSpace(manifest.BaseUrl)
            ? throw new InvalidOperationException("baseUrl manquant dans le manifeste.")
            : manifest.BaseUrl.TrimEnd('/') + "/";

        var relativeUrl = string.IsNullOrWhiteSpace(file.Url)
            ? "files/" + EscapeRelativeUrl(file.Path)
            : file.Url.TrimStart('/');

        return new Uri(new Uri(baseUrl), relativeUrl);
    }

    private static Uri BuildLauncherUpdateUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri;
        }

        return new Uri(new Uri(LauncherUpdateManifestUrl), url);
    }

    private static string EscapeRelativeUrl(string path)
    {
        return string.Join("/", path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private void SaveSettingsFromUi()
    {
        _settings.InstallPath = LauncherSettings.NormalizeInstallPath(InstallPathBox.Text);
        _settings.ManifestUrl = LauncherSettings.GetDefaultManifestUrl();
        _settings.GameLocale = GetSelectedGameLocale();
        _settings.AutomaticLauncherUpdates = AutomaticUpdatesCheckBox.IsChecked == true;
        _settings.CloseLauncherOnGameStart = CloseOnGameStartCheckBox.IsChecked == true;
        _dependencies.SaveSettings(_settings);
        InstallPathBox.Text = _settings.InstallPath;
        SettingsInstallPathBox.Text = _settings.InstallPath;
    }

    private void SyncSettingsUi()
    {
        _isInitializingUi = true;
        try
        {
            SettingsInstallPathBox.Text = _settings.InstallPath;
            SetSettingsLanguageSelection(_settings.GameLocale);
            AutomaticUpdatesCheckBox.IsChecked = _settings.AutomaticLauncherUpdates;
            CloseOnGameStartCheckBox.IsChecked = _settings.CloseLauncherOnGameStart;
            SettingsLogBox.Text = LogBox.Text;
            SettingsLogBox.ScrollToEnd();
        }
        finally
        {
            _isInitializingUi = false;
        }
    }

    private void SettingsLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi || SettingsLanguageComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _isInitializingUi = true;
        SetLanguageSelection(LauncherSettings.NormalizeGameLocale(item.Tag?.ToString()));
        _isInitializingUi = false;
        GameLanguageComboBox_SelectionChanged(sender, e);
    }

    private void SettingsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializingUi)
        {
            return;
        }

        SaveSettingsFromUi();
        if (_settings.AutomaticLauncherUpdates)
        {
            _launcherUpdateTimer.Start();
            _ = CheckLauncherUpdateAsync();
        }
        else
        {
            _launcherUpdateTimer.Stop();
        }
    }

    private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LauncherSettings.SettingsDirectory);
        if (!File.Exists(GetLauncherLogPath()))
        {
            File.WriteAllText(GetLauncherLogPath(), LogBox.Text, new UTF8Encoding(false));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "/select,\"" + GetLauncherLogPath() + "\"",
            UseShellExecute = true
        });
    }

    private bool EnsureGameDirectoryWritable()
    {
        try
        {
            if (GameDirectoryAccess.EnsureWritable(this, _settings.InstallPath))
            {
                return true;
            }

            SetStatus("Autorisation annulee.");
            AppendLog("Autorisation du dossier WotLK annulee.");
            return false;
        }
        catch (Exception ex)
        {
            SetStatus("Autorisation requise.");
            AppendLog("Autorisation du dossier WotLK impossible: " + ex.Message);
            ShowToast("Autorisation requise", ex.Message, ToastKind.Warning);
            return false;
        }
    }

    private void SetLanguageSelection(string locale)
    {
        var normalizedLocale = LauncherSettings.NormalizeGameLocale(locale);
        foreach (var item in GameLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalizedLocale, StringComparison.OrdinalIgnoreCase))
            {
                GameLanguageComboBox.SelectedItem = item;
                return;
            }
        }

        GameLanguageComboBox.SelectedIndex = 0;
    }

    private void SetSettingsLanguageSelection(string locale)
    {
        var normalizedLocale = LauncherSettings.NormalizeGameLocale(locale);
        foreach (var item in SettingsLanguageComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), normalizedLocale, StringComparison.OrdinalIgnoreCase))
            {
                SettingsLanguageComboBox.SelectedItem = item;
                return;
            }
        }

        SettingsLanguageComboBox.SelectedIndex = 0;
    }

    private string GetSelectedGameLocale()
    {
        if (GameLanguageComboBox.SelectedItem is ComboBoxItem item)
        {
            return LauncherSettings.NormalizeGameLocale(item.Tag?.ToString());
        }

        return LauncherSettings.GetDefaultGameLocale();
    }

    private static string GetGameLocaleLabel(string locale)
    {
        return LauncherSettings.NormalizeGameLocale(locale) == "enUS" ? "English" : "Francais";
    }

    private static string GetLauncherVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        return "v" + version;
    }

    private void SetGameAction(GameAction action)
    {
        _gameAction = action;
        if (_downloadCancellation is null)
        {
            UpdateButton.Content = GetGameActionLabel(action);
            HomePlayButton.Content = GetGameActionLabel(action);
        }

        HomeClientStatusText.Text = action switch
        {
            GameAction.Play => "Prêt à jouer",
            GameAction.Update => "Mise à jour disponible",
            _ => "Installation requise"
        };
    }

    private static string GetGameActionLabel(GameAction action)
    {
        return action switch
        {
            GameAction.Play => "JOUER",
            GameAction.Update => "METTRE A JOUR",
            _ => "INSTALLER"
        };
    }

    private void SetBusy(bool busy)
    {
        LauncherSelfUpdateButton.IsEnabled = !busy;
        HeaderServerStatusButton.IsEnabled = !busy;
        HeaderSettingsButton.IsEnabled = !busy;
        ProfileButton.IsEnabled = !busy;
        BrowseInstallPathButton.IsEnabled = !busy;
        GameLanguageComboBox.IsEnabled = !busy;
        ClientTabButton.IsEnabled = !busy;
        AddonsTabButton.IsEnabled = !busy;
        FriendsTabButton.IsEnabled = !busy;
        NewsTabButton.IsEnabled = !busy;
        ServerTabButton.IsEnabled = !busy;
        AccountTabButton.IsEnabled = !busy;
        SettingsTabButton.IsEnabled = !busy;
        AddonItemsControl.IsEnabled = !busy;
        AddonApplyButton.IsEnabled = !busy || _isApplyingAddons;
        AddonApplyButton.Content = busy && _isApplyingAddons ? "ANNULER" : "APPLIQUER";
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = busy ? "ANNULER" : GetGameActionLabel(_gameAction);
        HomePlayButton.IsEnabled = !busy;
        HomePlayButton.Content = busy ? "ANNULER" : GetGameActionLabel(_gameAction);
    }

    private void SetStatus(string status)
    {
        StatusText.Text = GetStatusBadgeText(status);
    }

    private static string GetStatusBadgeText(string status)
    {
        var cleanStatus = status.Trim();
        var normalizedStatus = cleanStatus
            .TrimEnd('.')
            .Replace('à', 'a')
            .Replace('é', 'e')
            .Replace('è', 'e')
            .ToLowerInvariant();

        return normalizedStatus switch
        {
            "client a jour" => "Client à jour - Prêt à jouer",
            "pret" => "Prêt",
            "telechargement" => "Téléchargement",
            "mise a jour disponible" => "Mise à jour disponible",
            _ => cleanStatus.TrimEnd('.')
        };
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogBox.AppendText(line);
        LogBox.ScrollToEnd();
        if (SettingsLogBox is not null)
        {
            SettingsLogBox.AppendText(line);
            SettingsLogBox.ScrollToEnd();
        }

        try
        {
            _dependencies.PersistLogLine(line);
        }
        catch
        {
            // Logging must never interrupt launcher operations.
        }
    }

    internal Task RestoreSessionAndAnalyzeForCharacterizationAsync()
    {
        return RestoreSessionAndAnalyzeAsync();
    }

    internal Task RefreshGameActionForCharacterizationAsync(bool silentWhenUpToDate = false)
    {
        return RefreshGameActionAsync(silentWhenUpToDate);
    }

    internal void SetGameActionForCharacterization(GameAction action)
    {
        SetGameAction(action);
    }

    internal void SetBusyForCharacterization(bool busy)
    {
        SetBusy(busy);
    }

    internal CancellationToken AttachActiveOperationForCharacterization()
    {
        if (_downloadCancellation is not null)
        {
            throw new InvalidOperationException("Une opération legacy est déjà active.");
        }

        _downloadCancellation = new CancellationTokenSource();
        SetBusy(true);
        return _downloadCancellation.Token;
    }

    internal LegacyMainWindowSnapshot CaptureCharacterizationSnapshot()
    {
        return new LegacyMainWindowSnapshot(
            _gameAction,
            UpdateButton.Content?.ToString() ?? string.Empty,
            UpdateButton.IsEnabled,
            HomePlayButton.Content?.ToString() ?? string.Empty,
            HomePlayButton.IsEnabled,
            HomeClientStatusText.Text,
            VerifyClientButton.IsEnabled,
            AddonsTabButton.IsEnabled,
            LauncherSelfUpdateButton.IsEnabled,
            MainProgress.Value,
            ProgressText.Text,
            _downloadCancellation is not null,
            _isRefreshingGameAction);
    }

    internal LegacyLocalPathSnapshot CaptureLocalPathCharacterization()
    {
        return new LegacyLocalPathSnapshot(_settings.InstallPath, GetLauncherLogPath());
    }

    private static string GetLauncherLogPath()
    {
        return LauncherSettings.LauncherLogPath;
    }

    private static void WriteLauncherUpdateScript(string scriptPath, string targetExe, string downloadedExe, int processId)
    {
        var workingDirectory = Path.GetDirectoryName(targetExe) ?? Environment.CurrentDirectory;
        var script = $$"""
        $ErrorActionPreference = 'Stop'
        $ProcessIdToWait = {{processId}}
        $Source = {{PowerShellString(downloadedExe)}}
        $Target = {{PowerShellString(targetExe)}}
        $WorkingDirectory = {{PowerShellString(workingDirectory)}}

        try {
            Wait-Process -Id $ProcessIdToWait -Timeout 45 -ErrorAction SilentlyContinue
        } catch {
        }

        Copy-Item -LiteralPath $Source -Destination $Target -Force
        Start-Process -FilePath $Target -WorkingDirectory $WorkingDirectory
        Start-Sleep -Seconds 2
        Remove-Item -LiteralPath $Source -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void StartElevatedScript(string scriptPath)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteProcessArgument(scriptPath),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        if (process is null)
        {
            throw new InvalidOperationException("Impossible de lancer le processus de mise à jour.");
        }
    }

    private static string PowerShellString(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string QuoteProcessArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["o", "Ko", "Mo", "Go", "To"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unit]);
    }

    private static string FormatTransferProgress(long received, long? total, TimeSpan elapsed)
    {
        var parts = new List<string>
        {
            total is > 0
                ? $"{FormatBytes(received)} / {FormatBytes(total.Value)}"
                : FormatBytes(received)
        };

        if (received <= 0 || elapsed.TotalSeconds < 0.5)
        {
            return string.Join(" · ", parts);
        }

        var bytesPerSecond = received / elapsed.TotalSeconds;
        if (bytesPerSecond <= 0)
        {
            return string.Join(" · ", parts);
        }

        parts.Add(FormatBytes((long)bytesPerSecond) + "/s");
        if (total is > 0 && total.Value > received)
        {
            var remaining = TimeSpan.FromSeconds((total.Value - received) / bytesPerSecond);
            parts.Add(FormatRemainingTime(remaining));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatRemainingTime(TimeSpan remaining)
    {
        if (remaining.TotalSeconds < 60)
        {
            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} s restantes";
        }

        if (remaining.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min restantes";
        }

        return $"{(int)remaining.TotalHours} h {remaining.Minutes:00} restantes";
    }
}
