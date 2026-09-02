using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

internal static class LauncherAddonsRuntimeTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        await CharacterizeCatalogAndLegacyPipelineAsync();
        await CharacterizeCatalogProjectionAsync();
        await CharacterizeRuntimeOperationsAsync();
        await CharacterizeFailuresCancellationAndShutdownAsync();
        await CharacterizeSequentialBatchAsync();
        CharacterizeCompatibilityMatrix();
        CharacterizePreviewIsolation();
        await ValidateRuntimeWpfAsync(captureDirectory);
        Console.WriteLine("Atlas addon runtime OK (04A.2, legacy pipeline reused).\n");
        return 0;
    }

    private static async Task CharacterizeCatalogAndLegacyPipelineAsync()
    {
        string repositoryRoot = FindRepositoryRoot();
        string catalogPath = Path.Combine(repositoryRoot, "current", "addons", "catalog.json");
        byte[] productionCatalog = await File.ReadAllBytesAsync(catalogPath);
        Uri catalogUri = new("https://atlas.test/addons/catalog.json");
        using (MappedHttpHandler catalogHandler = new())
        using (HttpClient catalogHttp = new(catalogHandler))
        {
            catalogHandler.Responses[catalogUri] = productionCatalog;
            AddonCatalog catalog = await AddonInstallServices.LoadCatalogAsync(
                catalogHttp,
                catalogUri,
                CancellationToken.None);
            Equal(10, catalog.Addons.Count, "Le catalogue Atlas versionné doit conserver ses 10 addons publiés.");
            Equal("30403", catalog.ClientInterface, "Le catalogue doit rester ciblé WotLK 3.4.3.");

            catalogHandler.Responses[catalogUri] = Encoding.UTF8.GetBytes(
                "{\"schemaVersion\":1,\"clientInterface\":\"30403\",\"addons\":[]}");
            await ThrowsAsync<InvalidOperationException>(
                () => AddonInstallServices.LoadCatalogAsync(catalogHttp, catalogUri, CancellationToken.None),
                "Le contrat legacy refuse toujours un catalogue distant vide.");
        }

        string root = CreatePlayableClientRoot();
        try
        {
            byte[] mainV1 = CreateArchive(new Dictionary<string, string>
            {
                ["AtlasAlpha/AtlasAlpha.toc"] = "## Interface: 30403\n## Title: Atlas Alpha\n",
                ["AtlasAlpha/core.lua"] = "local version = '@atlas-version@'\n"
            });
            byte[] componentV1 = CreateArchive(new Dictionary<string, string>
            {
                ["AtlasAlpha/module.lua"] = "return 'component'\n"
            });
            AddonPackage alphaV1 = CreatePackage(
                "alpha",
                "Alpha",
                "1.0.0",
                "AtlasAlpha",
                mainV1,
                dependencies: ["dependency-addon"],
                components:
                [
                    CreateComponent("Module Alpha", "https://atlas.test/alpha-component.zip", componentV1)
                ],
                replacements: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["@atlas-version@"] = "1.0.0"
                });
            byte[] dependencyArchive = CreateArchive(new Dictionary<string, string>
            {
                ["AtlasDependency/AtlasDependency.toc"] = "## Interface: 30403\n"
            });
            AddonPackage dependency = CreatePackage(
                "dependency-addon",
                "Dependency",
                "1.0.0",
                "AtlasDependency",
                dependencyArchive);
            AddonCatalog catalogV1 = CreateCatalog(alphaV1, dependency);

            using MappedHttpHandler handler = new();
            handler.Responses[new Uri(alphaV1.Url)] = mainV1;
            handler.Responses[new Uri(alphaV1.Components[0].Url)] = componentV1;
            handler.Responses[new Uri(dependency.Url)] = dependencyArchive;
            using HttpClient http = new(handler);

            List<AddonTransferProgress> progress = [];
            await AddonInstallServices.ApplySelectionAsync(
                http,
                catalogV1,
                root,
                new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    [alphaV1.Id] = true
                },
                new InlineProgress<AddonTransferProgress>(progress.Add),
                log: null,
                CancellationToken.None);

            string addonsDirectory = AddonInstallServices.GetAddonsDirectory(root);
            string alphaDirectory = Path.Combine(addonsDirectory, "AtlasAlpha");
            True(File.Exists(Path.Combine(alphaDirectory, "module.lua")),
                "Les composants multiples doivent rejoindre la transaction legacy du package.");
            Equal("local version = '1.0.0'\n",
                await File.ReadAllTextAsync(Path.Combine(alphaDirectory, "core.lua")),
                "Les remplacements de jetons legacy doivent rester appliqués.");
            True(!Directory.Exists(Path.Combine(addonsDirectory, "AtlasDependency")),
                "Les dépendances du catalogue restent des métadonnées et ne sont pas auto-installées.");
            True(progress.Count >= 2 && progress[^1].BytesReceived == componentV1.Length,
                "La progression legacy doit exposer les octets réellement reçus pour chaque archive.");

            IReadOnlyDictionary<string, AddonInspection> installed = AddonInstallServices.Inspect(catalogV1, root);
            Equal(AddonLocalStatus.Installed, installed[alphaV1.Id].Status,
                "Un addon géré complet doit être installé.");
            Equal("1.0.0", installed[alphaV1.Id].InstalledVersion,
                "La version installée doit provenir de l'état Atlas.");
            Equal(alphaV1.EffectiveInstallHash, installed[alphaV1.Id].InstalledSha256,
                "Le hash installé doit provenir de l'état Atlas.");
            True(installed[alphaV1.Id].InstalledAtUtc.HasValue,
                "La date d'installation existante doit être projetée.");

            string statePath = Path.Combine(addonsDirectory, ".atlas-addons.json");
            using (JsonDocument state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath)))
            {
                Equal(1, state.RootElement.GetProperty("schemaVersion").GetInt32(),
                    "Le schéma .atlas-addons.json ne doit pas changer.");
                JsonElement alphaState = state.RootElement.GetProperty("addons").GetProperty("alpha");
                Equal("1.0.0", alphaState.GetProperty("version").GetString(),
                    "L'état legacy doit être écrit après le succès.");
                Equal("AtlasAlpha", alphaState.GetProperty("folders")[0].GetString(),
                    "Les dossiers gérés doivent rester enregistrés explicitement.");
            }

            string unmanagedDirectory = Path.Combine(addonsDirectory, "PersonalNotes");
            Directory.CreateDirectory(unmanagedDirectory);
            await File.WriteAllTextAsync(Path.Combine(unmanagedDirectory, "keep.txt"), "user data");

            byte[] mainV2 = CreateArchive(new Dictionary<string, string>
            {
                ["AtlasAlpha/AtlasAlpha.toc"] = "## Interface: 30403\n",
                ["AtlasAlpha/core.lua"] = "return '2.0.0'\n"
            });
            AddonPackage alphaV2 = CreatePackage("alpha", "Alpha", "2.0.0", "AtlasAlpha", mainV2);
            AddonCatalog catalogV2 = CreateCatalog(alphaV2);
            using (MappedHttpHandler failingHandler = new())
            using (HttpClient failingHttp = new(failingHandler))
            {
                failingHandler.Failures[new Uri(alphaV2.Url)] = new HttpRequestException("network unavailable");
                await ThrowsAsync<HttpRequestException>(
                    () => AddonInstallServices.ApplySelectionAsync(
                        failingHttp,
                        catalogV2,
                        root,
                        new Dictionary<string, bool> { [alphaV2.Id] = true },
                        progress: null,
                        log: null,
                        CancellationToken.None),
                    "Une mise à jour réseau en échec doit remonter au coordinateur.");
            }

            IReadOnlyDictionary<string, AddonInspection> afterFailedUpdate =
                AddonInstallServices.Inspect(catalogV2, root);
            Equal(AddonLocalStatus.UpdateAvailable, afterFailedUpdate[alphaV2.Id].Status,
                "Une mise à jour échouée ne doit pas être déclarée installée.");
            Equal("1.0.0", afterFailedUpdate[alphaV2.Id].InstalledVersion,
                "La version précédente doit survivre à un échec avant application.");
            True((await File.ReadAllTextAsync(Path.Combine(alphaDirectory, "core.lua"))).Contains("1.0.0"),
                "Les fichiers précédents doivent rester en place après l'échec.");

            Directory.Delete(alphaDirectory, recursive: true);
            Equal(AddonLocalStatus.MissingFiles,
                AddonInstallServices.Inspect(catalogV1, root)[alphaV1.Id].Status,
                "Un dossier géré absent doit demander une réparation.");
            await AddonInstallServices.ApplySelectionAsync(
                http,
                CreateCatalog(alphaV1),
                root,
                new Dictionary<string, bool> { [alphaV1.Id] = true },
                progress: null,
                log: null,
                CancellationToken.None);
            Equal(AddonLocalStatus.Installed,
                AddonInstallServices.Inspect(CreateCatalog(alphaV1), root)[alphaV1.Id].Status,
                "La réparation doit réutiliser le même pipeline d'installation.");

            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            await ThrowsAsync<OperationCanceledException>(
                () => AddonInstallServices.ApplySelectionAsync(
                    http,
                    CreateCatalog(dependency),
                    root,
                    new Dictionary<string, bool> { [dependency.Id] = true },
                    progress: null,
                    log: null,
                    cancelled.Token),
                "Le token legacy doit interrompre une installation avant mutation.");
            True(!Directory.Exists(Path.Combine(addonsDirectory, "AtlasDependency")),
                "Une installation annulée ne doit pas publier un état partiel.");

            AddonPackage blockingPackage = CreateFakePackage(
                "blocking-download",
                "Téléchargement annulable");
            blockingPackage.Size = 100;
            blockingPackage.Sha256 = new string('c', 64);
            blockingPackage.InstallHash = blockingPackage.Sha256;
            using (BlockingDownloadHandler blockingHandler = new())
            using (HttpClient blockingHttp = new(blockingHandler))
            using (CancellationTokenSource downloadCancellation = new())
            {
                Task blockedInstall = AddonInstallServices.ApplySelectionAsync(
                    blockingHttp,
                    CreateCatalog(blockingPackage),
                    root,
                    new Dictionary<string, bool> { [blockingPackage.Id] = true },
                    progress: null,
                    log: null,
                    downloadCancellation.Token);
                await blockingHandler.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                downloadCancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(
                    () => blockedInstall,
                    "Une annulation pendant le téléchargement doit être observée par le pipeline legacy.");
            }
            True(!Directory.Exists(Path.Combine(addonsDirectory, blockingPackage.Folders[0])),
                "Une annulation en cours de téléchargement ne doit laisser aucun dossier géré.");

            await AddonInstallServices.ApplySelectionAsync(
                http,
                CreateCatalog(alphaV1),
                root,
                new Dictionary<string, bool> { [alphaV1.Id] = false },
                progress: null,
                log: null,
                CancellationToken.None);
            True(!Directory.Exists(alphaDirectory), "La suppression doit retirer le dossier géré.");
            True(File.Exists(Path.Combine(unmanagedDirectory, "keep.txt")),
                "La suppression ne doit jamais toucher un dossier utilisateur non géré.");
            Equal(AddonLocalStatus.NotInstalled,
                AddonInstallServices.Inspect(CreateCatalog(alphaV1), root)[alphaV1.Id].Status,
                "La suppression doit nettoyer uniquement l'entrée Atlas correspondante.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static async Task CharacterizeCatalogProjectionAsync()
    {
        AddonCatalog catalog = CreateCatalog(
            CreateFakePackage("zeta", "Zeta", "description finale"),
            CreateFakePackage("questie", "Alpha Questie", "Suivi des QUÊTES"),
            CreateFakePackage("unknown-logo", "Beta", "outil de combat"));
        FakeAddonManagementService service = new(catalog);
        service.SetInspection("zeta", Managed(AddonLocalStatus.Installed, "1.0.0"));
        service.SetInspection("questie", Managed(AddonLocalStatus.UpdateAvailable, "0.9.0"));
        service.SetInspection("unknown-logo", Unmanaged(AddonLocalStatus.NotInstalled));
        TaskCompletionSource loadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CatalogGate = loadGate;

        await using AddonsEnvironment environment = new(service, isGameRunning: false);
        AddonsCatalogStartResult first = environment.Coordinator.TryLoadCatalog();
        AddonsCatalogStartResult duplicate = environment.Coordinator.TryLoadCatalog();
        Equal(AddonsCatalogStartStatus.Started, first.Status,
            "Le premier chargement du catalogue doit démarrer.");
        Equal(AddonsCatalogStartStatus.Busy, duplicate.Status,
            "Un second chargement doit être refusé immédiatement, sans file.");
        loadGate.TrySetResult();
        await first.Completion!.WaitAsync(TimeSpan.FromSeconds(2));

        AddonsRuntimeSnapshot snapshot = environment.Coordinator.CurrentSnapshot;
        Equal(AddonsCatalogLoadState.Loaded, snapshot.LoadState,
            "Le catalogue chargé doit devenir la source runtime.");
        SequenceEqual(["Alpha Questie", "Beta", "Zeta"], snapshot.Items.Select(item => item.Name),
            "Le catalogue réel doit être trié alphabétiquement.");

        AddonsViewState projected = AddonsStateAdapter.Project(snapshot);
        True(projected.Catalog.Single(item => item.Id == "questie").HasOfficialIcon,
            "Un logo Atlas existant doit être utilisé.");
        True(!projected.Catalog.Single(item => item.Id == "unknown-logo").HasOfficialIcon,
            "Un addon sans ressource doit utiliser le fallback générique.");

        AddonsUiState state = new(projected);
        True(state.UpdateSearch("quêtes"), "La recherche locale doit être disponible.");
        Equal("questie", state.Current.VisibleAddons.Single().Id,
            "La recherche doit être insensible à la casse et couvrir la description.");
        state.UpdateSearch(string.Empty);
        state.SelectFilter(AddonCatalogFilter.Installed);
        Equal(2, state.Current.VisibleAddons.Length,
            "Installés doit provenir de l'état géré réel.");
        state.SelectFilter(AddonCatalogFilter.Updates);
        Equal(1, state.Current.VisibleAddons.Length,
            "Mises à jour doit utiliser le snapshot réel.");
        Equal(3, state.Current.TotalCount, "Le compteur Tous est incorrect.");
        Equal(2, state.Current.InstalledCount, "Le compteur Installés est incorrect.");
        Equal(1, state.Current.UpdateCount, "Le compteur Mises à jour est incorrect.");

        AddonsRuntimeSnapshot clientUnavailable = snapshot with
        {
            Sequence = snapshot.Sequence + 1,
            IsClientPlayable = false,
            CanMutate = false
        };
        True(AddonsStateAdapter.Project(clientUnavailable).NotificationMessage.Contains(
                "client WotLK",
                StringComparison.Ordinal),
            "Un client absent doit expliquer pourquoi les actions sont désactivées.");

        service.CatalogGate = null;
        service.NextCatalogFailure = new HttpRequestException("offline");
        AddonsCatalogStartResult refresh = environment.Coordinator.TryLoadCatalog(forceRefresh: true);
        await refresh.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
        AddonsRuntimeSnapshot stale = environment.Coordinator.CurrentSnapshot;
        True(stale.IsCatalogStale && stale.Items.Length == 3,
            "Un rafraîchissement réseau en échec doit conserver le catalogue connu.");
        Equal(
            "Le catalogue Atlas n’a pas pu être actualisé. Les informations affichées sont conservées localement.",
            AddonsStateAdapter.Project(stale).CatalogErrorMessage,
            "Le message de catalogue conservé doit rester celui validé visuellement.");

        environment.Session.SetAuthenticated(false);
        Equal(AddonsCatalogLoadState.SignedOut, environment.Coordinator.CurrentSnapshot.LoadState,
            "Une déconnexion doit rendre le catalogue non interactif sans effacer ses données.");
        environment.Session.SetAuthenticated(true);
        Equal(AddonsCatalogLoadState.Loaded, environment.Coordinator.CurrentSnapshot.LoadState,
            "Une reconnexion doit restaurer le catalogue mémoire comme source utilisable.");
        True(environment.Coordinator.CurrentSnapshot.CanMutate,
            "Les actions addon doivent redevenir disponibles après reconnexion.");

        FakeAddonManagementService emptyService = new(CreateCatalog());
        await using AddonsEnvironment empty = new(emptyService, isGameRunning: false);
        await LoadCatalogAsync(empty.Coordinator);
        AddonsUiState emptyState = new(AddonsStateAdapter.Project(empty.Coordinator.CurrentSnapshot));
        Equal("Aucun addon disponible", emptyState.Current.EmptyTitle,
            "Un vrai catalogue vide doit avoir un état distinct.");
        emptyState.ApplyRuntimeView(projected);
        emptyState.UpdateSearch("introuvable");
        True(emptyState.Current.EmptyTitle.Contains("introuvable", StringComparison.Ordinal),
            "Une recherche vide doit citer sa recherche.");
        emptyState.UpdateSearch(string.Empty);
        emptyState.SelectFilter(AddonCatalogFilter.Updates);
        True(emptyState.Current.EmptyTitle.Contains("filtre", StringComparison.OrdinalIgnoreCase),
            "Un filtre vide ne doit pas être confondu avec un catalogue vide.");
    }

    private static async Task CharacterizeRuntimeOperationsAsync()
    {
        AddonCatalog catalog = CreateCatalog(
            CreateFakePackage("install", "À installer"),
            CreateFakePackage("update", "À mettre à jour", version: "2.0.0"),
            CreateFakePackage("repair", "À réparer"),
            CreateFakePackage("remove", "À supprimer"));
        FakeAddonManagementService service = new(catalog);
        service.SetInspection("install", Unmanaged(AddonLocalStatus.NotInstalled));
        service.SetInspection("update", Managed(AddonLocalStatus.UpdateAvailable, "1.0.0"));
        service.SetInspection("repair", Managed(AddonLocalStatus.MissingFiles, "1.0.0"));
        service.SetInspection("remove", Managed(AddonLocalStatus.Installed, "1.0.0"));
        await using AddonsEnvironment environment = new(service, isGameRunning: true);
        await LoadCatalogAsync(environment.Coordinator);

        AddonsActionCompletion install = await CompleteAsync(
            environment.Coordinator.TryInvokePrimary("install"));
        Equal(AddonsActionCompletionStatus.Succeeded, install.Status,
            "Installer doit aboutir par le coordinateur V2.");
        Equal(AddonLocalStatus.Installed,
            environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "install").LocalStatus,
            "L'état local doit être relu sans refresh complet après installation.");
        Equal(AddonsNoticeKind.Installed, environment.Coordinator.CurrentSnapshot.Notice,
            "Le succès d'installation doit publier une seule notification.");
        True(AddonsStateAdapter.Project(environment.Coordinator.CurrentSnapshot)
                .NotificationMessage.Contains("/reload", StringComparison.Ordinal),
            "Une mutation réussie jeu ouvert doit conseiller /reload.");

        AddonsActionCompletion update = await CompleteAsync(
            environment.Coordinator.TryInvokePrimary("update"));
        Equal(AddonsActionCompletionStatus.Succeeded, update.Status,
            "Mettre à jour doit réutiliser le pipeline réel.");
        Equal("2.0.0",
            environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "update").InstalledVersion,
            "La version disponible ne doit devenir installée qu'après succès.");

        AddonsActionCompletion repair = await CompleteAsync(
            environment.Coordinator.TryInvokePrimary("repair"));
        Equal(AddonsActionCompletionStatus.Succeeded, repair.Status,
            "Réparer doit utiliser le même pipeline d'application.");
        Equal(AddonsNoticeKind.Repaired, environment.Coordinator.CurrentSnapshot.Notice,
            "La réparation doit publier son résultat dédié.");

        TaskCompletionSource removeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.ApplyGate = removeGate;
        AddonsActionStartResult removeStart = environment.Coordinator.TryRemove("remove");
        await service.WaitForApplyCountAsync(4);
        True(removeStart.IsStarted, "La suppression gérée doit démarrer.");
        Equal(AddonsCatalogStartStatus.Busy,
            environment.Coordinator.TryLoadCatalog().Status,
            "Revenir sur Addons ne doit pas masquer une opération active par une relecture locale.");
        Equal(AddonsCatalogStartStatus.Busy,
            environment.Coordinator.TryLoadCatalog(forceRefresh: true).Status,
            "Un rafraîchissement distant doit aussi être refusé pendant une mutation addon.");
        True(!environment.Coordinator.RefreshLocalState(),
            "Une relecture locale explicite doit préserver le snapshot actif.");
        True(!environment.Coordinator.CurrentSnapshot.CanCancel,
            "Annuler ne doit pas être affiché pendant la phase de suppression non annulable.");
        True(!environment.Coordinator.CancelCurrent(),
            "La suppression legacy ne doit pas prétendre accepter l'annulation utilisateur.");
        removeGate.TrySetResult();
        AddonsActionCompletion remove = await removeStart.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(AddonsActionCompletionStatus.Succeeded, remove.Status,
            "La suppression doit aboutir par le chemin legacy.");
        Equal(AddonLocalStatus.NotInstalled,
            environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "remove").LocalStatus,
            "L'addon supprimé doit redevenir non installé.");

        SequenceEqual(["install", "update", "repair", "remove"], service.AppliedAddonIds,
            "Chaque action unitaire doit envoyer un catalogue limité à l'addon ciblé.");
        True(service.AppliedCatalogSizes.All(size => size == 1),
            "La V2 ne doit jamais toucher implicitement les autres packages du catalogue.");
    }

    private static async Task CharacterizeFailuresCancellationAndShutdownAsync()
    {
        await CharacterizeItemFailureAsync(
            new HttpRequestException("download failed"),
            AddonsErrorCategory.Network);
        await CharacterizeItemFailureAsync(
            new UnauthorizedAccessException("denied"),
            AddonsErrorCategory.AccessDenied);
        await CharacterizeItemFailureAsync(
            new IOException("locked", 32),
            AddonsErrorCategory.FilesLocked);

        AddonCatalog cancelCatalog = CreateCatalog(
            CreateFakePackage("cancel", "Annulation"),
            CreateFakePackage("other", "Autre"));
        FakeAddonManagementService cancelService = new(cancelCatalog);
        cancelService.SetInspection("cancel", Unmanaged(AddonLocalStatus.NotInstalled));
        cancelService.SetInspection("other", Unmanaged(AddonLocalStatus.NotInstalled));
        TaskCompletionSource applyStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelService.ApplyBehavior = async (call, token) =>
        {
            call.Progress?.Report(new AddonTransferProgress(call.Package.Name, 10, 100));
            applyStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await using (AddonsEnvironment environment = new(cancelService, isGameRunning: false))
        {
            await LoadCatalogAsync(environment.Coordinator);
            AddonsActionStartResult start = environment.Coordinator.TryInvokePrimary("cancel");
            await applyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            AddonsActionStartResult parallel = environment.Coordinator.TryInvokePrimary("other");
            Equal(AddonsActionStartStatus.Busy, parallel.Status,
                "Une deuxième mutation addon doit être refusée immédiatement.");
            True(environment.Coordinator.CancelCurrent(),
                "Une installation doit relayer l'annulation réellement supportée.");
            True(!environment.Coordinator.CancelCurrent(),
                "Une double annulation doit rester idempotente.");
            AddonsActionCompletion completion = await start.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(AddonsActionCompletionStatus.Cancelled, completion.Status,
                "Une installation annulée doit revenir à son état local stable.");
            Equal(AddonLocalStatus.NotInstalled,
                environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "cancel").LocalStatus,
                "L'annulation ne doit pas inventer une installation.");
        }

        AddonCatalog unauthorizedCatalog = CreateCatalog(CreateFakePackage("secure", "Session"));
        FakeAddonManagementService unauthorizedService = new(unauthorizedCatalog)
        {
            NextCatalogFailure = new HttpRequestException(
                "unauthorized",
                inner: null,
                HttpStatusCode.Unauthorized)
        };
        FakeAddonsSessionContext unauthorizedSession = new(authenticated: true);
        await using (AddonsEnvironment environment = new(
                         unauthorizedService,
                         isGameRunning: false,
                         unauthorizedSession))
        {
            AddonsCatalogStartResult load = environment.Coordinator.TryLoadCatalog();
            await load.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(1, unauthorizedSession.UnauthorizedNotifications,
                "Un 401 addon doit invalider la session via le coordinateur central.");
            Equal(AddonsCatalogLoadState.Failed, environment.Coordinator.CurrentSnapshot.LoadState,
                "Sans catalogue connu, un 401 doit laisser un échec contrôlé.");
        }

        AddonCatalog progressCatalog = CreateCatalog(CreateFakePackage("progress", "Progression"));
        FakeAddonManagementService progressService = new(progressCatalog);
        progressService.SetInspection("progress", Unmanaged(AddonLocalStatus.NotInstalled));
        ManualTimeProvider clock = new();
        progressService.ApplyBehavior = (call, _) =>
        {
            call.Progress!.Report(new AddonTransferProgress(call.Package.Name, 10, 100));
            clock.Advance(TimeSpan.FromMilliseconds(20));
            call.Progress.Report(new AddonTransferProgress(call.Package.Name, 20, 100));
            clock.Advance(TimeSpan.FromMilliseconds(80));
            call.Progress.Report(new AddonTransferProgress(call.Package.Name, 60, 100));
            call.Progress.Report(new AddonTransferProgress(call.Package.Name, 100, 100));
            return Task.CompletedTask;
        };
        await using (AddonsEnvironment environment = new(
                         progressService,
                         isGameRunning: false,
                         timeProvider: clock))
        {
            await LoadCatalogAsync(environment.Coordinator);
            List<long> publishedBytes = [];
            environment.Coordinator.SnapshotChanged += (_, args) =>
            {
                if (args.Snapshot.Progress.BytesReceived is long bytes)
                {
                    publishedBytes.Add(bytes);
                }
            };
            await CompleteAsync(environment.Coordinator.TryInvokePrimary("progress"));
            SequenceEqual([10L, 60L, 100L], publishedBytes,
                "La progression doit être coalescée à 80 ms sans retarder la valeur terminale.");
        }

        AddonCatalog shutdownCatalog = CreateCatalog(CreateFakePackage("late", "Résultat tardif"));
        FakeAddonManagementService shutdownService = new(shutdownCatalog);
        shutdownService.SetInspection("late", Unmanaged(AddonLocalStatus.NotInstalled));
        TaskCompletionSource shutdownStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<AddonTransferProgress>? lateProgress = null;
        shutdownService.ApplyBehavior = async (call, token) =>
        {
            lateProgress = call.Progress;
            shutdownStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await using (AddonsEnvironment environment = new(shutdownService, isGameRunning: false))
        {
            await LoadCatalogAsync(environment.Coordinator);
            int eventCount = 0;
            environment.Coordinator.SnapshotChanged += (_, _) => eventCount++;
            AddonsActionStartResult start = environment.Coordinator.TryInvokePrimary("late");
            await shutdownStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            environment.Coordinator.BeginShutdown();
            AddonsActionCompletion completion = await start.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
            True(completion.Status is AddonsActionCompletionStatus.Cancelled
                    or AddonsActionCompletionStatus.Superseded,
                "La fermeture doit interrompre le pipeline même sans annulation utilisateur explicite.");
            int afterShutdown = eventCount;
            lateProgress!.Report(new AddonTransferProgress("Résultat tardif", 100, 100));
            await Task.Delay(30);
            Equal(afterShutdown, eventCount,
                "Un callback tardif ne doit jamais republier après la fermeture.");
        }
    }

    private static async Task CharacterizeItemFailureAsync(
        Exception failure,
        AddonsErrorCategory expectedCategory)
    {
        AddonCatalog catalog = CreateCatalog(
            CreateFakePackage("target", "Cible"),
            CreateFakePackage("healthy", "Sain"));
        FakeAddonManagementService service = new(catalog);
        service.SetInspection("target", Unmanaged(AddonLocalStatus.NotInstalled));
        service.SetInspection("healthy", Unmanaged(AddonLocalStatus.NotInstalled));
        service.NextApplyFailure = failure;
        await using AddonsEnvironment environment = new(service, isGameRunning: false);
        await LoadCatalogAsync(environment.Coordinator);
        AddonsActionCompletion completion = await CompleteAsync(
            environment.Coordinator.TryInvokePrimary("target"));
        Equal(AddonsActionCompletionStatus.Failed, completion.Status,
            "Une erreur d'addon doit produire un résultat contrôlé.");
        AddonRuntimeItem target = environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "target");
        AddonRuntimeItem healthy = environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "healthy");
        Equal(expectedCategory, target.ErrorCategory,
            "La catégorie d'erreur locale est incorrecte.");
        Equal(AddonsRequestedAction.Install, target.RetryAction,
            "Réessayer doit conserver exactement l'opération échouée.");
        Equal(AddonsErrorCategory.None, healthy.ErrorCategory,
            "Une erreur individuelle ne doit pas contaminer les autres lignes.");
        True(environment.Coordinator.CurrentSnapshot.CanMutate,
            "Les autres addons doivent redevenir utilisables après la libération du bail.");
    }

    private static async Task CharacterizeSequentialBatchAsync()
    {
        AddonCatalog catalog = CreateCatalog(
            CreateFakePackage("alpha", "Alpha", version: "2.0.0"),
            CreateFakePackage("beta", "Beta", version: "2.0.0"),
            CreateFakePackage("charlie", "Charlie", version: "2.0.0"));
        FakeAddonManagementService failureService = new(catalog);
        foreach (AddonPackage package in catalog.Addons)
        {
            failureService.SetInspection(package.Id, Managed(AddonLocalStatus.UpdateAvailable, "1.0.0"));
        }
        TaskCompletionSource failingSecondStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFailingSecond = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        failureService.ApplyBehavior = async (call, token) =>
        {
            if (call.Package.Id == "beta")
            {
                failingSecondStarted.TrySetResult();
                await releaseFailingSecond.Task.WaitAsync(token);
                throw new IOException("disk failure");
            }
        };

        await using (AddonsEnvironment environment = new(failureService, isGameRunning: false))
        {
            await LoadCatalogAsync(environment.Coordinator);
            AddonsUiState uiState = new(AddonsStateAdapter.Project(
                environment.Coordinator.CurrentSnapshot));
            uiState.SelectFilter(AddonCatalogFilter.Updates);
            environment.Coordinator.SnapshotChanged += (_, args) =>
                uiState.ApplyRuntimeView(AddonsStateAdapter.Project(args.Snapshot));
            AddonsActionStartResult start = environment.Coordinator.TryUpdateAll();
            await failingSecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(2, uiState.Current.UpdateCount,
                "Les compteurs doivent refléter immédiatement le premier succès du batch.");
            Equal(3, uiState.Current.VisibleAddons.Length,
                "Le filtre Mises à jour doit conserver les cibles visibles jusqu'à la fin du batch.");
            releaseFailingSecond.TrySetResult();
            AddonsActionCompletion completion = await CompleteAsync(start);
            Equal(AddonsActionCompletionStatus.Failed, completion.Status,
                "Le batch doit s'arrêter sur la première erreur.");
            SequenceEqual(["alpha", "beta"], failureService.AppliedAddonIds,
                "Tout mettre à jour doit être strictement séquentiel et ne pas poursuivre après erreur.");
            Equal(1, failureService.MaximumConcurrency,
                "Aucun téléchargement addon concurrent ne doit être introduit.");
            Equal(AddonLocalStatus.Installed,
                environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "alpha").LocalStatus,
                "Un succès antérieur du batch doit rester enregistré.");
            Equal(AddonsErrorCategory.Disk,
                environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "beta").ErrorCategory,
                "Seul l'addon ayant échoué doit porter l'erreur.");
            Equal(AddonLocalStatus.UpdateAvailable,
                environment.Coordinator.CurrentSnapshot.Items.Single(item => item.Id == "charlie").LocalStatus,
                "Les addons non encore traités doivent rester à mettre à jour.");
            Equal(2, uiState.Current.VisibleAddons.Length,
                "Le filtre doit être réappliqué dès la fin du batch.");
        }

        FakeAddonManagementService cancelService = new(catalog);
        foreach (AddonPackage package in catalog.Addons)
        {
            cancelService.SetInspection(package.Id, Managed(AddonLocalStatus.UpdateAvailable, "1.0.0"));
        }
        TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelService.ApplyBehavior = async (call, token) =>
        {
            if (call.Package.Id == "beta")
            {
                secondStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        await using (AddonsEnvironment environment = new(cancelService, isGameRunning: false))
        {
            await LoadCatalogAsync(environment.Coordinator);
            AddonsActionStartResult start = environment.Coordinator.TryUpdateAll();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            True(environment.Coordinator.CancelCurrent(),
                "Le batch doit partager une annulation globale réellement respectée.");
            AddonsActionCompletion completion = await start.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
            Equal(AddonsActionCompletionStatus.Cancelled, completion.Status,
                "Le batch annulé doit publier un résultat annulé.");
            SequenceEqual(["alpha", "beta"], cancelService.AppliedAddonIds,
                "L'annulation doit empêcher le démarrage de l'addon suivant.");
            Equal(1, cancelService.MaximumConcurrency,
                "L'annulation globale ne doit pas créer de branche parallèle.");
        }
    }

    private static void CharacterizeCompatibilityMatrix()
    {
        LauncherOperationKind[] maintenanceKinds =
        [
            LauncherOperationKind.Addons,
            LauncherOperationKind.GameInstall,
            LauncherOperationKind.GameUpdate,
            LauncherOperationKind.GameRepair,
            LauncherOperationKind.Verify,
            LauncherOperationKind.LauncherAutoUpdate
        ];

        foreach (LauncherOperationKind firstKind in maintenanceKinds)
        {
            using LauncherOperationCoordinator operations = new();
            LauncherOperationStartResult first = operations.TryBegin(firstKind, canUserCancel: true);
            True(first.IsStarted, $"Le bail initial {firstKind} doit démarrer dans le harnais.");
            LauncherOperationStartResult addons = operations.TryBegin(
                LauncherOperationKind.Addons,
                canUserCancel: true);
            Equal(LauncherOperationStartStatus.Busy, addons.Status,
                $"{firstKind} + Addons doit être refusé immédiatement, sans attente.");
            first.Lease!.Complete();
        }

        foreach (LauncherOperationKind secondKind in maintenanceKinds)
        {
            using LauncherOperationCoordinator operations = new();
            LauncherOperationLease addonLease = operations.TryBegin(
                LauncherOperationKind.Addons,
                canUserCancel: true).Lease!;
            LauncherOperationStartResult second = operations.TryBegin(secondKind, canUserCancel: true);
            Equal(LauncherOperationStartStatus.Busy, second.Status,
                $"Addons + {secondKind} doit être refusé immédiatement, sans file.");
            addonLease.Complete();
        }

        using (LauncherOperationCoordinator operations = new())
        {
            LauncherOperationLease play = operations.TryBeginPlay(clientIsPlayable: true).Lease!;
            Equal(LauncherOperationStartStatus.RejectedByCompatibility,
                operations.TryBegin(LauncherOperationKind.Addons, canUserCancel: true).Status,
                "Addons ne peut pas commencer pendant le single-flight Play.");
            play.Complete();
        }
        using (LauncherOperationCoordinator operations = new())
        {
            LauncherOperationLease addons = operations.TryBegin(
                LauncherOperationKind.Addons,
                canUserCancel: true).Lease!;
            Equal(LauncherOperationStartStatus.RejectedByCompatibility,
                operations.TryBeginPlay(clientIsPlayable: true).Status,
                "Play ne peut pas commencer pendant une mutation Addons.");
            addons.Complete();
        }
        using (LauncherOperationCoordinator operations = new())
        {
            LauncherOperationLease verify = operations.TryBegin(
                LauncherOperationKind.Verify,
                canUserCancel: false).Lease!;
            LauncherOperationLease? play = operations.TryBeginPlay(clientIsPlayable: true).Lease;
            if (play is null)
            {
                throw new InvalidOperationException(
                    "La compatibilité historique Play + vérification non mutante doit rester possible.");
            }
            Equal(LauncherOperationStartStatus.Busy,
                operations.TryBegin(LauncherOperationKind.Addons, canUserCancel: true).Status,
                "Addons reste incompatible avec Verify, même si Play coexiste avec Verify.");
            play.Complete();
            verify.Complete();
        }
    }

    private static void CharacterizePreviewIsolation()
    {
        Equal(LauncherStartupMode.Legacy, App.ResolveStartupMode([]),
            "Le lancement sans argument doit rester legacy.");
        Equal(LauncherStartupMode.UiV2, App.ResolveStartupMode(["--ui-v2"]),
            "La V2 réelle doit conserver sa branche dédiée.");
        Equal(LauncherStartupMode.UiV2AddonsPreview,
            App.ResolveStartupMode(["--ui-v2", "--preview-addons=default"]),
            "La preview Addons doit être sélectionnée avant le runtime réel.");
        Equal(LauncherStartupMode.InvalidArguments,
            App.ResolveStartupMode(["--preview-addons=default"]),
            "Une preview Addons sans --ui-v2 doit être refusée.");

        AddonsUiState preview = AddonsPreviewData.Create(AddonsPreviewScenario.Default);
        AddonsViewState before = preview.Current;
        True(preview.InvokePrimary(before.Catalog.First(item => item.CanInvokePrimary).Id),
            "La preview doit conserver ses transitions locales fictives.");
        True(preview.Current.IsPreview && !preview.Current.IsRuntimeConnected,
            "La preview ne doit recevoir aucun coordinateur réel.");
    }

    private static async Task ValidateRuntimeWpfAsync(string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() => RunWpfHarness(completion, captureDirectory))
        {
            IsBackground = true,
            Name = "AtlasAddonsRuntimeWpfHarness"
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
                await ValidateRuntimeWindowAsync(captureDirectory);
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

    private static async Task ValidateRuntimeWindowAsync(string? captureDirectory)
    {
        AddonCatalog catalog = CreateManyCatalog(50);
        FakeAddonManagementService service = new(catalog);
        for (int index = 0; index < catalog.Addons.Count; index++)
        {
            AddonPackage package = catalog.Addons[index];
            AddonInspection inspection = index switch
            {
                0 => Unmanaged(AddonLocalStatus.NotInstalled),
                1 => Managed(AddonLocalStatus.UpdateAvailable, "0.9.0"),
                2 => Managed(AddonLocalStatus.Installed, package.Version),
                3 => Managed(AddonLocalStatus.MissingFiles, package.Version),
                _ => index % 4 == 0
                    ? Managed(AddonLocalStatus.UpdateAvailable, "0.8.0")
                    : Unmanaged(AddonLocalStatus.NotInstalled)
            };
            service.SetInspection(package.Id, inspection);
        }

        FakeAddonsSessionContext session = new(authenticated: true);
        using LauncherOperationCoordinator operations = new();
        LauncherSettings settings = new() { InstallPath = "C:\\Atlas\\WotLK" };
        using LauncherAddonsCoordinator coordinator = new(
            service,
            session,
            operations,
            settings,
            _ => true,
            _ => true,
            _ => { },
            new ManualTimeProvider());
        AddonsUiState addonsState = new(AddonsStateAdapter.Project(coordinator.CurrentSnapshot));
        LauncherShellV2 window = new(
            LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true),
            LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
            addonsState,
            LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
            LauncherV2PreviewData.CreateFriends(),
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
        using AddonsCommands commands = new(
            coordinator,
            addonsState,
            window,
            () => settings.InstallPath,
            (_, _) => true);
        using AddonsStateAdapter adapter = new(addonsState, coordinator, window.Dispatcher);
        window.AttachAddons(commands);
        window.Show();
        try
        {
            await DelayAndPumpAsync(120);
            True(!window.IsPreviewMode && window.HasRealAddonsAttached,
                "Le harnais WPF doit utiliser la branche Addons réelle.");
            RaiseClick(Required<Button>(window, "AddonsNavigationButton"));
            await WaitForWpfAsync(() => addonsState.Current.TotalCount == 50);
            Equal(LauncherShellPage.Addons, window.CurrentPage,
                "La navigation réelle doit ouvrir AddonsViewV2.");
            Equal(50, window.AddonsPage.ListHost.Items.Count,
                "Le catalogue runtime doit être projeté dans la liste WPF.");
            True(VirtualizingPanel.GetIsVirtualizing(window.AddonsPage.ListHost),
                "La virtualisation doit rester active avec 50 addons.");
            True(window.AddonsPage.ListHost.ItemContainerGenerator.ContainerFromIndex(49) is null,
                "Une entrée hors viewport ne doit pas être matérialisée.");
            Equal(Visibility.Visible, Required<Border>(window.AddonsPage, "GameRunningBanner").Visibility,
                "Le jeu ouvert doit afficher le conseil /reload sans désactiver les actions.");

            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                SavePng(window, Path.Combine(captureDirectory, "01-addons-runtime-1440x860.png"));
            }

            window.Width = 1080;
            window.Height = 680;
            await DelayAndPumpAsync(180);
            Equal(AdaptiveLayoutMode.Stacked, window.ShellState.LayoutMode,
                "Addons doit utiliser l'état Stacked à 1080 DIPs.");
            Equal(ScrollBarVisibility.Disabled,
                ScrollViewer.GetHorizontalScrollBarVisibility(window.AddonsPage.ListHost),
                "Aucune barre horizontale ne doit apparaître à 1080 × 680.");
            Rect closeBounds = BoundsInAncestor(Required<Button>(window, "CloseWindowButton"), window);
            True(closeBounds.Right <= window.ActualWidth + 0.5,
                "Les commandes de fenêtre doivent rester accessibles à 1080 × 680.");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                SavePng(window, Path.Combine(captureDirectory, "02-addons-runtime-1080x680.png"));
            }
            window.Width = 1440;
            window.Height = 860;
            await DelayAndPumpAsync(180);

            window.AddonsPage.SearchBox.Text = "needle-49";
            await PumpAsync(DispatcherPriority.DataBind);
            Equal(1, addonsState.Current.VisibleAddons.Length,
                "La recherche WPF réelle doit rester locale et immédiate.");
            window.AddonsPage.SearchBox.Text = string.Empty;
            RaiseClick(Required<Button>(window.AddonsPage, "UpdatesFilterButton"));
            await PumpAsync(DispatcherPriority.DataBind);
            True(addonsState.Current.VisibleAddons.All(item => item.NeedsUpdate),
                "Le filtre WPF Mises à jour doit suivre le snapshot réel.");
            RaiseClick(Required<Button>(window.AddonsPage, "AllFilterButton"));
            await PumpAsync(DispatcherPriority.DataBind);

            AddonUiItem installItem = addonsState.Current.Catalog.Single(item => item.Id == catalog.Addons[0].Id);
            window.AddonsPage.ListHost.SelectedItem = installItem;
            await PumpAsync(DispatcherPriority.Input);
            True(window.AddonsPage.IsDetailOpen,
                "Le panneau détail doit s'ouvrir avec les données runtime.");

            TaskCompletionSource installGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource installStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            service.ApplyBehavior = async (call, token) =>
            {
                call.Progress?.Report(new AddonTransferProgress(call.Package.Name, 25, 100));
                installStarted.TrySetResult();
                await installGate.Task.WaitAsync(token);
            };
            True(addonsState.InvokePrimary(installItem.Id),
                "Le bouton Installer WPF doit déléguer à la commande réelle.");
            await installStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForWpfAsync(() =>
                addonsState.Current.Catalog.Single(item => item.Id == installItem.Id).ProgressPercent == 25d);
            AddonUiItem installing = addonsState.Current.Catalog.Single(item => item.Id == installItem.Id);
            Equal(AddonVisualState.Installing, installing.VisualState,
                "La ligne réelle doit afficher la phase Installation.");
            Equal(25d, installing.ProgressPercent,
                "La progression WPF doit afficher uniquement le pourcentage réel.");
            installGate.TrySetResult();
            await WaitForWpfAsync(() =>
                addonsState.Current.Catalog.Single(item => item.Id == installItem.Id).VisualState
                    == AddonVisualState.Installed);

            AddonUiItem updateItem = addonsState.Current.Catalog.Single(item => item.Id == catalog.Addons[1].Id);
            service.ApplyBehavior = null;
            True(addonsState.InvokePrimary(updateItem.Id),
                "Le bouton Mettre à jour WPF doit être réel.");
            await WaitForWpfAsync(() =>
                addonsState.Current.Catalog.Single(item => item.Id == updateItem.Id).VisualState
                    == AddonVisualState.Installed);

            AddonUiItem removeItem = addonsState.Current.Catalog.Single(item => item.Id == catalog.Addons[2].Id);
            addonsState.OpenDetails(removeItem.Id);
            True(addonsState.RequestRemoveSelected() && addonsState.Current.IsDeleteConfirmationOpen,
                "La suppression réelle doit conserver la confirmation validée.");
            True(addonsState.ConfirmRemove(),
                "La confirmation doit déléguer à la suppression legacy.");
            await WaitForWpfAsync(() =>
                addonsState.Current.Catalog.Single(item => item.Id == removeItem.Id).VisualState
                    == AddonVisualState.NotInstalled);

            AddonUiItem repairItem = addonsState.Current.Catalog.Single(item => item.Id == catalog.Addons[3].Id);
            service.NextApplyFailure = new IOException("disk error");
            addonsState.OpenDetails(repairItem.Id);
            True(addonsState.InvokePrimary(repairItem.Id),
                "Réparer doit rester réservé au détail lorsqu'il est pertinent.");
            await WaitForWpfAsync(() =>
                addonsState.Current.Catalog.Single(item => item.Id == repairItem.Id).VisualState
                    == AddonVisualState.Error);
            AddonUiItem failedRepair = addonsState.Current.Catalog.Single(item => item.Id == repairItem.Id);
            Equal(AddonPrimaryActionKind.Repair, failedRepair.EffectivePrimaryAction,
                "Réessayer après une réparation ne doit pas devenir une installation.");
            True(addonsState.Current.Catalog.Where(item => item.Id != repairItem.Id)
                    .All(item => item.VisualState != AddonVisualState.Error),
                "Une erreur WPF individuelle ne doit pas masquer le catalogue.");

        }
        finally
        {
            window.Close();
            coordinator.BeginShutdown();
            await coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            await PumpAsync(DispatcherPriority.Background);
        }
    }

    private static async Task LoadCatalogAsync(LauncherAddonsCoordinator coordinator)
    {
        AddonsCatalogStartResult start = coordinator.TryLoadCatalog();
        True(start.IsStarted, $"Le catalogue de test n'a pas démarré ({start.Status}).");
        await start.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(AddonsCatalogLoadState.Loaded, coordinator.CurrentSnapshot.LoadState,
            "Le catalogue de test doit être chargé.");
    }

    private static async Task<AddonsActionCompletion> CompleteAsync(AddonsActionStartResult start)
    {
        True(start.IsStarted, $"L'action addon n'a pas démarré ({start.Status}).");
        return await start.Completion!.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static AddonCatalog CreateManyCatalog(int count)
    {
        List<AddonPackage> packages = [];
        for (int index = 0; index < count; index++)
        {
            string id = index == 0 ? "questie" : $"addon-{index:00}";
            packages.Add(CreateFakePackage(
                id,
                $"Addon {index:00}",
                $"Description locale needle-{index:00}",
                version: "1.0.0"));
        }
        return CreateCatalog([.. packages]);
    }

    private static AddonCatalog CreateCatalog(params AddonPackage[] packages) => new()
    {
        SchemaVersion = 1,
        ClientInterface = AddonInstallServices.SupportedInterface,
        Addons = [.. packages]
    };

    private static AddonPackage CreateFakePackage(
        string id,
        string name,
        string description = "Description de test",
        string version = "1.0.0") => new()
    {
        Id = id,
        Name = name,
        Description = description,
        Category = id.Length % 2 == 0 ? "Combat" : "Interface",
        Version = version,
        Interface = AddonInstallServices.SupportedInterface,
        Url = $"https://atlas.test/{id}.zip",
        Size = 1,
        Sha256 = new string('a', 64),
        InstallHash = new string('b', 64),
        Folders = [$"Atlas{id.Replace('-', '_')}"]
    };

    private static AddonPackage CreatePackage(
        string id,
        string name,
        string version,
        string folder,
        byte[] archive,
        IReadOnlyList<string>? dependencies = null,
        IReadOnlyList<AddonPackageComponent>? components = null,
        Dictionary<string, string>? replacements = null) => new()
    {
        Id = id,
        Name = name,
        Description = $"Package {name}",
        Category = "Test",
        Version = version,
        Interface = AddonInstallServices.SupportedInterface,
        Url = $"https://atlas.test/{id}-{version}.zip",
        Size = archive.Length,
        Sha256 = Hash(archive),
        InstallHash = Hash(archive),
        Folders = [folder],
        Dependencies = dependencies is null ? [] : [.. dependencies],
        Components = components is null ? [] : [.. components],
        TokenReplacements = replacements ?? new Dictionary<string, string>(StringComparer.Ordinal)
    };

    private static AddonPackageComponent CreateComponent(
        string name,
        string url,
        byte[] archive) => new()
    {
        Name = name,
        Url = url,
        Size = archive.Length,
        Sha256 = Hash(archive)
    };

    private static byte[] CreateArchive(IReadOnlyDictionary<string, string> entries)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using StreamWriter writer = new(
                    entry.Open(),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: false);
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private static AddonInspection Managed(
        AddonLocalStatus status,
        string version,
        string folder = "ManagedFolder") => new(
        status,
        IsManaged: true,
        InstalledVersion: version,
        InstalledSha256: new string('b', 64),
        InstalledFolders: [folder],
        InstalledAtUtc: DateTimeOffset.Parse("2026-09-01T12:00:00Z"));

    private static AddonInspection Unmanaged(AddonLocalStatus status) =>
        new(status, IsManaged: false);

    private static string CreatePlayableClientRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "AtlasAddonsRuntimeTests",
            Guid.NewGuid().ToString("N"));
        string classic = GameInstallServices.GetClassicDirectoryPath(root);
        Directory.CreateDirectory(classic);
        File.WriteAllBytes(GameInstallServices.GetGameExecutablePath(root), []);
        File.WriteAllBytes(GameInstallServices.GetGameLauncherPath(root), []);
        return root;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "current", "addons", "catalog.json")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Le dépôt contenant current/addons/catalog.json est introuvable.");
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
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

    private static Rect BoundsInAncestor(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static T Required<T>(FrameworkElement root, string name)
        where T : FrameworkElement =>
        root.FindName(name) as T
        ?? throw new InvalidOperationException($"Le contrôle WPF {name} est absent.");

    private static void RaiseClick(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static async Task WaitForWpfAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Le scénario WPF Addons n'a pas atteint l'état attendu.");
            }
            await DelayAndPumpAsync(15);
        }
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

    private static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string message)
    {
        T[] expectedArray = [.. expected];
        T[] actualArray = [.. actual];
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new InvalidOperationException(
                $"{message} Attendu=[{string.Join(", ", expectedArray)}]; "
                + $"Actuel=[{string.Join(", ", actualArray)}].");
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class MappedHttpHandler : HttpMessageHandler
    {
        internal Dictionary<Uri, byte[]> Responses { get; } = [];

        internal Dictionary<Uri, Exception> Failures { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri
                ?? throw new InvalidOperationException("URI de test absente.");
            if (Failures.TryGetValue(uri, out Exception? failure))
            {
                return Task.FromException<HttpResponseMessage>(failure);
            }
            if (!Responses.TryGetValue(uri, out byte[]? payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.LongLength;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingDownloadHandler : HttpMessageHandler
    {
        internal TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream(ReadStarted))
            };
            response.Content.Headers.ContentLength = 100;
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingReadStream(TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => 100;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAddonManagementService : IAddonManagementService
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, AddonInspection> _inspections =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _appliedAddonIds = [];
        private readonly List<int> _appliedCatalogSizes = [];
        private int _activeCalls;
        private int _applyCalls;

        internal FakeAddonManagementService(AddonCatalog catalog)
        {
            Catalog = catalog;
        }

        internal AddonCatalog Catalog { get; set; }

        internal TaskCompletionSource? CatalogGate { get; set; }

        internal TaskCompletionSource? ApplyGate { get; set; }

        internal Exception? NextCatalogFailure { get; set; }

        internal Exception? NextApplyFailure { get; set; }

        internal Func<FakeApplyCall, CancellationToken, Task>? ApplyBehavior { get; set; }

        internal int LoadCalls { get; private set; }

        internal int MaximumConcurrency { get; private set; }

        internal IReadOnlyList<string> AppliedAddonIds
        {
            get
            {
                lock (_sync)
                {
                    return _appliedAddonIds.ToArray();
                }
            }
        }

        internal IReadOnlyList<int> AppliedCatalogSizes
        {
            get
            {
                lock (_sync)
                {
                    return _appliedCatalogSizes.ToArray();
                }
            }
        }

        internal void SetInspection(string addonId, AddonInspection inspection)
        {
            lock (_sync)
            {
                _inspections[addonId] = inspection;
            }
        }

        public async Task<AddonCatalog> LoadCatalogAsync(CancellationToken cancellationToken)
        {
            LoadCalls++;
            if (CatalogGate is not null)
            {
                await CatalogGate.Task.WaitAsync(cancellationToken);
            }
            Exception? failure;
            lock (_sync)
            {
                failure = NextCatalogFailure;
                NextCatalogFailure = null;
            }
            if (failure is not null)
            {
                throw failure;
            }
            return Catalog;
        }

        public IReadOnlyDictionary<string, AddonInspection> Inspect(
            AddonCatalog catalog,
            string installRoot)
        {
            lock (_sync)
            {
                return catalog.Addons.ToDictionary(
                    package => package.Id,
                    package => _inspections.TryGetValue(package.Id, out AddonInspection? inspection)
                        ? inspection
                        : Unmanaged(AddonLocalStatus.NotInstalled),
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public async Task ApplySelectionAsync(
            AddonCatalog catalog,
            string installRoot,
            IReadOnlyDictionary<string, bool> selection,
            IProgress<AddonTransferProgress>? progress,
            Action<string>? log,
            CancellationToken cancellationToken)
        {
            Equal(1, catalog.Addons.Count,
                "Le service fake attend le même catalogue unitaire que le pipeline réel V2.");
            AddonPackage package = catalog.Addons.Single();
            bool selected = selection.TryGetValue(package.Id, out bool value) && value;
            int callIndex;
            lock (_sync)
            {
                _appliedAddonIds.Add(package.Id);
                _appliedCatalogSizes.Add(catalog.Addons.Count);
                callIndex = ++_applyCalls;
                _activeCalls++;
                MaximumConcurrency = Math.Max(MaximumConcurrency, _activeCalls);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ApplyGate is not null)
                {
                    await ApplyGate.Task.WaitAsync(cancellationToken);
                }
                Exception? failure;
                lock (_sync)
                {
                    failure = NextApplyFailure;
                    NextApplyFailure = null;
                }
                if (failure is not null)
                {
                    throw failure;
                }

                FakeApplyCall call = new(
                    callIndex,
                    package,
                    selected,
                    progress,
                    log);
                if (ApplyBehavior is not null)
                {
                    await ApplyBehavior(call, cancellationToken);
                }
                else if (selected)
                {
                    progress?.Report(new AddonTransferProgress(package.Name, 100, 100));
                }

                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    _inspections[package.Id] = selected
                        ? new AddonInspection(
                            AddonLocalStatus.Installed,
                            IsManaged: true,
                            InstalledVersion: package.Version,
                            InstalledSha256: package.EffectiveInstallHash,
                            InstalledFolders: package.Folders.ToArray(),
                            InstalledAtUtc: DateTimeOffset.Parse("2026-09-02T12:00:00Z"))
                        : Unmanaged(AddonLocalStatus.NotInstalled);
                }
            }
            finally
            {
                lock (_sync)
                {
                    _activeCalls--;
                }
            }
        }

        internal async Task WaitForApplyCountAsync(int count)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (Volatile.Read(ref _applyCalls) < count)
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new TimeoutException("Le nombre d'appels addon attendu n'a pas été atteint.");
                }
                await Task.Delay(5);
            }
        }
    }

    private sealed record FakeApplyCall(
        int Index,
        AddonPackage Package,
        bool Selected,
        IProgress<AddonTransferProgress>? Progress,
        Action<string>? Log);

    private sealed class FakeAddonsSessionContext : IAddonsSessionContext
    {
        private AuthSessionSnapshot _snapshot;

        internal FakeAddonsSessionContext(bool authenticated)
        {
            _snapshot = CreateSnapshot(authenticated, sequence: 1);
        }

        public event EventHandler<AuthSessionSnapshotEventArgs>? SnapshotChanged;

        public AuthSessionSnapshot CurrentSnapshot => _snapshot;

        internal AtlasRequestPreparationStatus PreparationStatus { get; set; } =
            AtlasRequestPreparationStatus.Ready;

        internal int PreparationCalls { get; private set; }

        internal int UnauthorizedNotifications { get; private set; }

        public Task<AtlasRequestPreparationStatus> PrepareAuthenticatedRequestAsync(
            CancellationToken cancellationToken)
        {
            PreparationCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(PreparationStatus);
        }

        public void NotifyAuthenticatedRequestUnauthorized()
        {
            UnauthorizedNotifications++;
        }

        internal void SetAuthenticated(bool authenticated)
        {
            _snapshot = CreateSnapshot(authenticated, _snapshot.Sequence + 1);
            SnapshotChanged?.Invoke(this, new AuthSessionSnapshotEventArgs(_snapshot));
        }

        private static AuthSessionSnapshot CreateSnapshot(bool authenticated, long sequence) => new(
            Sequence: sequence,
            AttemptId: null,
            State: authenticated
                ? LauncherSessionState.Authenticated
                : LauncherSessionState.SignedOut,
            OperationKind: null,
            Username: authenticated ? "Dono1402" : string.Empty,
            IsEmailVerified: true,
            FailureCategory: LauncherSessionFailureCategory.None);
    }

    private sealed class AddonsEnvironment : IAsyncDisposable
    {
        private readonly LauncherOperationCoordinator _operations;

        internal AddonsEnvironment(
            FakeAddonManagementService service,
            bool isGameRunning,
            FakeAddonsSessionContext? session = null,
            TimeProvider? timeProvider = null)
        {
            Service = service;
            Session = session ?? new FakeAddonsSessionContext(authenticated: true);
            _operations = new LauncherOperationCoordinator();
            Settings = new LauncherSettings { InstallPath = "C:\\Atlas\\WotLK" };
            Coordinator = new LauncherAddonsCoordinator(
                service,
                Session,
                _operations,
                Settings,
                _ => true,
                _ => isGameRunning,
                _ => { },
                timeProvider ?? TimeProvider.System);
        }

        internal FakeAddonManagementService Service { get; }

        internal FakeAddonsSessionContext Session { get; }

        internal LauncherSettings Settings { get; }

        internal LauncherAddonsCoordinator Coordinator { get; }

        public async ValueTask DisposeAsync()
        {
            Coordinator.BeginShutdown();
            await Coordinator.WaitForIdleAsync(TimeSpan.FromSeconds(2));
            Coordinator.Dispose();
            _operations.Dispose();
        }
    }
}
