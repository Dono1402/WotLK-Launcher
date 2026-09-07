using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using WotLK.Launcher;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Commands;
using WotLK.Launcher.UI.V2.Presentation;

internal static class AddonsLibraryCommandTests
{
    internal static async Task<int> RunAsync()
    {
        TaskCompletionSource<int> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            _ = ExecuteAsync();
            Dispatcher.Run();
            async Task ExecuteAsync()
            {
                string temporary = Path.Combine(Path.GetTempPath(), "atlas-addon-commands-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temporary);
                int checks = 0;
                using LauncherOperationCoordinator operations = new();
                FakeService service = new();
                FakeSession session = new();
                LauncherSettings settings = new() { InstallPath = Path.Combine(temporary, "game") };
                using LauncherAddonsCoordinator runtime = new(service, session, operations, settings, _ => true, _ => false, _ => { });
                Window owner = new(); // Remains unshown: no native window, tray icon or desktop input.
                try
                {
                    AddonsCatalogStartResult load = runtime.TryLoadCatalog();
                    await load.Completion!;
                    AddonsUiState state = new(AddonsStateAdapter.Project(runtime.CurrentSnapshot));
                    FakeDialogs dialogs = new();
                    MemoryStore preferences = new();
                    int writableChecks = 0;
                    using AddonsCommands commands = new(runtime, state, owner, () => settings.InstallPath,
                        (_, _) => { writableChecks++; return true; }, dialogs, preferences);
                    using AddonsStateAdapter adapter = new(state, runtime, dispatcher);

                    dialogs.Confirm = _ => false;
                    state.PrimaryCommand.Execute("feature");
                    Check(dialogs.Plans.Count == 1 && dialogs.Plans[0].Items.Select(item => item.Id).SequenceEqual(new[] { "base", "feature" }),
                        "The actual command presents dependencies in their installation order.");
                    Check(service.Applied.Count == 0 && writableChecks == 0, "Cancelling a plan does not request write access or install anything.");

                    string originalRoot = settings.InstallPath;
                    dialogs.Confirm = _ => { settings.InstallPath = Path.Combine(temporary, "other-game"); return true; };
                    state.PrimaryCommand.Execute("feature");
                    Check(service.Applied.Count == 0 && writableChecks == 0, "A changed game root invalidates an already displayed plan.");
                    settings.InstallPath = originalRoot;
                    dialogs.Confirm = _ => true;
                    state.PrimaryCommand.Execute("feature");
                    await Idle();
                    Check(service.Applied.Select(item => item.Id).SequenceEqual(new[] { "base", "feature" }), "Confirmed installation executes dependencies before the selected addon.");
                    Check(service.Applied.All(item => !item.AllowExternal) && writableChecks == 1, "A dependency plan grants no consent to replace unrelated manual installations.");

                    int appliedBefore = service.Applied.Count;
                    dialogs.Confirm = _ => false;
                    state.PrimaryCommand.Execute("manual");
                    Check(dialogs.Plans[^1].RequiresExternalReplacementConfirmation && service.Applied.Count == appliedBefore,
                        "A detected manual addon requires a concrete replacement confirmation.");
                    dialogs.Confirm = _ => true;
                    state.PrimaryCommand.Execute("manual");
                    await Idle();
                    Check(service.Applied[^1].Id == "manual" && service.Applied[^1].AllowExternal,
                        "Explicit adoption consent reaches the exact package operation.");

                    int writesBeforeVerify = writableChecks, authBeforeVerify = session.PreparationCalls;
                    state.VerifyCommand.Execute("healthy");
                    await Idle();
                    Check(service.Verified.SequenceEqual(new[] { "healthy" }), "The Verify command invokes the read-only verifier.");
                    Check(writableChecks == writesBeforeVerify && session.PreparationCalls == authBeforeVerify,
                        "Local verification neither asks for write permissions nor refreshes remote authentication.");
                    Check(state.Current.Catalog.Single(item => item.Id == "healthy").IsVerified, "A completed verification result reaches the UI projection.");

                    state.ReinstallCommand.Execute("healthy");
                    await Idle();
                    Check(service.Applied[^1].Id == "healthy" && service.Applied[^1].Forced, "Reinstall forces replacement even for an addon already at the current version.");

                    appliedBefore = service.Applied.Count;
                    int writesBeforeRemove = writableChecks;
                    state.RemoveCommand.Execute("base");
                    Check(service.Applied.Count == appliedBefore && writableChecks == writesBeforeRemove,
                        "Removing a dependency in use is rejected before any write permission or file operation.");

                    service.RemoveFailuresRemaining = 1;
                    appliedBefore = service.Applied.Count;
                    state.RemoveCommand.Execute("removable");
                    await Idle();
                    Check(runtime.CurrentSnapshot.PendingRetryAction == AddonsRequestedAction.Remove
                        && state.Current.CanRetryFailed && service.RemoveAttempts.SequenceEqual(new[] { "removable" }),
                        "A failed removal preserves a removal retry instead of changing the operation to installation.");
                    int plansBeforeRemovalRetry = dialogs.Plans.Count;
                    int writesBeforeRemovalRetry = writableChecks;
                    state.RetryFailedCommand.Execute(null);
                    await Idle();
                    Check(dialogs.Plans.Count == plansBeforeRemovalRetry,
                        "Retrying a removal never displays an installation plan, even when the removed addon has a missing dependency.");
                    Check(service.Applied.Count == appliedBefore
                        && !runtime.CurrentSnapshot.Items.Single(item => item.Id == "remove-dependency").IsInstalled,
                        "A removal retry does not install the removed addon's missing dependencies.");
                    Check(service.Removed.SequenceEqual(new[] { "removable" })
                        && service.RemoveAttempts.SequenceEqual(new[] { "removable", "removable" })
                        && !runtime.CurrentSnapshot.Items.Single(item => item.Id == "removable").IsManaged
                        && !state.Current.CanRetryFailed && writableChecks == writesBeforeRemovalRetry + 1,
                        "The removal retry requests write access and completes the original removal successfully.");

                    state.SetSelected("feature", true);
                    state.SetSelected("healthy", true);
                    dialogs.ExportPath = Path.Combine(temporary, "selection.json");
                    state.ExportProfileCommand.Execute(null);
                    AddonSelectionProfile exported = AddonSelectionJson.Import(File.ReadAllText(dialogs.ExportPath));
                    Check(exported.AddonIds.SequenceEqual(new[] { "feature", "healthy" }), "Export contains exactly the selected catalogue IDs.");
                    string exportedJson = File.ReadAllText(dialogs.ExportPath);
                    Check(!exportedJson.Contains(temporary, StringComparison.OrdinalIgnoreCase)
                        && !exportedJson.Contains("sha256", StringComparison.OrdinalIgnoreCase), "A profile export contains no game paths, installed files or credentials.");
                    state.ClearSelection();
                    dialogs.ImportPath = dialogs.ExportPath;
                    appliedBefore = service.Applied.Count;
                    state.ImportProfileCommand.Execute(null);
                    Check(state.SelectedAddonIds.SequenceEqual(exported.AddonIds) && service.Applied.Count == appliedBefore,
                        "Import selects components for review without starting an installation.");
                    File.WriteAllText(dialogs.ImportPath, "{\"format\":\"atlas-addon-selection\",\"version\":1,\"name\":\"Invalid\",\"addonIds\":[\"../outside\"]}");
                    state.ImportProfileCommand.Execute(null);
                    Check(state.SelectedAddonIds.SequenceEqual(exported.AddonIds), "An invalid import preserves the current selection and performs no writes to addons.");

                    settings.InstallPath = Path.Combine(temporary, "second-game");
                    commands.RefreshCatalog();
                    await Idle();
                    Check(state.SelectedAddonIds.IsEmpty && preferences.LoadedRoots.Count >= 2, "Changing the game folder loads separate library preferences and clears the old selection.");
                    Check(new System.Windows.Interop.WindowInteropHelper(owner).Handle == IntPtr.Zero, "All command tests leave the owner without a native window.");
                    commands.Dispose();
                    Check(!state.VerifyCommand.CanExecute("healthy") && !state.InstallSelectionCommand.CanExecute(null), "Disposed commands cannot start new work.");
                    Console.WriteLine($"Addons library commands PASS: {checks} assertions; in-memory service, unshown owner, fake dialogs and disposable JSON files only.");
                    result.TrySetResult(0);

                    async Task Idle()
                    {
                        await runtime.WaitForIdleAsync(TimeSpan.FromSeconds(5));
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    }
                    void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
                }
                catch (Exception error) { Console.Error.WriteLine(error); result.TrySetResult(1); }
                finally
                {
                    runtime.BeginShutdown();
                    await runtime.WaitForIdleAsync(TimeSpan.FromSeconds(5));
                    string safeRoot = Path.GetFullPath(temporary);
                    string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                    if (safeRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                        && Path.GetFileName(safeRoot).StartsWith("atlas-addon-commands-", StringComparison.Ordinal)
                        && Directory.Exists(safeRoot)) Directory.Delete(safeRoot, recursive: true);
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        }) { IsBackground = true, Name = "AtlasAddonCommandFixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await result.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private sealed class FakeDialogs : IAddonLibraryDialogs
    {
        internal Func<AddonsPlanPreview, bool> Confirm { get; set; } = _ => false;
        internal List<AddonsPlanPreview> Plans { get; } = [];
        internal string? ImportPath { get; set; }
        internal string? ExportPath { get; set; }
        public bool ConfirmInstall(Window owner, AddonsPlanPreview plan) { Plans.Add(plan); return Confirm(plan); }
        public string? OpenProfile(Window owner) => ImportPath;
        public string? SaveProfile(Window owner) => ExportPath;
    }

    private sealed class MemoryStore : IAddonLibraryStore
    {
        internal List<string> LoadedRoots { get; } = [];
        public AddonLibraryPreferences Load(string clientRoot) { LoadedRoots.Add(clientRoot); return AddonLibraryPreferences.Empty; }
        public void Save(string clientRoot, AddonLibraryPreferences preferences) { }
    }

    private sealed class FakeService : IAddonManagementService
    {
        private readonly AddonCatalog _catalog = new()
        {
            SchemaVersion = 1, ClientInterface = "30403",
            Addons = [Package("base"), Package("feature", "base"), Package("manual"), Package("healthy"),
                Package("remove-dependency"), Package("removable", "remove-dependency")]
        };
        private readonly Dictionary<string, AddonInspection> _installed = new(StringComparer.OrdinalIgnoreCase)
        {
            ["manual"] = new(AddonLocalStatus.DetectedUnmanaged, false),
            ["healthy"] = new(AddonLocalStatus.Installed, true, "1.0", new string('a', 64), ["healthy"], DateTimeOffset.UtcNow),
            ["removable"] = new(AddonLocalStatus.Installed, true, "1.0", new string('a', 64), ["removable"], DateTimeOffset.UtcNow)
        };
        internal List<(string Id, bool Forced, bool AllowExternal)> Applied { get; } = [];
        internal List<string> Verified { get; } = [];
        internal List<string> RemoveAttempts { get; } = [];
        internal List<string> Removed { get; } = [];
        internal int RemoveFailuresRemaining { get; set; }
        public Task<AddonCatalog> LoadCatalogAsync(CancellationToken cancellationToken) => Task.FromResult(_catalog);
        public IReadOnlyDictionary<string, AddonInspection> Inspect(AddonCatalog catalog, string installRoot) =>
            catalog.Addons.ToDictionary(item => item.Id, item => _installed.GetValueOrDefault(item.Id) ?? new AddonInspection(AddonLocalStatus.NotInstalled, false), StringComparer.OrdinalIgnoreCase);
        public Task ApplySelectionAsync(AddonCatalog catalog, string installRoot, IReadOnlyDictionary<string, bool> selection,
            IProgress<AddonTransferProgress>? progress, Action<string>? log, CancellationToken cancellationToken) => throw new InvalidOperationException("The command must invoke explicit package operations.");
        public Task ApplyPackageAsync(AddonCatalog catalog, string installRoot, AddonPackage package, bool install, bool forceReinstall,
            bool allowExternalReplacement, IProgress<AddonTransferProgress>? progress, Action<string>? log, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!install)
            {
                RemoveAttempts.Add(package.Id);
                if (RemoveFailuresRemaining > 0)
                {
                    RemoveFailuresRemaining--;
                    throw new IOException("Synthetic removal failure.");
                }
                _installed.Remove(package.Id);
                Removed.Add(package.Id);
                return Task.CompletedTask;
            }
            Applied.Add((package.Id, forceReinstall, allowExternalReplacement));
            _installed[package.Id] = new(AddonLocalStatus.Installed, true, package.Version, package.Sha256, package.Folders, DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        }
        public Task<AddonVerificationResult> VerifyAsync(AddonCatalog catalog, string installRoot, string addonId, CancellationToken cancellationToken)
        {
            Verified.Add(addonId);
            return Task.FromResult(new AddonVerificationResult(addonId, AddonVerificationStatus.Verified, 2, [], [], [], DateTimeOffset.UtcNow));
        }
        private static AddonPackage Package(string id, params string[] dependencies) => new()
        {
            Id = id, Name = id, Description = "Synthetic addon", Category = "Interface", Version = "1.0", Interface = "30403",
            Folders = [id], Dependencies = dependencies.ToList(), Sha256 = new string('a', 64), Url = "https://fixture.invalid/" + id + ".zip", Size = 1
        };
    }

    private sealed class FakeSession : IAddonsSessionContext
    {
        public event EventHandler<AuthSessionSnapshotEventArgs>? SnapshotChanged { add { } remove { } }
        public AuthSessionSnapshot CurrentSnapshot { get; } = new(1, null, LauncherSessionState.Authenticated, null, "SyntheticAccount", true, LauncherSessionFailureCategory.None);
        internal int PreparationCalls { get; private set; }
        public Task<AtlasRequestPreparationStatus> PrepareAuthenticatedRequestAsync(CancellationToken cancellationToken)
        { PreparationCalls++; cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(AtlasRequestPreparationStatus.Ready); }
        public void NotifyAuthenticatedRequestUnauthorized() { }
    }
}
