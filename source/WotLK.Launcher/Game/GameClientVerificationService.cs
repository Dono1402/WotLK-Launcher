namespace WotLK.Launcher.Game;

internal interface IGameClientVerificationService
{
    Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken);
}

internal sealed class GameClientVerificationService : IGameClientVerificationService
{
    private readonly IGameManifestClient _manifestClient;
    private readonly IGameFileVerifier _fileVerifier;
    private readonly IInstalledManifestStore _manifestStore;
    private readonly Func<string, bool> _hasPlayableClient;
    private readonly Func<string, bool> _isGameRunning;

    internal GameClientVerificationService(
        IGameManifestClient manifestClient,
        IGameFileVerifier fileVerifier,
        IInstalledManifestStore manifestStore,
        Func<string, bool>? hasPlayableClient = null,
        Func<string, bool>? isGameRunning = null)
    {
        _manifestClient = manifestClient ?? throw new ArgumentNullException(nameof(manifestClient));
        _fileVerifier = fileVerifier ?? throw new ArgumentNullException(nameof(fileVerifier));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        _hasPlayableClient = hasPlayableClient ?? GameInstallServices.HasPlayableClient;
        _isGameRunning = isGameRunning ?? GameInstallServices.IsGameRunning;
    }

    public async Task<GameClientVerificationResult> VerifyAsync(
        LauncherSettings settings,
        bool reportFileProgress,
        Action<GameVerificationProgress>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        reportProgress?.Invoke(new GameVerificationProgress(
            GameVerificationPhase.CheckingLocalClient));

        if (!_hasPlayableClient(settings.InstallPath))
        {
            return Result(
                GameVerificationOutcome.NotInstalled,
                GameAction.Install,
                GameUpdateKnowledge.Unknown);
        }

        if (_isGameRunning(settings.InstallPath))
        {
            return Result(
                GameVerificationOutcome.GameRunning,
                GameAction.Play,
                GameUpdateKnowledge.Unknown);
        }

        reportProgress?.Invoke(new GameVerificationProgress(
            GameVerificationPhase.LoadingManifest));
        LauncherManifest manifest = await _manifestClient.LoadAsync(
            settings.ManifestUrl,
            cancellationToken);
        if (manifest.Files.Count == 0)
        {
            return Result(
                GameVerificationOutcome.EmptyManifest,
                GameAction.Play,
                GameUpdateKnowledge.Unavailable,
                manifest.Version);
        }

        reportProgress?.Invoke(new GameVerificationProgress(
            GameVerificationPhase.ComparingManifest));
        GameFileComparisonResult comparison = await _fileVerifier
            .FindMissingOrChangedFilesAsync(
                settings.InstallPath,
                manifest,
                reportFileProgress ? reportProgress : null,
                cancellationToken);
        IReadOnlyList<string> removedFiles = _fileVerifier.FindRemovedFiles(
            settings.InstallPath,
            manifest);
        int changeCount = comparison.MissingOrChangedFiles.Count + removedFiles.Count;

        if (changeCount == 0)
        {
            _manifestStore.Save(settings.InstallPath, manifest);
            return Result(
                GameVerificationOutcome.UpToDate,
                GameAction.Play,
                GameUpdateKnowledge.Known,
                manifest.Version);
        }

        return Result(
            GameVerificationOutcome.UpdateAvailable,
            GameAction.Update,
            GameUpdateKnowledge.Known,
            manifest.Version,
            changeCount);
    }

    private static GameClientVerificationResult Result(
        GameVerificationOutcome outcome,
        GameAction action,
        GameUpdateKnowledge knowledge,
        string availableVersion = "",
        int changeCount = 0)
    {
        return new GameClientVerificationResult(
            outcome,
            action,
            knowledge,
            availableVersion,
            changeCount);
    }
}
