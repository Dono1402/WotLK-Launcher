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
using System.Windows.Threading;

namespace WotLK.Launcher;

public partial class MainWindow : Window
{
    private enum GameAction
    {
        Install,
        Update,
        Play
    }

    private const string LauncherUpdateManifestUrl = "http://152.228.225.7/launcher/launcher-update.json";
    private const string LauncherUpdateRequestHeader = "X-WotLK-Launcher-Update";
    private const string LauncherUpdateRequestMarker = "1";
    private static readonly TimeSpan LauncherUpdateCheckInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new();
    private readonly LauncherSettings _settings;
    private readonly DispatcherTimer _launcherUpdateTimer;
    private CancellationTokenSource? _downloadCancellation;
    private LauncherUpdateManifest? _launcherUpdate;
    private GameAction _gameAction = GameAction.Install;
    private bool _isRefreshingGameAction;
    private bool _isCheckingLauncherUpdate;
    private bool _isInitializingUi = true;
    private string? _announcedLauncherUpdateHash;
    private string? _announcedGameUpdateVersion;

    public MainWindow()
    {
        InitializeComponent();

        var displayName = GetLauncherDisplayName();
        Title = displayName;
        TitleText.Text = displayName;
        VersionText.Text = GetLauncherVersionText();
        TitleBarText.Text = displayName + " - " + GetLauncherVersionText();

        _settings = LauncherSettings.Load();
        _settings.Save();
        InstallPathBox.Text = _settings.InstallPath;
        SetLanguageSelection(_settings.GameLocale);
        _isInitializingUi = false;

        _launcherUpdateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LauncherUpdateCheckInterval
        };
        _launcherUpdateTimer.Tick += LauncherUpdateTimer_Tick;

        AppendLog("Launcher prêt.");
        SetInitialGameActionFromDisk();
        _ = CheckLauncherUpdateAsync();
        _ = RefreshGameActionAsync();
        _launcherUpdateTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _launcherUpdateTimer.Stop();
        _launcherUpdateTimer.Tick -= LauncherUpdateTimer_Tick;
        base.OnClosed(e);
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
            System.Windows.MessageBox.Show(this, ex.Message, "Erreur mise à jour launcher", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (_downloadCancellation is not null)
        {
            _downloadCancellation.Cancel();
            return;
        }

        SaveSettingsFromUi();
        if (_gameAction == GameAction.Play)
        {
            PlayGame();
            return;
        }

        _downloadCancellation = new CancellationTokenSource();
        SetBusy(true);

        try
        {
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
            System.Windows.MessageBox.Show(this, ex.Message, "Erreur launcher", MessageBoxButton.OK, MessageBoxImage.Error);
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
        _settings.Save();
        InstallPathBox.Text = _settings.InstallPath;

        AppendLog("Dossier client: " + _settings.InstallPath);
        SetInitialGameActionFromDisk();
        await RefreshGameActionAsync();
    }

    private void GameLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializingUi)
        {
            return;
        }

        SaveSettingsFromUi();
        var wowPath = Path.Combine(_settings.InstallPath, "Wow.exe");
        if (!File.Exists(wowPath))
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

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void PlayGame()
    {
        var wowPath = Path.Combine(_settings.InstallPath, "Wow.exe");
        if (!File.Exists(wowPath))
        {
            System.Windows.MessageBox.Show(this, "Wow.exe est introuvable. Installe ou mets a jour le client d'abord.", "Client introuvable", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetGameAction(GameAction.Install);
            return;
        }

        GameInstallServices.EnsureDefaultClientConfig(_settings.InstallPath, _settings.GameLocale);

        Process.Start(new ProcessStartInfo
        {
            FileName = wowPath,
            WorkingDirectory = _settings.InstallPath,
            UseShellExecute = true
        });

        AppendLog("Jeu lance: " + wowPath);
    }

    private async void LauncherUpdateTimer_Tick(object? sender, EventArgs e)
    {
        await CheckLauncherUpdateAsync();
        await RefreshGameActionAsync(silentWhenUpToDate: true);
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
                !string.Equals(currentHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
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

        var wowPath = Path.Combine(_settings.InstallPath, "Wow.exe");
        var hasClient = File.Exists(wowPath);
        SetGameAction(hasClient ? GameAction.Play : GameAction.Install);
        MainProgress.Value = hasClient ? 100 : 0;
        ProgressText.Text = hasClient ? "Client à jour" : string.Empty;
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

            var wowPath = Path.Combine(_settings.InstallPath, "Wow.exe");
            if (!File.Exists(wowPath))
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
            var wowPath = Path.Combine(_settings.InstallPath, "Wow.exe");
            SetGameAction(File.Exists(wowPath) ? GameAction.Play : GameAction.Install);
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

        var installedVersion = TryReadInstalledClientVersion();
        var wowPath = Path.Combine(_settings.InstallPath, "Wow.exe");
        if (!string.IsNullOrWhiteSpace(manifest.Version) &&
            string.Equals(installedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(wowPath))
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
        Directory.CreateDirectory(_settings.InstallPath);
        var historyPath = GetInstalledManifestHistoryPath();
        var options = new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(manifest, options);
        File.WriteAllText(historyPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string? TryReadInstalledClientVersion()
    {
        var markerPath = Path.Combine(_settings.InstallPath, "client-install.json");
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(markerPath));
            return document.RootElement.TryGetProperty("clientVersion", out var versionElement)
                ? versionElement.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

        GameInstallServices.StopRunningGameProcesses(_settings.InstallPath);
        AppendLog("Processus Wow ferme si necessaire.");

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

        foreach (var file in missingOrChanged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = GetSafeTargetPath(_settings.InstallPath, file.Path);
            var uri = BuildFileUri(manifest, file);

            AppendLog("Téléchargement: " + file.Path);
            await DownloadFileAsync(uri, target, file.Size, file.Sha256, progressBytes =>
            {
                var current = downloadedBytes + progressBytes;
                MainProgress.Value = totalBytes == 0 ? 0 : Math.Clamp((double)current / totalBytes * 100, 0, 100);
                ProgressText.Text = $"{FormatBytes(current)} / {FormatBytes(totalBytes)}";
            }, cancellationToken);

            downloadedBytes += file.Size;
        }

        SaveInstalledManifestHistory(manifest);
        _announcedGameUpdateVersion = null;
        SetGameAction(GameAction.Play);
        RegisterGameApplication(manifest.Version);
        MainProgress.Value = 100;
        ProgressText.Text = "Terminé";
        SetStatus("Installation terminée.");
        AppendLog("Client prêt: " + _settings.InstallPath);
    }

    private void RegisterGameApplication(string clientVersion)
    {
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
                ProgressText.Text = $"{FormatBytes(written)} / {FormatBytes(totalSize.Value)}";
            }
            else
            {
                ProgressText.Text = FormatBytes(written);
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
        _settings.Save();
        InstallPathBox.Text = _settings.InstallPath;
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

    private static string GetLauncherDisplayName()
    {
        return "WotLK Launcher";
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
        }
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
        BrowseInstallPathButton.IsEnabled = !busy;
        GameLanguageComboBox.IsEnabled = !busy;
        UpdateButton.IsEnabled = true;
        UpdateButton.Content = busy ? "ANNULER" : GetGameActionLabel(_gameAction);
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
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
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
}
