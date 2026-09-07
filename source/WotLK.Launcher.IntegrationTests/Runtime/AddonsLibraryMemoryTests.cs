using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using WotLK.Launcher.UI.V2.Presentation;

internal static class AddonsLibraryMemoryTests
{
    internal static int Run()
    {
        int checks = 0;
        AddonsUiState? state = null;
        try
        {
            ImmutableArray<AddonUiItem> catalog =
            [
                Item("questie", "Questie", "Quêtes"),
                Item("leatrix-plus", "Leatrix Plus", "Interface"),
                Item("atlaslootclassic", "AtlasLoot", "Donjons") with { VisualState = AddonVisualState.UpdateAvailable, IsManagedByAtlas = true, InstalledVersion = "1.0", AvailableVersion = "2.0", InstalledAtUtc = DateTimeOffset.Parse("2026-09-01T12:00:00Z") },
                Item("details", "Details", "Combat") with { VisualState = AddonVisualState.Installed, IsManagedByAtlas = true, InstalledVersion = "2.0", InstalledAtUtc = DateTimeOffset.Parse("2026-09-06T12:00:00Z") },
                Item("dbm", "Deadly Boss Mods", "Combat") with { VisualState = AddonVisualState.Installing },
                Item("known-manual", "Known Manual", "Interface") with { IsDetectedUnmanaged = true },
                Item("unlisted", "Unlisted Manual", "Manuels") with { IsCatalogEntry = false, IsDetectedUnmanaged = true }
            ];
            AddonsViewState initial = AddonsUiState.EmptyView with { Catalog = catalog, VisibleAddons = catalog, IsRuntimeConnected = true, CanMutate = true };
            state = new(initial);
            MemoryStore store = new();
            string firstRoot = Path.Combine(Path.GetTempPath(), "atlas-addon-state-fixture-a");
            string secondRoot = Path.Combine(Path.GetTempPath(), "atlas-addon-state-fixture-b");
            state.ConfigureLibraryStore(store, firstRoot);
            Check(store.LoadCalls == 1 && state.Current.Catalog.Length == catalog.Length, "Preferences configure without changing the catalogue.");
            Check(state.Current.UpdateCount == 1 && state.Current.ShowsUpdateAll && state.Current.CanUpdateAll,
                "A single update keeps the bulk action available.");

            Check(state.ToggleFavorite("QUESTIE") && state.Current.FavoriteCount == 1, "Favorite IDs are case insensitive.");
            Check(store.SaveCalls == 1 && store.LastSaved!.FavoriteIds.SequenceEqual(new[] { "QUESTIE" }), "Favorites are persisted through the injected store.");
            Check(!state.ToggleFavorite("unlisted") && !state.ToggleFavorite("missing"), "An unknown manual addon or absent ID cannot be favorited.");
            Check(state.SelectFilter(AddonCatalogFilter.Favorites) && Visible("questie"), "The favorites filter uses the current saved choices.");
            state.ApplyRuntimeView(initial);
            Check(Visible("questie") && state.Current.FavoriteCount == 1, "A runtime snapshot preserves the active favorite filter and annotations.");
            state.SelectFilter(AddonCatalogFilter.All);
            state.SelectSort(AddonSortOrder.FavoritesFirst);
            Check(state.Current.VisibleAddons[0].Id == "questie", "Favorite-first sorting promotes the chosen item.");
            state.SelectSort(AddonSortOrder.UpdatesFirst);
            Check(state.Current.VisibleAddons[0].Id == "atlaslootclassic", "Update-first sorting promotes the pending update.");
            state.SelectSort(AddonSortOrder.RecentlyInstalled);
            Check(state.Current.VisibleAddons[0].Id == "details", "Recent installation sorting uses the recorded installation date.");
            Check(state.SelectCategory("combat") && Visible("details", "dbm"), "Category matching is case insensitive and combines with sorting.");
            state.UpdateSearch("boss");
            Check(Visible("dbm"), "Search composes with category filtering.");
            Check(!state.SelectCategory("missing-category") && state.Current.CategoryFilter == "combat", "An unavailable category leaves the filter unchanged.");
            Check(!state.SelectSort((AddonSortOrder)999) && state.Current.SortOrder == AddonSortOrder.RecentlyInstalled, "An invalid sort order is rejected.");
            state.ApplyRuntimeView(initial);
            Check(Visible("dbm") && state.Current.SearchText == "boss" && state.Current.CategoryFilter == "combat",
                "Runtime updates preserve search, category and sorting.");
            state.UpdateSearch("");
            state.SelectCategory("");
            state.SelectFilter(AddonCatalogFilter.Manual);
            Check(Visible("known-manual", "unlisted") && state.Current.ManualCount == 2, "The manual filter includes recognized and unlisted installations.");
            Check(state.Current.VisibleAddons.All(item => item.IsInstalled && !item.CanRemove && !item.CanVerify),
                "Manual rows do not offer managed removal or verification.");
            Check(state.Current.Catalog.Single(item => item.Id == "known-manual").CanReinstall
                && !state.Current.Catalog.Single(item => item.Id == "unlisted").CanReinstall,
                "Only a manual installation matched to the catalogue may request explicit reinstallation.");
            Check(!state.SetSelected("unlisted", true) && !state.SetSelected("dbm", true), "Unlisted and currently busy addons cannot be selected manually.");
            state.SelectVisibleAddons();
            Check(state.SelectedAddonIds.SequenceEqual(new[] { "known-manual" }), "Select-visible skips unlisted manual addons.");
            state.ClearSelection();

            CountingCommand commands = new();
            state.AttachLibraryCommands(commands, commands, commands, commands, commands, commands);
            Check(state.ApplyPack("starter") && state.IsLibraryOpen, "A pack opens component selection.");
            Check(state.SelectedAddonIds.SequenceEqual(new[] { "atlaslootclassic", "leatrix-plus", "questie" }), "The starter pack selects its available component IDs.");
            Check(state.Current.Filter == AddonCatalogFilter.All && state.Current.CategoryFilter.Length == 0 && state.Current.SearchText.Length == 0,
                "Applying a pack resets filters so its components can be reviewed.");
            Check(commands.Calls == 0, "Applying a pack starts no install, maintenance or file command.");
            Check(state.SetSelected("leatrix-plus", false) && state.SelectedAddonIds.Length == 2, "A component can be deselected before installation.");
            state.ProfileName = "  Quêtes perso  ";
            Check(state.CanSaveProfile && state.SaveProfile(), "A named component selection can be saved.");
            Check(state.Profiles.Length == 1 && state.Profiles[0].Name == "Quêtes perso" && state.HasSelectedProfile, "Profile names are trimmed and the saved profile becomes selected.");
            state.ClearSelection();
            Check(!state.CanSaveProfile && !state.CanInstallSelection, "Empty selections disable save and install.");
            Check(state.LoadProfile("QUÊTES PERSO") && state.SelectedAddonIds.SequenceEqual(new[] { "atlaslootclassic", "questie" }), "Profile lookup is case insensitive and restores the exact saved choices.");
            state.SetSelected("questie", false);
            state.ProfileName = "QUÊTES PERSO";
            Check(state.SaveProfile() && state.Profiles.Length == 1 && state.Profiles[0].AddonIds.SequenceEqual(new[] { "atlaslootclassic" }),
                "Saving an existing profile replaces its selection without duplicate names.");

            string exported = state.ExportSelectionJson("Sélection de test");
            AddonSelectionProfile imported = AddonSelectionJson.Import(exported);
            Check(imported.Name == "Sélection de test" && imported.AddonIds.SequenceEqual(new[] { "atlaslootclassic" }), "JSON export and import preserve the selected IDs and profile name.");
            using (JsonDocument document = JsonDocument.Parse(exported))
                Check(document.RootElement.EnumerateObject().Select(property => property.Name)
                    .Order().SequenceEqual(new[] { "addonIds", "format", "name", "version" }),
                    "Exports contain only the versioned selection, with no game settings, addon files or paths.");
            string partialJson = Selection("Partiel", "QUESTIE", "questie", "absent-addon");
            Check(state.ImportSelectionJson(partialJson) && state.SelectedAddonIds.SequenceEqual(new[] { "questie" })
                && state.Current.NotificationMessage.Contains("1 composant", StringComparison.Ordinal),
                "Import deduplicates case variants, keeps available components and reports missing ones.");
            Check(!state.ImportSelectionJson(Selection("Absent", "missing-only")) && state.SelectedAddonIds.SequenceEqual(new[] { "questie" }),
                "An import with no available component preserves the existing selection.");
            Check(!state.ImportSelectionJson(Selection("Manual", "unlisted")) && state.SelectedAddonIds.SequenceEqual(new[] { "questie" }),
                "An import cannot select a manually discovered addon outside the catalogue.");
            Check(!state.ImportSelectionJson(Selection("Invalid", "../outside")) && state.SelectedAddonIds.SequenceEqual(new[] { "questie" }),
                "Malformed identifiers are rejected without changing selections.");
            Check(commands.Calls == 0, "Saving, loading, importing and exporting selections starts no addon operation.");

            foreach (string invalid in new[]
            {
                "", "{}", "null", "{not-json}",
                "{\"format\":\"atlas-addon-selection\",\"version\":2,\"name\":\"Test\",\"addonIds\":[]}",
                Selection("", "questie"), Selection(new string('x', 61), "questie"), Selection("line\nbreak", "questie"),
                Selection("Test", "bad/id"), Selection("Test", "bad\\id"), Selection("Test", "bad id"), Selection("Test", ""),
                Selection("Test", new string('x', 101)), Selection("Test", Enumerable.Range(0, 257).Select(i => "addon-" + i).ToArray()),
                new string(' ', AddonSelectionJson.MaximumJsonLength + 1)
            })
            {
                bool rejected = false;
                try { AddonSelectionJson.Import(invalid); }
                catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException) { rejected = true; }
                Check(rejected, "Invalid selection documents fail closed.");
            }

            state.ConfigureLibraryStore(store, firstRoot.ToLowerInvariant() + Path.DirectorySeparatorChar);
            Check(store.LoadCalls == 1 && state.HasSelection, "Equivalent client paths retain the current library without an unnecessary reload.");
            state.ConfigureLibraryStore(store, secondRoot);
            Check(store.LoadCalls == 2 && !state.HasSelection && !state.HasProfiles && state.Current.FavoriteCount == 0,
                "A different client path loads an isolated library and clears the previous selection.");
            state.ConfigureLibraryStore(store, firstRoot);
            Check(store.LoadCalls == 3 && state.Current.FavoriteCount == 1 && state.HasProfiles, "Returning to the first client restores its favorites and named profiles.");
            Check(state.RemoveProfile("quêtes perso") && !state.HasProfiles, "Removing a profile persists that removal.");
            Check(!state.RemoveProfile("missing") && !state.LoadProfile("missing") && !state.ApplyPack("missing"), "Unknown choices perform no mutation.");

            state.SetSelected("questie", true);
            state.IsLibraryOpen = true;
            state.ApplyRuntimeView(AddonsUiState.EmptyView);
            Check(!state.HasSelection && !state.IsLibraryOpen, "Signing out clears transient component selection and closes the library.");
            state.ApplyRuntimeView(initial);
            state.SetSelected("questie", true);
            state.ApplyRuntimeView(initial with { IsCatalogLoading = true, Catalog = [], VisibleAddons = [] });
            Check(state.HasSelection, "Transient catalogue loading retains the user's component choices.");
            state.ApplyRuntimeView(initial with { Catalog = catalog.Where(item => item.Id != "questie").ToImmutableArray() });
            Check(!state.HasSelection, "A loaded catalogue removes vanished component IDs from the current selection.");
            state.ApplyRuntimeView(initial);
            state.SetSelected("questie", true);
            state.ApplyRuntimeView(initial with { Catalog = [], VisibleAddons = [] });
            Check(!state.HasSelection, "A successfully loaded empty catalogue clears obsolete selection IDs as well.");

            AddonsViewState running = initial with { IsBatchOperation = true, CanCancelCurrent = true, CanMutate = false, ActiveAddonId = "questie", ActiveAddonPosition = 2, ActiveAddonTotal = 3 };
            Check(running.ShowsBatchProgress && running.BatchProgressLabel.Contains("2 sur 3", StringComparison.Ordinal)
                && running.BatchProgressLabel.Contains("Questie", StringComparison.Ordinal), "Batch progress presents the current item and stable total.");
            Check((running with { Catalog = catalog.Select(item => item with { VisualState = AddonVisualState.Installed }).ToImmutableArray() }).ShowsUpdateAll,
                "The batch action remains visible even when the last update leaves the update list.");
            Check((initial with { CanRetryFailed = true }).ShowsBatchProgress, "A partial failure keeps the retry area visible after work ends.");
            AddonUiItem compatible = Item("compatibility", "Compatibility", "Interface");
            Check(compatible.CompatibilitySummary == "Interface compatible" && !compatible.IsAtlasValidated,
                "An interface match alone never claims in-game validation.");
            Check((compatible with { AtlasValidationEvidence = "Checked by fixture", KnownLimitations = "Limited legacy API" }).CompatibilityHint.Contains("Limited legacy API", StringComparison.Ordinal),
                "Compatibility tooltips retain evidence and explicit limitations.");

            MemoryStore failingStore = new() { FailLoad = true };
            state.ApplyRuntimeView(initial);
            state.ConfigureLibraryStore(failingStore, firstRoot);
            Check(state.Current.ShowsNotification && !state.HasProfiles, "Unreadable preferences degrade to an empty library with a visible notice.");
            failingStore.FailLoad = false;
            failingStore.FailSave = true;
            state.ToggleFavorite("questie");
            Check(state.Current.FavoriteCount == 1 && state.Current.NotificationMessage.Contains("enregistrement local a échoué", StringComparison.Ordinal),
                "A failed preference save keeps the in-memory favorite and explains that persistence failed.");

            ValidateJsonStore(Check, initial);
            Console.WriteLine($"Addons library memory PASS: {checks} assertions; in-memory state, fake store and disposable JSON preference fixture, no windows and no actual game or user preference files accessed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }

        void Check(bool condition, string message) { checks++; if (!condition) throw new InvalidOperationException(message); }
        bool Visible(params string[] ids) => state!.Current.VisibleAddons.Select(item => item.Id)
            .Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(ids.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static AddonUiItem Item(string id, string name, string category) => new(id, name, "Description " + name, category,
        "2.0", "", "30403", "Fixture author", [], [], "", false, AddonVisualState.NotInstalled, null, false, "");

    private static string Selection(string name, params string[] ids) => JsonSerializer.Serialize(new
    { format = "atlas-addon-selection", version = 1, name, addonIds = ids });

    private static void ValidateJsonStore(Action<bool, string> check, AddonsViewState initial)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "atlas-addon-library-json-" + Guid.NewGuid().ToString("N"));
        string storageDirectory = Path.Combine(temporary, "preferences");
        string firstClient = Path.Combine(temporary, "client-a-never-created");
        string secondClient = Path.Combine(temporary, "client-b-never-created");
        try
        {
            JsonAddonLibraryStore store = new(storageDirectory);
            AddonLibraryPreferences firstPreferences = new(["QUESTIE", "questie", "details"],
                [new("Quêtes perso", ["questie", "atlaslootclassic"])]);
            store.Save(firstClient, firstPreferences);
            string firstFile = Directory.EnumerateFiles(storageDirectory, "*.json").Single();
            AddonLibraryPreferences loaded = store.Load(firstClient.ToUpperInvariant() + Path.DirectorySeparatorChar);
            check(loaded.FavoriteIds.SequenceEqual(new[] { "details", "questie" }) && loaded.Profiles.Length == 1
                && loaded.Profiles[0].Name == "Quêtes perso"
                && loaded.Profiles[0].AddonIds.SequenceEqual(new[] { "atlaslootclassic", "questie" }),
                "The real JSON store persists normalized favorites and profiles across equivalent client path spelling.");
            check(Path.GetFileNameWithoutExtension(firstFile).Length == 64 && Path.GetFileName(firstFile).EndsWith(".json", StringComparison.Ordinal),
                "The preference filename uses a digest instead of exposing the client path.");

            AddonLibraryPreferences secondPreferences = new(["dbm"], [new("Raids", ["dbm", "details"])]);
            store.Save(secondClient, secondPreferences);
            check(store.Load(secondClient).FavoriteIds.SequenceEqual(new[] { "dbm" })
                && store.Load(firstClient).FavoriteIds.SequenceEqual(new[] { "details", "questie" })
                && Directory.EnumerateFiles(storageDirectory, "*.json").Count() == 2,
                "Two client roots retain separate preference files without overwriting each other.");
            foreach (string file in Directory.EnumerateFiles(storageDirectory, "*.json"))
            {
                string json = File.ReadAllText(file);
                using JsonDocument document = JsonDocument.Parse(json);
                check(!json.Contains("client-a-never-created", StringComparison.OrdinalIgnoreCase)
                    && !json.Contains("client-b-never-created", StringComparison.OrdinalIgnoreCase)
                    && document.RootElement.EnumerateObject().Select(property => property.Name).Order()
                        .SequenceEqual(new[] { "favoriteIds", "profiles", "version" }),
                    "Preference JSON contains only favorites and named selections, with no client path or game settings.");
            }
            store.Save(firstClient, new(["leatrix-plus"], []));
            check(store.Load(firstClient).FavoriteIds.SequenceEqual(new[] { "leatrix-plus" })
                && store.Load(firstClient).Profiles.IsEmpty
                && !Directory.EnumerateFiles(storageDirectory, "*.tmp").Any(),
                "Replacing preferences reloads the final complete document and leaves no temporary file.");

            File.WriteAllText(firstFile, "{\"version\":1,\"favoriteIds\":[\"questie\"],\"profiles\":[null,{\"name\":\"Kept\",\"addonIds\":[\"questie\"]}]}");
            loaded = store.Load(firstClient);
            check(loaded.Profiles.Length == 1 && loaded.Profiles[0].Name == "Kept", "A null profile entry does not prevent loading intact profiles.");
            File.WriteAllText(firstFile, "{broken-json}");
            AddonsUiState safeState = new(initial);
            safeState.ConfigureLibraryStore(store, firstClient);
            check(safeState.Current.Catalog.Length == initial.Catalog.Length && safeState.Current.ShowsNotification
                && safeState.Current.FavoriteCount == 0 && !safeState.HasProfiles,
                "Malformed preference JSON produces a visible load notice without losing the addon catalogue or exposing partial profiles.");
            check(!Directory.Exists(firstClient) && !Directory.Exists(secondClient), "No fixture operation creates or accesses a game folder.");
        }
        finally
        {
            string resolved = Path.GetFullPath(temporary);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("atlas-addon-library-json-", StringComparison.Ordinal))
                throw new InvalidOperationException("The JSON fixture cleanup path is outside its designated temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
        check(!Directory.Exists(temporary), "The disposable preference fixture is removed after verification.");
    }

    private sealed class CountingCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        internal int Calls { get; private set; }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => Calls++;
    }

    private sealed class MemoryStore : IAddonLibraryStore
    {
        private readonly Dictionary<string, AddonLibraryPreferences> _values = new(StringComparer.OrdinalIgnoreCase);
        internal int LoadCalls { get; private set; }
        internal int SaveCalls { get; private set; }
        internal AddonLibraryPreferences? LastSaved { get; private set; }
        internal bool FailLoad { get; set; }
        internal bool FailSave { get; set; }
        public AddonLibraryPreferences Load(string clientRoot)
        {
            LoadCalls++;
            if (FailLoad) throw new IOException("Synthetic preference read failure.");
            return _values.GetValueOrDefault(clientRoot) ?? AddonLibraryPreferences.Empty;
        }
        public void Save(string clientRoot, AddonLibraryPreferences preferences)
        {
            SaveCalls++;
            if (FailSave) throw new IOException("Synthetic preference write failure.");
            _values[clientRoot] = LastSaved = preferences;
        }
    }
}
