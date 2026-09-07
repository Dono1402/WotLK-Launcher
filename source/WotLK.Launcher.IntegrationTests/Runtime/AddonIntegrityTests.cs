using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WotLK.Launcher;

internal static class AddonIntegrityTests
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        await IntegrityAsync();
        await PreflightAsync();
        await DependentsAsync();
        await CatalogValidationAsync();
        await LinksAsync();
        Console.WriteLine($"Addon integrity PASS: {checks} synthetic ZIP/HTTP/temp-directory assertions. No client or external network was accessed.");
        return checks;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
        async Task ExpectAsync<T>(Func<Task> operation, string message) where T : Exception
        {
            try { await operation(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        async Task PlanErrorAsync(string code, Func<Task> operation, string[] ids, string message)
        {
            try { await operation(); }
            catch (AddonPlanException error)
            {
                Check(error.Code == code && ids.All(id => error.AddonIds.Contains(id, StringComparer.OrdinalIgnoreCase)), message);
                return;
            }
            throw new InvalidOperationException(message);
        }
        async Task IntegrityAsync()
        {
            using Fixture fixture = new();
            AddonPackage package = fixture.Package("integrity", "IntegrityAddon");
            AddonCatalog catalog = Catalog(package);
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.NotInstalled, "An absent addon cannot claim verification.");
            await fixture.Apply(catalog, new() { [package.Id] = true });
            AddonVerificationResult verified = await fixture.Verify(catalog, package);
            Check(verified.Status == AddonVerificationStatus.Verified && verified.CheckedFiles == 4,
                "A validated install persists and verifies a SHA-256 manifest for every installed file.");
            Check(AddonInstallServices.Inspect(catalog, fixture.Root)[package.Id].HasFileManifest,
                "Inspection reports the presence of a usable manifest separately from verification.");
            string folder = Path.Combine(fixture.Addons, "IntegrityAddon");
            File.Delete(Path.Combine(folder, "notes.txt"));
            File.WriteAllText(Path.Combine(folder, "core.lua"), "return 2", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "extra.lua"), "local extra = true");
            AddonVerificationResult damaged = await fixture.Verify(catalog, package);
            Check(damaged.Status == AddonVerificationStatus.NeedsRepair && damaged.CheckedFiles == 3,
                "Missing, modified and additional files prevent a verified result.");
            Check(damaged.MissingFiles.SequenceEqual(["IntegrityAddon/notes.txt"])
                && damaged.ModifiedFiles.SequenceEqual(["IntegrityAddon/core.lua"])
                && damaged.UnexpectedFiles.SequenceEqual(["IntegrityAddon/extra.lua"]),
                "Verification distinguishes missing files, same-size SHA changes and unexpected files.");
            int requests = fixture.Handler.Requests.Count;
            await fixture.Apply(catalog, new() { [package.Id] = true });
            Check(fixture.Handler.Requests.Count == requests && File.Exists(Path.Combine(folder, "extra.lua")),
                "An ordinary current-version operation does not silently masquerade as a forced reinstall.");
            await fixture.Apply(catalog, new() { [package.Id] = true }, force: true);
            Check(fixture.Handler.Requests.Count == requests + 1 && File.ReadAllText(Path.Combine(folder, "core.lua")) == "return 1"
                && File.Exists(Path.Combine(folder, "notes.txt")) && !File.Exists(Path.Combine(folder, "extra.lua")),
                "Forced reinstall downloads the reference again and restores the complete declared folder.");
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.Verified,
                "Reinstall creates a fresh valid file manifest.");
            string referenceState = File.ReadAllText(fixture.StatePath);
            JsonObject legacy = JsonNode.Parse(referenceState)!.AsObject();
            legacy["addons"]![package.Id]!.AsObject().Remove("files");
            File.WriteAllText(fixture.StatePath, legacy.ToJsonString());
            Check(!AddonInstallServices.Inspect(catalog, fixture.Root)[package.Id].HasFileManifest
                && (await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.LegacyUnverified,
                "Schema-1 legacy installations without file references remain explicitly unverified.");
            await fixture.Apply(catalog, new() { [package.Id] = true }, force: true);
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.Verified,
                "A forced reinstall upgrades a legacy installation to an actual verifiable manifest.");
            JsonObject invalid = JsonNode.Parse(referenceState)!.AsObject();
            invalid["addons"]![package.Id]!["files"] = new JsonObject
            {
                ["../outside.txt"] = new JsonObject { ["size"] = 1, ["sha256"] = new string('a', 64) }
            };
            File.WriteAllText(fixture.StatePath, invalid.ToJsonString());
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.LegacyUnverified,
                "A malformed traversal manifest is rejected before reading any referenced path.");
            invalid["addons"]![package.Id]!["files"] = new JsonObject
            {
                ["IntegrityAddon/a.lua"] = new JsonObject { ["size"] = 2L * 1024 * 1024 * 1024, ["sha256"] = new string('a', 64) },
                ["IntegrityAddon/b.lua"] = new JsonObject { ["size"] = 1, ["sha256"] = new string('a', 64) }
            };
            File.WriteAllText(fixture.StatePath, invalid.ToJsonString());
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.LegacyUnverified,
                "A file manifest cannot exceed the total supported expanded size.");
            JsonObject unknownSchema = JsonNode.Parse(referenceState)!.AsObject(); unknownSchema["schemaVersion"] = 2;
            File.WriteAllText(fixture.StatePath, unknownSchema.ToJsonString());
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.Unmanaged,
                "An unsupported state schema cannot grant ownership or verified status.");
            File.WriteAllText(fixture.StatePath, referenceState);
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => AddonInstallServices.VerifyAsync(catalog, fixture.Root, package.Id, cancelled.Token),
                "A cancelled verification must stop.");
        }
        async Task PreflightAsync()
        {
            using Fixture fixture = new();
            AddonPackage first = fixture.Package("first", "FirstAddon"), second = fixture.Package("second", "SecondAddon"), third = fixture.Package("third", "ThirdAddon"), remove = fixture.Package("remove", "RemoveAddon");
            AddonCatalog catalog = Catalog(remove, first, second, third);
            AddonInstallServices.ValidatePlan(catalog, fixture.Root, ["first"], [], false);
            Check(!Directory.Exists(fixture.Addons) && fixture.Handler.Requests.Count == 0,
                "Read-only plan validation never creates the AddOns directory or sends a download.");
            await fixture.Apply(catalog, new() { [remove.Id] = true });
            fixture.Manual("SecondAddon", "## Interface: 30403\n## Title: External second\n", "manual second");
            fixture.Manual("ThirdAddon", "## Interface: 30403\n## Title: External third\n", "manual third");
            string state = File.ReadAllText(fixture.StatePath);
            int requests = fixture.Handler.Requests.Count;
            Dictionary<string, bool> selection = new() { [remove.Id] = false, [first.Id] = true, [second.Id] = true, [third.Id] = true };
            await PlanErrorAsync("external-replacement-required", () => fixture.Apply(catalog, selection), ["second", "third"],
                "All externally installed replacements are reported before any package starts.");
            Check(fixture.Handler.Requests.Count == requests && Directory.Exists(Path.Combine(fixture.Addons, "RemoveAddon"))
                && !Directory.Exists(Path.Combine(fixture.Addons, "FirstAddon")) && File.ReadAllText(fixture.StatePath) == state,
                "External consent failure leaves earlier removals, installs and persisted state untouched.");
            Check(File.ReadAllText(Path.Combine(fixture.Addons, "SecondAddon", "core.lua")) == "manual second"
                && File.ReadAllText(Path.Combine(fixture.Addons, "ThirdAddon", "core.lua")) == "manual third",
                "Every external folder remains unchanged while consent is missing.");
            await PlanErrorAsync("external-replacement-required", () =>
            {
                AddonInstallServices.ValidatePlan(catalog, fixture.Root, [first.Id, second.Id, third.Id], [remove.Id], false);
                return Task.CompletedTask;
            }, [second.Id, third.Id], "The coordinator's read-only preflight enforces the same all-package consent boundary.");
            await fixture.Apply(catalog, selection, allowExternal: true);
            Check(!Directory.Exists(Path.Combine(fixture.Addons, "RemoveAddon")) && (await fixture.Verify(catalog, second)).Status == AddonVerificationStatus.Verified
                && (await fixture.Verify(catalog, third)).Status == AddonVerificationStatus.Verified,
                "Explicit consent allows replacements and records their new managed reference files.");
        }
        async Task DependentsAsync()
        {
            using Fixture fixture = new();
            AddonPackage core = fixture.Package("core", "AtlasCore"), widget = fixture.Package("widget", "KnownWidget"); widget.Dependencies = [core.Id];
            AddonCatalog catalog = Catalog(core, widget);
            await fixture.Apply(catalog, new() { [core.Id] = true });
            fixture.Manual("KnownWidget", "## Interface: 30403\n## Title: Known external\n", "external");
            await PlanErrorAsync("dependency-in-use", () => fixture.Apply(catalog, new() { [core.Id] = false }), [widget.Id],
                "A known but externally installed dependent prevents removal of its managed dependency.");
            await PlanErrorAsync("dependency-in-use", () => fixture.Apply(catalog, new() { [core.Id] = false, [widget.Id] = false }), [widget.Id],
                "Selecting an unmanaged dependent for removal cannot pretend that its external folder was removed.");
            fixture.RemoveFolder("KnownWidget");
            fixture.Manual("ManualBridge", "## Interface: 30403\n## RequiredDeps: atlascore\n", "bridge");
            fixture.Manual("ManualUI", "## Interface: 30403\n## Dependencies: ManualBridge\n## OptionalDeps: AtlasCore\n", "ui");
            int requests = fixture.Handler.Requests.Count;
            string state = File.ReadAllText(fixture.StatePath);
            await PlanErrorAsync("dependency-in-use", () => fixture.Apply(catalog, new() { [core.Id] = false }), ["manual:manualbridge", "manual:manualui"],
                "TOC folder-name dependencies block removal transitively and case-insensitively for unknown addons.");
            Check(fixture.Handler.Requests.Count == requests && File.ReadAllText(fixture.StatePath) == state && Directory.Exists(Path.Combine(fixture.Addons, "AtlasCore")),
                "Dependency refusal happens before downloads, deletion and state writes.");
            fixture.RemoveFolder("ManualUI"); fixture.RemoveFolder("ManualBridge");
            fixture.Manual("Cosmetic", "## Interface: 30403\n## OptionalDeps: AtlasCore\n", "cosmetic");
            await fixture.Apply(catalog, new() { [core.Id] = false });
            Check(!Directory.Exists(Path.Combine(fixture.Addons, "AtlasCore")) && Directory.Exists(Path.Combine(fixture.Addons, "Cosmetic")),
                "Optional dependencies do not block removal and unrelated manual folders are preserved.");

            fixture.Manual("Metadata", "## Interface: 100000\n## Title: Wrong client variant\n", "meta");
            File.WriteAllText(Path.Combine(fixture.Addons, "Metadata", "Metadata_Wrath.toc"),
                "## Interface: 30403, 30400\n## Title: |cff00ff00English title|r\n## Title-frFR: |cffff0000Titre manuel|r\n## Author: |cffabcdefAuteur|r\n## Version: 2.7\n## Dependencies: AtlasCore\n## RequiredDeps: Other, atlascore\n## RequiredDependencies: Third\n## OptionalDeps: OptionalOnly\n");
            ManualAddonInstallation metadata = AddonInstallServices.InspectManualAddons(catalog, fixture.Root).Single(item => item.Folder == "Metadata");
            Check(metadata.Name == "Titre manuel" && metadata.Author == "Auteur" && metadata.Version == "2.7" && metadata.HasCompatibleInterface,
                "Manual inventory prefers a compatible TOC and cleans localized title/author metadata.");
            Check(metadata.Dependencies.SequenceEqual(["AtlasCore", "Other", "Third"], StringComparer.OrdinalIgnoreCase),
                "Required dependency aliases are combined without duplicates and optional dependencies stay separate.");
            Check(AddonInstallServices.InspectManualAddons(catalog, fixture.Root).All(item => item.Folder != "AtlasCore" && item.Folder != "KnownWidget"),
                "The manual inventory excludes catalog-owned folder names.");
            fixture.Manual("TooLarge", "## Interface: 30403\n" + new string('x', 1024 * 1024), "large");
            Check(AddonInstallServices.InspectManualAddons(catalog, fixture.Root).Single(item => item.Folder == "TooLarge").InspectionError == "unreadable",
                "Oversized TOC files are reported unreadable instead of being parsed without a bound.");
            await fixture.Apply(catalog, new() { [core.Id] = true });
            await PlanErrorAsync("dependency-inspection-failed", () => fixture.Apply(catalog, new() { [core.Id] = false }), ["manual:toolarge"],
                "Unreadable retained TOC metadata cannot silently authorize an unchecked dependency removal.");
        }
        async Task CatalogValidationAsync()
        {
            using Fixture fixture = new(); AddonPackage package = fixture.Package("evidence", "EvidenceAddon");
            AddonCatalog catalog = Catalog(package);
            Check(package.ValidatedAtlasEvidenceUrl.Length == 0, "Absent evidence cannot imply an Atlas validation.");
            foreach (string scheme in new[] { "https", "http" })
            {
                package.AtlasValidation = new() { Version = package.Version, EvidenceUrl = scheme + "://atlas.test/evidence" };
                AddonCatalog loaded = await fixture.Load(catalog);
                Check(loaded.Addons[0].ValidatedAtlasEvidenceUrl == scheme + "://atlas.test/evidence", "Matching-version " + scheme + " evidence is preserved.");
            }
            package.AtlasValidation!.Version = "another-version";
            Check(package.ValidatedAtlasEvidenceUrl.Length == 0, "Evidence for another version cannot label this version as tested.");
            await ExpectAsync<InvalidOperationException>(() => fixture.Load(catalog), "Mismatched evidence versions must fail catalog validation.");
            package.AtlasValidation.Version = package.Version;
            foreach (string url in new[] { "javascript:alert(1)", "file:///C:/fixture.txt", "/relative-proof", "https://user:password@atlas.test/proof" })
            {
                package.AtlasValidation.EvidenceUrl = url;
                Check(package.ValidatedAtlasEvidenceUrl.Length == 0, "Unsafe/relative evidence is never projected as a validation link.");
                await ExpectAsync<InvalidOperationException>(() => fixture.Load(catalog), "Invalid evidence URLs must fail catalog validation.");
            }
        }
        async Task LinksAsync()
        {
            using Fixture fixture = new(); AddonPackage package = fixture.Package("linked", "LinkedAddon"); AddonCatalog catalog = Catalog(package);
            await fixture.Apply(catalog, new() { [package.Id] = true });
            string target = Path.Combine(fixture.Root, "private-fixture-target"); Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "private.txt"), "Synthetic outside-folder sentinel");
            string link = Path.Combine(fixture.Addons, "LinkedAddon", "nested-link");
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Console.WriteLine("Addon integrity links SKIP: this host cannot create a synthetic directory symlink (" + error.GetType().Name + ").");
                return;
            }
            try
            {
                await ExpectAsync<IOException>(() => fixture.Verify(catalog, package), "Verification must refuse a nested directory link.");
                int requests = fixture.Handler.Requests.Count;
                await ExpectAsync<IOException>(() => fixture.Apply(catalog, new() { [package.Id] = true }, force: true), "Reinstall must refuse linked descendants before mutation.");
                Check(fixture.Handler.Requests.Count == requests && File.ReadAllText(Path.Combine(target, "private.txt")) == "Synthetic outside-folder sentinel",
                    "A link refusal neither downloads nor changes the linked target.");
            }
            finally { Directory.Delete(link); }
        }
    }

    private static AddonCatalog Catalog(params AddonPackage[] packages) => new() { SchemaVersion = 1, ClientInterface = "30403", Addons = [.. packages] };
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AtlasAddonIntegrityTests", Guid.NewGuid().ToString("N"));
        internal string Addons => AddonInstallServices.GetAddonsDirectory(Root);
        internal string StatePath => Path.Combine(Addons, ".atlas-addons.json");
        internal FixtureHttpHandler Handler { get; } = new();
        private readonly HttpClient _http;
        internal Fixture()
        {
            Directory.CreateDirectory(GameInstallServices.GetClassicDirectoryPath(Root));
            File.WriteAllBytes(GameInstallServices.GetGameExecutablePath(Root), []);
            File.WriteAllBytes(GameInstallServices.GetGameLauncherPath(Root), []);
            _http = new(Handler);
        }
        internal AddonPackage Package(string id, string folder)
        {
            using MemoryStream bytes = new();
            using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach ((string name, string content) in new Dictionary<string, string>
                {
                    [folder + "/" + folder + ".toc"] = "## Interface: 30403\n## Title: " + id + "\n## Version: 1.0\ncore.lua\n",
                    [folder + "/core.lua"] = "return 1", [folder + "/settings.xml"] = "<Ui/>", [folder + "/notes.txt"] = "Fixture notes"
                })
                {
                    using StreamWriter writer = new(archive.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(content);
                }
            byte[] payload = bytes.ToArray(); string url = "https://atlas.test/" + id + ".zip"; Handler.Responses[url] = payload;
            return new() { Id = id, Name = id, Version = "1.0", Interface = "30403", Url = url, Size = payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(payload)), Folders = [folder] };
        }
        internal Task Apply(AddonCatalog catalog, Dictionary<string, bool> selection, bool force = false, bool allowExternal = false)
            => AddonInstallServices.ApplySelectionAsync(_http, catalog, Root, selection, null, null, CancellationToken.None, forceReinstall: force, allowExternalReplacement: allowExternal);
        internal Task<AddonVerificationResult> Verify(AddonCatalog catalog, AddonPackage package)
            => AddonInstallServices.VerifyAsync(catalog, Root, package.Id, CancellationToken.None);
        internal Task<AddonCatalog> Load(AddonCatalog catalog)
        {
            Handler.Responses["https://atlas.test/catalog.json"] = JsonSerializer.SerializeToUtf8Bytes(catalog);
            return AddonInstallServices.LoadCatalogAsync(_http, new("https://atlas.test/catalog.json"), CancellationToken.None);
        }
        internal void Manual(string folder, string toc, string body)
        {
            string path = Path.Combine(Addons, folder); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, folder + ".toc"), toc); File.WriteAllText(Path.Combine(path, "core.lua"), body);
        }
        internal void RemoveFolder(string folder)
        {
            string path = Path.GetFullPath(Path.Combine(Addons, folder));
            if (!path.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture cleanup path.");
            AddonLocalInventory.EnsureNotLinked(path); Directory.Delete(path, recursive: true);
        }
        public void Dispose()
        {
            _http.Dispose();
            string path = Path.GetFullPath(Root), boundary = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AtlasAddonIntegrityTests")) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture root.");
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        internal Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); string url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            if (!Responses.TryGetValue(url, out byte[]? bytes)) throw new InvalidOperationException("Unexpected synthetic HTTP request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}
