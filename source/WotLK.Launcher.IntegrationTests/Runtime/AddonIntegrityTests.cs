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
        await StateLoadingBoundsAsync();
        await StateWriterTransitionAsync();
        await PreflightAsync();
        await DependentsAsync();
        await CatalogValidationAsync();
        await DownloadBoundsAsync();
        ArchiveEntryBounds();
        await TokenReplacementBoundsAsync();
        await TocValidationBoundsAsync();
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
        void Expect<T>(Action operation, string message) where T : Exception
        {
            try { operation(); }
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
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.Unmanaged,
                "A malformed traversal manifest neutralizes the complete state before reading any referenced path.");
            invalid["addons"]![package.Id]!["files"] = new JsonObject
            {
                ["IntegrityAddon/a.lua"] = new JsonObject { ["size"] = 2L * 1024 * 1024 * 1024, ["sha256"] = new string('a', 64) },
                ["IntegrityAddon/b.lua"] = new JsonObject { ["size"] = 1, ["sha256"] = new string('a', 64) }
            };
            File.WriteAllText(fixture.StatePath, invalid.ToJsonString());
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.Unmanaged,
                "A file manifest exceeding the supported expanded size neutralizes the complete state.");
            JsonObject unknownSchema = JsonNode.Parse(referenceState)!.AsObject(); unknownSchema["schemaVersion"] = 2;
            File.WriteAllText(fixture.StatePath, unknownSchema.ToJsonString());
            Check((await fixture.Verify(catalog, package)).Status == AddonVerificationStatus.Unmanaged,
                "An unsupported state schema cannot grant ownership or verified status.");
            File.WriteAllText(fixture.StatePath, referenceState);
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => AddonInstallServices.VerifyAsync(catalog, fixture.Root, package.Id, cancelled.Token),
                "A cancelled verification must stop.");
        }
        async Task StateLoadingBoundsAsync()
        {
            using Fixture fixture = new();
            AddonPackage package = fixture.Package("state-bounds", "StateBoundsAddon");
            AddonCatalog catalog = Catalog(package);
            await fixture.Apply(catalog, new() { [package.Id] = true });
            string referenceState = File.ReadAllText(fixture.StatePath, Encoding.UTF8);

            AddonInspection normal = AddonInstallServices.Inspect(catalog, fixture.Root)[package.Id];
            Check(normal.IsManaged && normal.HasFileManifest,
                "A normal bounded addon state remains accepted with its file manifest.");

            void CheckNeutralAndPreserved(string message)
            {
                AddonInspection inspection = AddonInstallServices.Inspect(catalog, fixture.Root)[package.Id];
                Check(!inspection.IsManaged && File.Exists(fixture.StatePath), message);
            }

            using (FileStream sparse = new(
                       fixture.StatePath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            {
                sparse.SetLength(AddonInstallServices.MaximumAddonStateBytes + 1L);
            }
            CheckNeutralAndPreserved(
                "A sparse addon state larger than 16 MiB is neutralized from its opened length without being deleted.");

            string duplicateState = referenceState.Replace(
                "\"schemaVersion\": 1",
                "\"schemaVersion\": 1,\n  \"SCHEMAVERSION\": 1",
                StringComparison.Ordinal);
            Check(!string.Equals(duplicateState, referenceState, StringComparison.Ordinal),
                "The duplicate-property fixture must contain two schemaVersion members.");
            File.WriteAllText(fixture.StatePath, duplicateState, new UTF8Encoding(false));
            CheckNeutralAndPreserved(
                "A duplicate addon-state property is rejected case-insensitively without deleting the state file.");

            JsonObject missingSchemaState = JsonNode.Parse(referenceState)!.AsObject();
            missingSchemaState.Remove("schemaVersion");
            File.WriteAllText(
                fixture.StatePath,
                missingSchemaState.ToJsonString(),
                new UTF8Encoding(false));
            CheckNeutralAndPreserved(
                "An addon state cannot inherit the DTO's default schema version when the JSON field is absent.");

            string deepState = "{\"schemaVersion\":1,\"addons\":"
                + new string('[', 40)
                + "{}"
                + new string(']', 40)
                + "}";
            File.WriteAllText(fixture.StatePath, deepState, new UTF8Encoding(false));
            CheckNeutralAndPreserved(
                "An addon state deeper than the JSON ceiling is rejected without deleting the state file.");

            AddonInstallState excessiveAddons = new();
            for (int index = 0; index <= AddonInstallServices.MaximumCatalogAddons; index++)
            {
                excessiveAddons.Addons.Add("state-" + index, new InstalledAddonState
                {
                    Version = "1.0",
                    Sha256 = new string('a', 64),
                    Folders = ["StateFolder" + index],
                    InstalledAtUtc = DateTimeOffset.UtcNow
                });
            }
            File.WriteAllText(
                fixture.StatePath,
                JsonSerializer.Serialize(excessiveAddons),
                new UTF8Encoding(false));
            CheckNeutralAndPreserved(
                "An addon state containing more entries than the catalog ceiling is neutralized intact.");

            string stateHash = new('a', 64);
            using (StreamWriter writer = new(
                       fixture.StatePath,
                       append: false,
                       new UTF8Encoding(false),
                       bufferSize: 64 * 1024))
            {
                writer.Write("{\"schemaVersion\":1,\"addons\":{\"file-count\":{\"version\":\"1.0\",\"sha256\":\"");
                writer.Write(stateHash);
                writer.Write("\",\"folders\":[\"F\"],\"installedAtUtc\":\"2026-09-10T00:00:00+00:00\",\"files\":{");
                for (int index = 0; index <= AddonInstallServices.MaximumArchiveEntries; index++)
                {
                    if (index != 0) writer.Write(',');
                    writer.Write("\"F/f");
                    writer.Write(index.ToString("D6"));
                    writer.Write("\":{\"size\":0,\"sha256\":\"");
                    writer.Write(stateHash);
                    writer.Write("\"}");
                }
                writer.Write("}}}}");
            }
            Check(new FileInfo(fixture.StatePath).Length <= AddonInstallServices.MaximumAddonStateBytes,
                "The excessive-file-count fixture must remain below the byte ceiling.");
            CheckNeutralAndPreserved(
                "More than 100,000 cumulative manifest files neutralize the state before DTO allocation.");

            JsonObject invalidPathState = JsonNode.Parse(referenceState)!.AsObject();
            JsonObject files = invalidPathState["addons"]![package.Id]!["files"]!.AsObject();
            JsonNode fileReference = files.First().Value!.DeepClone();
            files.Clear();
            files["../outside.txt"] = fileReference;
            File.WriteAllText(
                fixture.StatePath,
                invalidPathState.ToJsonString(),
                new UTF8Encoding(false));
            CheckNeutralAndPreserved(
                "A traversal path in a manifest neutralizes the complete state without deletion.");

            foreach ((string label, Action<JsonObject> corrupt) in new (string, Action<JsonObject>)[]
                     {
                         ("version", root => root["addons"]![package.Id]!["version"] = new string('v', 33)),
                         ("hash", root => root["addons"]![package.Id]!["sha256"] = "not-a-sha256"),
                         ("folder", root => root["addons"]![package.Id]!["folders"] =
                             new JsonArray(JsonValue.Create(new string('f', 129)))),
                         ("id", root =>
                         {
                             JsonObject addons = root["addons"]!.AsObject();
                             JsonNode addon = addons[package.Id]!.DeepClone();
                             addons.Clear();
                             addons["../invalid-id"] = addon;
                         })
                     })
            {
                JsonObject corrupted = JsonNode.Parse(referenceState)!.AsObject();
                corrupt(corrupted);
                File.WriteAllText(fixture.StatePath, corrupted.ToJsonString(), new UTF8Encoding(false));
                CheckNeutralAndPreserved(
                    "An invalid or unbounded addon-state " + label + " neutralizes the complete state.");
            }
        }
        async Task StateWriterTransitionAsync()
        {
            const string validHash =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

            static InstalledAddonFile FileReference(string hash) => new()
            {
                Size = 0,
                Sha256 = hash
            };

            static AddonInstallState StateWithFile(
                string addonId,
                string folder,
                string relativePath,
                string hash)
                => new()
                {
                    Addons = new Dictionary<string, InstalledAddonState>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [addonId] = new()
                        {
                            Version = "1.0",
                            Sha256 = hash,
                            Folders = [folder],
                            InstalledAtUtc = DateTimeOffset.UtcNow,
                            Files = new Dictionary<string, InstalledAddonFile>(
                                StringComparer.OrdinalIgnoreCase)
                            {
                                [relativePath] = FileReference(hash)
                            }
                        }
                    }
                };

            void CheckRejectedBeforeCommit(AddonInstallState candidate, string message)
            {
                bool applyEntered = false;
                bool rollbackEntered = false;
                bool persistEntered = false;
                Expect<InvalidDataException>(
                    () => AddonInstallServices.CommitValidatedStateTransition(
                        candidate,
                        () => applyEntered = true,
                        () => rollbackEntered = true,
                        _ => persistEntered = true),
                    message);
                Check(
                    !applyEntered && !rollbackEntered && !persistEntered,
                    message + " No mutation, rollback or state write callback may run.");
            }

            {
                AddonInstallState invalidVersion = StateWithFile(
                    "invalid-version",
                    "VersionFolder",
                    "VersionFolder/core.lua",
                    validHash);
                invalidVersion.Addons["invalid-version"].Version = "1.0\u0001";
                CheckRejectedBeforeCommit(
                    invalidVersion,
                    "A control character in the final addon version is rejected before commit.");
            }

            {
                string longPath = "PathFolder/" + string.Join(
                    '/',
                    Enumerable.Repeat(
                        new string('p', AddonInstallServices.MaximumAddonStateFolderCharacters - 1),
                        8));
                Check(
                    longPath.Length > AddonInstallServices.MaximumAddonStatePathCharacters,
                    "The overlong state-path fixture must exceed 1,024 characters.");
                CheckRejectedBeforeCommit(
                    StateWithFile(
                        "invalid-path",
                        "PathFolder",
                        longPath,
                        validHash),
                    "A final manifest path longer than 1,024 characters is rejected before commit.");
            }

            {
                string longSegment = "SegmentFolder/"
                    + new string(
                        's',
                        AddonInstallServices.MaximumAddonStateFolderCharacters + 1);
                CheckRejectedBeforeCommit(
                    StateWithFile(
                        "invalid-segment",
                        "SegmentFolder",
                        longSegment,
                        validHash),
                    "A final manifest segment longer than 128 characters is rejected before commit.");
            }

            {
                string tooManySegments = "DepthFolder/" + string.Join(
                    '/',
                    Enumerable.Repeat(
                        "d",
                        AddonInstallServices.MaximumAddonStatePathSegments));
                CheckRejectedBeforeCommit(
                    StateWithFile(
                        "invalid-depth",
                        "DepthFolder",
                        tooManySegments,
                        validHash),
                    "A final manifest deeper than 64 segments is rejected before commit.");
            }

            {
                AddonInstallState excessiveFiles = StateWithFile(
                    "small-count",
                    "SmallFolder",
                    "SmallFolder/core.lua",
                    validHash);
                Dictionary<string, InstalledAddonFile> largeManifest = new(
                    AddonInstallServices.MaximumArchiveEntries,
                    StringComparer.OrdinalIgnoreCase);
                InstalledAddonFile sharedFile = FileReference(validHash);
                for (int index = 0;
                     index < AddonInstallServices.MaximumArchiveEntries;
                     index++)
                {
                    largeManifest.Add(
                        "LargeFolder/f" + index.ToString("D6"),
                        sharedFile);
                }

                excessiveFiles.Addons.Add("large-count", new InstalledAddonState
                {
                    Version = "1.0",
                    Sha256 = validHash,
                    Folders = ["LargeFolder"],
                    InstalledAtUtc = DateTimeOffset.UtcNow,
                    Files = largeManifest
                });
                CheckRejectedBeforeCommit(
                    excessiveFiles,
                    "More than 100,000 cumulative final manifest files are rejected before commit.");
            }

            {
                const int oversizedEntryCount = 20_000;
                string repeatedSegment = new('q', 120);
                Dictionary<string, InstalledAddonFile> oversizedManifest = new(
                    oversizedEntryCount,
                    StringComparer.OrdinalIgnoreCase);
                InstalledAddonFile sharedFile = FileReference(validHash);
                for (int index = 0; index < oversizedEntryCount; index++)
                {
                    string uniqueSegment = new string('u', 114)
                        + index.ToString("D6");
                    string relative = "PayloadFolder/"
                        + string.Join(
                            '/',
                            repeatedSegment,
                            repeatedSegment,
                            repeatedSegment,
                            repeatedSegment,
                            repeatedSegment,
                            repeatedSegment,
                            uniqueSegment);
                    oversizedManifest.Add(relative, sharedFile);
                }

                AddonInstallState oversizedPayload = new()
                {
                    Addons = new Dictionary<string, InstalledAddonState>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["oversized-payload"] = new()
                        {
                            Version = "1.0",
                            Sha256 = validHash,
                            Folders = ["PayloadFolder"],
                            InstalledAtUtc = DateTimeOffset.UtcNow,
                            Files = oversizedManifest
                        }
                    }
                };
                CheckRejectedBeforeCommit(
                    oversizedPayload,
                    "A final serialized state larger than 16 MiB is rejected before commit.");
            }

            {
                AddonInstallState validForRollback = StateWithFile(
                    "rollback-state",
                    "RollbackFolder",
                    "RollbackFolder/core.lua",
                    validHash);
                bool mutated = false;
                Expect<IOException>(
                    () => AddonInstallServices.CommitValidatedStateTransition(
                        validForRollback,
                        () => mutated = true,
                        () => mutated = false,
                        _ => throw new IOException("Synthetic state-write failure.")),
                    "A prepared-state write failure must be reported.");
                Check(
                    !mutated,
                    "A prepared-state write failure runs the folder rollback callback.");
            }

            using (Fixture fixture = new())
            {
                AddonPackage package = fixture.Package(
                    "write-failure",
                    "WriteFailureAddon");
                AddonCatalog catalog = Catalog(package);
                await fixture.Apply(catalog, new() { [package.Id] = true });
                string referenceState = File.ReadAllText(
                    fixture.StatePath,
                    Encoding.UTF8);
                string referenceFile = File.ReadAllText(
                    Path.Combine(fixture.Addons, "WriteFailureAddon", "core.lua"),
                    Encoding.UTF8);
                fixture.ReplaceCoreArchive(
                    package,
                    "WriteFailureAddon",
                    stream => stream.Write("return 2"u8));

                await using (FileStream stateLock = new(
                                 fixture.StatePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read))
                {
                    await ExpectAsync<UnauthorizedAccessException>(
                        () => fixture.Apply(
                            catalog,
                            new() { [package.Id] = true },
                            force: true),
                        "A real atomic state-write failure must be reported after staging.");
                }

                Check(
                    File.ReadAllText(
                        Path.Combine(
                            fixture.Addons,
                            "WriteFailureAddon",
                            "core.lua"),
                        Encoding.UTF8) == referenceFile &&
                    File.ReadAllText(fixture.StatePath, Encoding.UTF8) == referenceState &&
                    !Directory.EnumerateFileSystemEntries(fixture.Addons)
                        .Select(Path.GetFileName)
                        .Any(name =>
                            name!.StartsWith(
                                ".atlas-stage-",
                                StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith(
                                ".atlas-backup-",
                                StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith(
                                ".atlas-work-",
                                StringComparison.OrdinalIgnoreCase)),
                    "A real state-write failure restores the previous managed folder and state without transition leftovers.");
            }

            using (Fixture fixture = new())
            {
                AddonPackage package = fixture.Package(
                    "writer-roundtrip",
                    "WriterRoundTripAddon");
                AddonCatalog catalog = Catalog(package);
                const string content = "return 'validated-state'";
                string contentHash = Convert.ToHexString(
                        SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                    .ToLowerInvariant();
                AddonInstallState accepted = new()
                {
                    Addons = new Dictionary<string, InstalledAddonState>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [package.Id] = new()
                        {
                            Version = package.Version,
                            Sha256 = package.EffectiveInstallHash,
                            Folders = [.. package.Folders],
                            InstalledAtUtc = DateTimeOffset.UtcNow,
                            Files = new Dictionary<string, InstalledAddonFile>(
                                StringComparer.OrdinalIgnoreCase)
                            {
                                ["WriterRoundTripAddon/core.lua"] = new()
                                {
                                    Size = Encoding.UTF8.GetByteCount(content),
                                    Sha256 = contentHash
                                }
                            }
                        }
                    }
                };

                AddonInstallServices.CommitValidatedStateTransition(
                    accepted,
                    () =>
                    {
                        string folder = Path.Combine(
                            fixture.Addons,
                            "WriterRoundTripAddon");
                        Directory.CreateDirectory(folder);
                        File.WriteAllText(
                            Path.Combine(folder, "core.lua"),
                            content,
                            new UTF8Encoding(false));
                    },
                    () => fixture.RemoveFolder("WriterRoundTripAddon"),
                    payload => File.WriteAllBytes(fixture.StatePath, payload));

                AddonInspection inspection = AddonInstallServices.Inspect(
                    catalog,
                    fixture.Root)[package.Id];
                AddonVerificationResult verification = await fixture.Verify(
                    catalog,
                    package);
                Check(
                    inspection.IsManaged &&
                    inspection.HasFileManifest &&
                    verification.Status == AddonVerificationStatus.Verified &&
                    verification.CheckedFiles == 1,
                    "A committed prepared payload round-trips through the real state reader and manifest verifier.");
            }

            using (Fixture fixture = new())
            {
                AddonPackage installed = fixture.Package(
                    "preserved",
                    "PreservedAddon");
                AddonPackage invalid = fixture.Package(
                    "invalid-transition",
                    "InvalidTransitionAddon");
                AddonCatalog catalog = Catalog(installed, invalid);
                await fixture.Apply(catalog, new() { [installed.Id] = true });
                string referenceState = File.ReadAllText(
                    fixture.StatePath,
                    Encoding.UTF8);
                string referenceFile = File.ReadAllText(
                    Path.Combine(fixture.Addons, "PreservedAddon", "core.lua"),
                    Encoding.UTF8);

                async Task CheckArchiveRejectedBeforeRemoval(
                    string entryName,
                    string message)
                {
                    fixture.ReplaceArchive(
                        invalid,
                        archive => Fixture.WriteTextEntry(
                            archive,
                            entryName,
                            "return false"));
                    await ExpectAsync<InvalidDataException>(
                        () => fixture.Apply(
                            catalog,
                            new Dictionary<string, bool>
                            {
                                [installed.Id] = false,
                                [invalid.Id] = true
                            }),
                        message);
                    Check(
                        Directory.Exists(
                            Path.Combine(fixture.Addons, "PreservedAddon")) &&
                        File.ReadAllText(
                            Path.Combine(
                                fixture.Addons,
                                "PreservedAddon",
                                "core.lua"),
                            Encoding.UTF8) == referenceFile &&
                        File.ReadAllText(fixture.StatePath, Encoding.UTF8) == referenceState &&
                        !Directory.Exists(
                            Path.Combine(
                                fixture.Addons,
                                "InvalidTransitionAddon")),
                        message + " The selected removal and its state remain unchanged.");
                }

                string longArchivePath = "InvalidTransitionAddon/" + string.Join(
                    '/',
                    Enumerable.Repeat(
                        new string('p', AddonInstallServices.MaximumAddonStateFolderCharacters - 1),
                        8));
                await CheckArchiveRejectedBeforeRemoval(
                    longArchivePath,
                    "An archive path longer than the state-reader ceiling is rejected before a selected removal.");
                await CheckArchiveRejectedBeforeRemoval(
                    "InvalidTransitionAddon/"
                    + new string(
                        's',
                        AddonInstallServices.MaximumAddonStateFolderCharacters + 1),
                    "An archive segment longer than the state-reader ceiling is rejected before a selected removal.");

                int requestCount = fixture.Handler.Requests.Count;
                invalid.Version = "1.0\n";
                await ExpectAsync<InvalidOperationException>(
                    () => fixture.Apply(
                        catalog,
                        new Dictionary<string, bool>
                        {
                            [installed.Id] = false,
                            [invalid.Id] = true
                        }),
                    "A catalog version containing a control character is rejected before a selected removal.");
                Check(
                    fixture.Handler.Requests.Count == requestCount &&
                    Directory.Exists(
                        Path.Combine(fixture.Addons, "PreservedAddon")) &&
                    File.ReadAllText(fixture.StatePath, Encoding.UTF8) == referenceState,
                    "Invalid catalog metadata causes no download, folder replacement, removal or state write.");
            }
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
            string[] currentArchiveUrls =
            [
                "https://github.com/WeakAuras/WeakAuras2/releases/download/5.13.1/WeakAuras-5.13.1.zip",
                "https://github.com/tukui-org/ElvUI/archive/refs/tags/v13.61.zip",
                "https://animeclub.fr/wotlk/addons/packages/ElvUI-13.61-libraries-wotlk-30403.zip",
                "https://github.com/Questie/Questie/releases/download/v10.19.2/Questie-v10.19.2.zip",
                "https://animeclub.fr/wotlk/addons/packages/DBM-Core-11.0.34-wotlk-30403.zip",
                "https://edge.forgecdn.net/files/5763/677/DBM-Raids-WoTLK-r337.zip",
                "https://edge.forgecdn.net/files/5194/699/DBM-Party-WotLK-r122-wrath.zip",
                "https://edge.forgecdn.net/files/5237/506/DBM-Vanilla_SoD_BC-r713.zip",
                "https://animeclub.fr/wotlk/addons/packages/Details-Details.20240115.12220.155-atlas-30403.zip",
                "https://edge.forgecdn.net/files/4811/104/AtlasLootClassic-v3.2.0.zip",
                "https://edge.forgecdn.net/files/4869/540/Auctionator-10.2.0-wrath.zip",
                "https://edge.forgecdn.net/files/5275/306/Leatrix_Plus-3.0.191.zip",
                "https://edge.forgecdn.net/files/5292/821/NovaInstanceTracker-v1.55-Wrath.zip",
                "https://edge.forgecdn.net/files/5758/185/Attune-WOTLK-314.zip",
                "https://release-assets.githubusercontent.com/github-production-release-asset/fixture/archive.zip?sig=test",
                "https://codeload.github.com/tukui-org/ElvUI/zip/refs/tags/v13.61",
                "https://mediafilez.forgecdn.net/files/4811/104/AtlasLootClassic-v3.2.0.zip"
            ];
            Check(currentArchiveUrls.All(url => AddonInstallServices.IsAllowedAddonArchiveUri(new Uri(url))),
                "Current addon origins and their observed exact redirect destinations are accepted without network access.");
            foreach (string rejected in new[]
            {
                "http://github.com/archive.zip",
                "https://github.com:444/archive.zip",
                "https://user@github.com/archive.zip",
                "https://127.0.0.1/archive.zip",
                "https://[::1]/archive.zip",
                "https://localhost/archive.zip",
                "https://evil.example/archive.zip",
                "https://github.com.evil.example/archive.zip"
            })
            {
                Check(!AddonInstallServices.IsAllowedAddonArchiveUri(new Uri(rejected)),
                    "Archive origin policy rejects downgrade, non-default port, userinfo, IP, loopback and off-list hosts.");
            }
            Check(AddonInstallServices.IsAllowedAddonCatalogUri(new Uri("https://animeclub.fr/wotlk/addons/catalog.json"))
                && !AddonInstallServices.IsAllowedAddonCatalogUri(new Uri("https://github.com/catalog.json")),
                "The addon catalog itself is restricted to the exact Atlas production origin.");
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

            package.AtlasValidation = null;
            string archiveUrl = package.Url;
            package.Components =
            [
                new AddonPackageComponent
                {
                    Name = "Legacy component URL",
                    Url = "http://animeclub.fr/component.zip?source=legacy",
                    Size = package.Size,
                    Sha256 = package.Sha256
                }
            ];
            package.Url = "http://animeclub.fr:80/evidence.zip";
            AddonCatalog canonicalized = await fixture.Load(catalog);
            Check(canonicalized.Addons[0].Url == archiveUrl
                  && canonicalized.Addons[0].Components[0].Url == "https://animeclub.fr/component.zip?source=legacy",
                "Legacy Atlas package and component URLs are canonicalized locally to HTTPS before catalog validation.");

            package.Components = [];
            int compatibilityRequestStart = fixture.Handler.Requests.Count;
            canonicalized = await fixture.Load(catalog);
            await fixture.Apply(canonicalized, new() { [canonicalized.Addons[0].Id] = true });
            string[] compatibilityRequests = fixture.Handler.Requests.Skip(compatibilityRequestStart).ToArray();
            Check(compatibilityRequests.SequenceEqual(["https://animeclub.fr/catalog.json", archiveUrl])
                  && compatibilityRequests.All(url => url.StartsWith("https://", StringComparison.Ordinal)),
                "Legacy Atlas archive compatibility performs one HTTPS catalog request and one HTTPS archive request without an HTTP hop.");

            foreach (string rejectedLegacyUrl in new[]
                     {
                         "http://animeclub.fr:81/evidence.zip",
                         "http://user@animeclub.fr/evidence.zip",
                         "http://animeclub.fr/evidence.zip#fragment",
                         "http://evil.example/evidence.zip"
                     })
            {
                package.Url = rejectedLegacyUrl;
                await ExpectAsync<InvalidOperationException>(
                    () => fixture.Load(catalog),
                    "Only the exact userinfo-free, fragment-free Atlas HTTP port 80 alias may be canonicalized: " + rejectedLegacyUrl);
            }
            package.Url = archiveUrl;

            int requests = fixture.Handler.Requests.Count;
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri("http://animeclub.fr/catalog.json")),
                "An HTTP catalog URL must be rejected before sending a request.");
            Check(fixture.Handler.Requests.Count == requests, "An HTTP catalog URL must not reach the transport.");

            const string catalogUrl = "https://animeclub.fr/catalog.json";
            string validJson = JsonSerializer.Serialize(catalog);
            fixture.Handler.ResponseFactories[catalogUrl] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://animeclub.fr/catalog.json"),
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(validJson))
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "A catalog redirect downgrade to HTTP must be rejected.");
            fixture.Handler.ResponseFactories.Remove(catalogUrl);

            int requestsBeforeRedirect = fixture.Handler.Requests.Count;
            fixture.Handler.ResponseFactories[catalogUrl] = () => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("http://animeclub.fr/catalog.json") }
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "A catalog redirect downgrade must be rejected before following its target.");
            Check(
                fixture.Handler.Requests.Count == requestsBeforeRedirect + 1,
                "A rejected catalog redirect must not trigger a second HTTP hop.");
            fixture.Handler.ResponseFactories.Remove(catalogUrl);

            const string redirectedCatalogUrl = "https://animeclub.fr/catalog-v1.json";
            requestsBeforeRedirect = fixture.Handler.Requests.Count;
            fixture.Handler.ResponseFactories[catalogUrl] = () => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("/catalog-v1.json", UriKind.Relative) }
            };
            fixture.Handler.Responses[redirectedCatalogUrl] = Encoding.UTF8.GetBytes(validJson);
            AddonCatalog redirectedCatalog = await fixture.LoadFrom(new Uri(catalogUrl));
            Check(redirectedCatalog.Addons.Count == 1
                && fixture.Handler.Requests.Skip(requestsBeforeRedirect).SequenceEqual([catalogUrl, redirectedCatalogUrl]),
                "An allowed same-origin catalog redirect is followed manually and exactly once.");
            fixture.Handler.ResponseFactories.Remove(catalogUrl);
            fixture.Handler.Responses.Remove(redirectedCatalogUrl);

            const string loopUrl = "https://animeclub.fr/catalog-loop.json";
            requestsBeforeRedirect = fixture.Handler.Requests.Count;
            fixture.Handler.ResponseFactories[catalogUrl] = () => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(loopUrl) }
            };
            fixture.Handler.ResponseFactories[loopUrl] = () => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(catalogUrl) }
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "A catalog redirect loop must be rejected.");
            Check(fixture.Handler.Requests.Count == requestsBeforeRedirect + 2,
                "A redirect loop stops before replaying a previously visited URL.");
            fixture.Handler.ResponseFactories.Remove(catalogUrl);
            fixture.Handler.ResponseFactories.Remove(loopUrl);

            string[] redirectChain = Enumerable.Range(1, AddonInstallServices.MaximumRemoteRedirects + 1)
                .Select(index => $"https://animeclub.fr/catalog-r{index}.json")
                .ToArray();
            requestsBeforeRedirect = fixture.Handler.Requests.Count;
            fixture.Handler.ResponseFactories[catalogUrl] = () => Redirect(redirectChain[0]);
            for (int index = 0; index < redirectChain.Length - 1; index++)
            {
                string current = redirectChain[index];
                string next = redirectChain[index + 1];
                fixture.Handler.ResponseFactories[current] = () => Redirect(next);
            }
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "More than three catalog redirects must be rejected.");
            Check(fixture.Handler.Requests.Count == requestsBeforeRedirect + AddonInstallServices.MaximumRemoteRedirects + 1
                && !fixture.Handler.Requests.Contains(redirectChain[^1], StringComparer.Ordinal),
                "The redirect ceiling is enforced before a fourth target request.");
            fixture.Handler.ResponseFactories.Remove(catalogUrl);
            foreach (string redirectUrl in redirectChain)
            {
                fixture.Handler.ResponseFactories.Remove(redirectUrl);
            }

            fixture.Handler.ResponseFactories[catalogUrl] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://animeclub.fr/hidden-final.json"),
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(validJson))
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "A transport-reported final URL that bypasses the manual redirect chain must be rejected.");
            fixture.Handler.ResponseFactories.Remove(catalogUrl);

            fixture.Handler.Responses[catalogUrl] = Encoding.UTF8.GetBytes(validJson[..^1] + ",\"unexpected\":true}");
            await ExpectAsync<JsonException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "Unknown catalog properties must fail strict deserialization.");

            string duplicate = validJson.Replace(
                "\"schemaVersion\":1",
                "\"schemaVersion\":1,\"schemaVersion\":1",
                StringComparison.Ordinal);
            fixture.Handler.Responses[catalogUrl] = Encoding.UTF8.GetBytes(duplicate);
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "Duplicate catalog properties must be rejected.");

            fixture.Handler.ResponseFactories[catalogUrl] = () =>
            {
                HttpResponseMessage response = new(HttpStatusCode.OK) { Content = new ByteArrayContent("{}"u8.ToArray()) };
                response.Content.Headers.ContentLength = AddonInstallServices.MaximumCatalogBytes + 1L;
                return response;
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "An excessive declared catalog size must be rejected before buffering.");

            byte[] streamedOversize = Enumerable.Repeat((byte)' ', AddonInstallServices.MaximumCatalogBytes + 1).ToArray();
            fixture.Handler.ResponseFactories[catalogUrl] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedReadStream(streamedOversize, 64 * 1024))
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.LoadFrom(new Uri(catalogUrl)),
                "A chunked catalog must remain bounded without Content-Length.");

            static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(location) }
            };
        }

        async Task DownloadBoundsAsync()
        {
            using (Fixture redirectFixture = new())
            {
                AddonPackage redirectedPackage = redirectFixture.Package("redirected", "RedirectedAddon");
                byte[] redirectedArchive = redirectFixture.Handler.Responses[redirectedPackage.Url];
                redirectFixture.Handler.Responses.Remove(redirectedPackage.Url);
                redirectedPackage.Url = "https://github.com/tukui-org/ElvUI/archive/refs/tags/v13.61.zip";
                const string finalArchiveUrl = "https://codeload.github.com/tukui-org/ElvUI/zip/refs/tags/v13.61";
                redirectFixture.Handler.ResponseFactories[redirectedPackage.Url] = () => new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri(finalArchiveUrl) }
                };
                redirectFixture.Handler.Responses[finalArchiveUrl] = redirectedArchive;
                await redirectFixture.Apply(Catalog(redirectedPackage), new() { [redirectedPackage.Id] = true });
                Check(redirectFixture.Handler.Requests.SequenceEqual([redirectedPackage.Url, finalArchiveUrl])
                    && Directory.Exists(Path.Combine(redirectFixture.Addons, "RedirectedAddon")),
                    "An observed GitHub-to-codeload archive redirect is followed manually before installation.");
            }

            using Fixture fixture = new();
            AddonPackage package = fixture.Package("bounded", "BoundedAddon");
            AddonCatalog catalog = Catalog(package);
            byte[] archive = fixture.Handler.Responses[package.Url];
            byte[] oversized = archive.Concat([(byte)0x42]).ToArray();

            int requestsBeforeOffListRedirect = fixture.Handler.Requests.Count;
            fixture.Handler.ResponseFactories[package.Url] = () => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://evil.example/bounded.zip") }
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.Apply(catalog, new() { [package.Id] = true }),
                "An off-list archive redirect must be rejected before the target request.");
            Check(fixture.Handler.Requests.Count == requestsBeforeOffListRedirect + 1
                && !Directory.Exists(Path.Combine(fixture.Addons, "BoundedAddon")),
                "An off-list archive redirect cannot install files or reach its destination.");

            fixture.Handler.ResponseFactories[package.Url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://animeclub.fr/bounded.zip"),
                Content = new ByteArrayContent(archive)
            };
            await ExpectAsync<InvalidDataException>(
                () => fixture.Apply(catalog, new() { [package.Id] = true }),
                "An addon archive redirect downgrade to HTTP must be rejected.");

            fixture.Handler.ResponseFactories[package.Url] = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedReadStream(oversized, archive.Length))
            };
            List<AddonTransferProgress> progress = [];

            await ExpectAsync<InvalidOperationException>(
                () => fixture.Apply(
                    catalog,
                    new() { [package.Id] = true },
                    progress: new ImmediateProgress<AddonTransferProgress>(progress.Add)),
                "A chunked archive exceeding its declared package size must fail during streaming.");

            Check(progress.Count > 0 && progress.Max(item => item.BytesReceived) == package.Size,
                "The excess archive byte must be rejected before it is written or reported.");
            Check(!Directory.Exists(Path.Combine(fixture.Addons, "BoundedAddon")) && !File.Exists(fixture.StatePath),
                "An excessive archive must not install files or persist addon state.");
        }
        void ArchiveEntryBounds()
        {
            using Fixture fixture = new();

            string oversizedDestination = Path.Combine(fixture.Root, "oversized-entry.bin");
            using MemoryStream oversizedSource = new([1, 2, 3, 4, 5]);
            bool oversizedRejected = false;
            try
            {
                AddonInstallServices.CopyArchiveEntryToFileBounded(
                    oversizedSource,
                    oversizedDestination,
                    declaredLength: 4,
                    expandedSize: 0,
                    CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                oversizedRejected = true;
            }

            Check(oversizedRejected,
                "Archive extraction counts actual bytes and rejects a stream longer than ZipArchiveEntry.Length.");
            Check(!File.Exists(oversizedDestination),
                "A rejected oversized ZIP entry leaves no partial file.");

            string truncatedDestination = Path.Combine(fixture.Root, "truncated-entry.bin");
            using MemoryStream truncatedSource = new([1, 2, 3]);
            bool truncatedRejected = false;
            try
            {
                AddonInstallServices.CopyArchiveEntryToFileBounded(
                    truncatedSource,
                    truncatedDestination,
                    declaredLength: 4,
                    expandedSize: 0,
                    CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                truncatedRejected = true;
            }

            Check(truncatedRejected && !File.Exists(truncatedDestination),
                "Archive extraction requires the exact declared size and removes a truncated partial file.");

            string totalLimitDestination = Path.Combine(fixture.Root, "total-limit-entry.bin");
            using MemoryStream totalLimitSource = new([1]);
            bool totalLimitRejected = false;
            try
            {
                AddonInstallServices.CopyArchiveEntryToFileBounded(
                    totalLimitSource,
                    totalLimitDestination,
                    declaredLength: 1,
                    expandedSize: AddonInstallServices.MaximumExpandedArchiveSize,
                    CancellationToken.None);
            }
            catch (InvalidDataException)
            {
                totalLimitRejected = true;
            }

            Check(totalLimitRejected && !File.Exists(totalLimitDestination),
                "The cumulative extracted size cannot exceed 2 GiB even when an entry is individually small.");

            byte[] exactPayload = [6, 7, 8, 9];
            string exactDestination = Path.Combine(fixture.Root, "exact-entry.bin");
            using MemoryStream exactSource = new(exactPayload);
            long expandedSize = AddonInstallServices.CopyArchiveEntryToFileBounded(
                exactSource,
                exactDestination,
                declaredLength: exactPayload.Length,
                expandedSize: 7,
                CancellationToken.None);
            Check(expandedSize == 11 && File.ReadAllBytes(exactDestination).SequenceEqual(exactPayload),
                "An exact ZIP entry is fully copied and contributes its actual bytes to the cumulative total.");
        }
        async Task TokenReplacementBoundsAsync()
        {
            using (Fixture fixture = new())
            {
                AddonPackage package = fixture.Package("tokens", "TokenAddon");
                package.TokenReplacements["@project-version@"] = "13.61";
                fixture.ReplaceCoreArchive(
                    package,
                    "TokenAddon",
                    stream =>
                    {
                        byte[] content = Encoding.UTF8.GetBytes("local version = '@project-version@'");
                        stream.Write(content);
                    });

                await fixture.Apply(Catalog(package), new() { [package.Id] = true });
                Check(
                    File.ReadAllText(Path.Combine(fixture.Addons, "TokenAddon", "core.lua"), Encoding.UTF8)
                        == "local version = '13.61'",
                    "A normal ElvUI-style token replacement remains compatible under the text bounds.");
            }

            using (Fixture fixture = new())
            {
                AddonPackage package = fixture.Package("oversized-text", "OversizedTextAddon");
                package.TokenReplacements["@token@"] = "replacement";
                fixture.ReplaceCoreArchive(
                    package,
                    "OversizedTextAddon",
                    stream =>
                    {
                        byte[] block = Enumerable.Repeat((byte)'x', 128 * 1024).ToArray();
                        long remaining = AddonInstallServices.MaximumTokenReplacementTextFileBytes + 1L;
                        while (remaining > 0)
                        {
                            int count = (int)Math.Min(block.Length, remaining);
                            stream.Write(block, 0, count);
                            remaining -= count;
                        }
                    });

                await ExpectAsync<InvalidDataException>(
                    () => fixture.Apply(Catalog(package), new() { [package.Id] = true }),
                    "A token-replacement text file larger than the production bound must be rejected before buffering.");
                Check(
                    !Directory.Exists(Path.Combine(fixture.Addons, "OversizedTextAddon"))
                    && !File.Exists(fixture.StatePath),
                    "An oversized replacement input rolls back without installing files or state.");
            }

            using (Fixture fixture = new())
            {
                AddonPackage package = fixture.Package("expanding-text", "ExpandingTextAddon");
                package.TokenReplacements["@x@"] = new string('y', 128);
                fixture.ReplaceCoreArchive(
                    package,
                    "ExpandingTextAddon",
                    stream =>
                    {
                        byte[] token = "@x@"u8.ToArray();
                        int occurrences = AddonInstallServices.MaximumTokenReplacementTextFileBytes / 128 + 1;
                        byte[] block = new byte[12 * 1024];
                        for (int offset = 0; offset < block.Length; offset += token.Length)
                        {
                            token.CopyTo(block, offset);
                        }

                        int tokensPerBlock = block.Length / token.Length;
                        while (occurrences >= tokensPerBlock)
                        {
                            stream.Write(block);
                            occurrences -= tokensPerBlock;
                        }

                        for (int index = 0; index < occurrences; index++)
                        {
                            stream.Write(token);
                        }
                    });

                await ExpectAsync<InvalidDataException>(
                    () => fixture.Apply(Catalog(package), new() { [package.Id] = true }),
                    "A small repetitive text input must be rejected before a token replacement can expand it beyond the bound.");
                Check(
                    !Directory.Exists(Path.Combine(fixture.Addons, "ExpandingTextAddon"))
                    && !File.Exists(fixture.StatePath),
                    "An excessive replacement expansion rolls back without installing files or state.");

                string cumulativePath = Path.Combine(fixture.Root, "cumulative.lua");
                File.WriteAllText(cumulativePath, "@x@", new UTF8Encoding(false));
                bool cumulativeRejected = false;
                try
                {
                    AddonInstallServices.ApplyTokenReplacementsToFileBounded(
                        cumulativePath,
                        package.TokenReplacements,
                        AddonInstallServices.MaximumExpandedArchiveSize - 1,
                        AddonInstallServices.MaximumTokenReplacementTextFileBytes,
                        CancellationToken.None);
                }
                catch (InvalidDataException)
                {
                    cumulativeRejected = true;
                }

                Check(
                    cumulativeRejected && File.ReadAllText(cumulativePath, Encoding.UTF8) == "@x@",
                    "Cumulative expansion beyond 2 GiB is rejected before mutating the temporary source file.");
            }
        }
        async Task TocValidationBoundsAsync()
        {
            using Fixture fixture = new();

            AddonPackage sparsePackage = fixture.Package("sparse-toc", "SparseTocAddon");
            string sparseRoot = Path.Combine(fixture.Root, "sparse-toc-extracted");
            string sparseFolder = Path.Combine(sparseRoot, "SparseTocAddon");
            Directory.CreateDirectory(sparseFolder);
            using (FileStream sparse = new(
                       Path.Combine(sparseFolder, "SparseTocAddon.toc"),
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                sparse.SetLength(AddonInstallServices.MaximumCompatibleTocBytes + 1L);
            }

            await ExpectAsync<InvalidDataException>(
                () => AddonInstallServices.ValidateExtractedFoldersAsync(
                    sparsePackage,
                    sparseRoot,
                    CancellationToken.None),
                "A sparse TOC larger than 1 MiB must be rejected from the opened handle before text is read.");

            AddonPackage longLinePackage = fixture.Package("long-line-toc", "LongLineTocAddon");
            string longLineRoot = Path.Combine(fixture.Root, "long-line-toc-extracted");
            string longLineFolder = Path.Combine(longLineRoot, "LongLineTocAddon");
            Directory.CreateDirectory(longLineFolder);
            File.WriteAllText(
                Path.Combine(longLineFolder, "LongLineTocAddon.toc"),
                new string('x', 16 * 1024 + 1),
                new UTF8Encoding(false));
            await ExpectAsync<InvalidDataException>(
                () => AddonInstallServices.ValidateExtractedFoldersAsync(
                    longLinePackage,
                    longLineRoot,
                    CancellationToken.None),
                "A TOC line longer than 16 Ki characters must fail with a controlled validation error.");

            AddonPackage normalPackage = fixture.Package("normal-toc", "NormalTocAddon");
            string normalRoot = Path.Combine(fixture.Root, "normal-toc-extracted");
            string normalFolder = Path.Combine(normalRoot, "NormalTocAddon");
            Directory.CreateDirectory(normalFolder);
            File.WriteAllText(
                Path.Combine(normalFolder, "NormalTocAddon.toc"),
                "## Interface: 30403\n## Title: Normal synthetic addon\ncore.lua\n",
                new UTF8Encoding(false));
            await AddonInstallServices.ValidateExtractedFoldersAsync(
                normalPackage,
                normalRoot,
                CancellationToken.None);
            Check(true, "A normal bounded TOC with interface 30403 remains accepted.");

            foreach ((string validName, string oversizedName) in new[]
                     {
                         ("00-valid.toc", "99-oversized.toc"),
                         ("99-valid.toc", "00-oversized.toc")
                     })
            {
                AddonPackage mixedPackage = fixture.Package(
                    "mixed-toc-" + validName[..2],
                    "MixedTocAddon" + validName[..2]);
                string mixedRoot = Path.Combine(fixture.Root, "mixed-toc-extracted-" + validName[..2]);
                string mixedFolder = Path.Combine(mixedRoot, mixedPackage.Folders[0]);
                Directory.CreateDirectory(mixedFolder);
                File.WriteAllText(
                    Path.Combine(mixedFolder, validName),
                    "## Interface: 30403\n",
                    new UTF8Encoding(false));
                using (FileStream oversized = new(
                           Path.Combine(mixedFolder, oversizedName),
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    oversized.SetLength(AddonInstallServices.MaximumCompatibleTocBytes + 1L);
                }

                await ExpectAsync<InvalidDataException>(
                    () => AddonInstallServices.ValidateExtractedFoldersAsync(
                        mixedPackage,
                        mixedRoot,
                        CancellationToken.None),
                    "Every top-level TOC must be scanned, so a valid TOC cannot hide an oversized sibling in either filename order.");
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
            byte[] payload = bytes.ToArray(); string url = "https://animeclub.fr/" + id + ".zip"; Handler.Responses[url] = payload;
            return new() { Id = id, Name = id, Version = "1.0", Interface = "30403", Url = url, Size = payload.Length, Sha256 = Convert.ToHexString(SHA256.HashData(payload)), Folders = [folder] };
        }
        internal void ReplaceCoreArchive(AddonPackage package, string folder, Action<Stream> writeCore)
        {
            using MemoryStream bytes = new();
            using (ZipArchive archive = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
            {
                WriteTextEntry(
                    archive,
                    folder + "/" + folder + ".toc",
                    "## Interface: 30403\n## Title: " + package.Id + "\n## Version: 1.0\ncore.lua\n");
                using (Stream core = archive.CreateEntry(folder + "/core.lua", CompressionLevel.Optimal).Open())
                {
                    writeCore(core);
                }

                WriteTextEntry(archive, folder + "/settings.xml", "<Ui/>");
                WriteTextEntry(archive, folder + "/notes.txt", "Fixture notes");
            }

            byte[] payload = bytes.ToArray();
            Handler.Responses[package.Url] = payload;
            package.Size = payload.Length;
            package.Sha256 = Convert.ToHexString(SHA256.HashData(payload));
            package.InstallHash = "";
        }
        internal void ReplaceArchive(
            AddonPackage package,
            Action<ZipArchive> writeEntries)
        {
            using MemoryStream bytes = new();
            using (ZipArchive archive = new(
                       bytes,
                       ZipArchiveMode.Create,
                       leaveOpen: true))
            {
                writeEntries(archive);
            }

            byte[] payload = bytes.ToArray();
            Handler.Responses[package.Url] = payload;
            package.Size = payload.Length;
            package.Sha256 = Convert.ToHexString(SHA256.HashData(payload));
            package.InstallHash = "";
        }
        internal static void WriteTextEntry(ZipArchive archive, string name, string content)
        {
            using StreamWriter writer = new(
                archive.CreateEntry(name, CompressionLevel.Optimal).Open(),
                new UTF8Encoding(false));
            writer.Write(content);
        }
        internal Task Apply(
            AddonCatalog catalog,
            Dictionary<string, bool> selection,
            bool force = false,
            bool allowExternal = false,
            IProgress<AddonTransferProgress>? progress = null)
            => AddonInstallServices.ApplySelectionAsync(
                _http,
                catalog,
                Root,
                selection,
                progress,
                null,
                CancellationToken.None,
                forceReinstall: force,
                allowExternalReplacement: allowExternal,
                rootLease: GameInstallServices.CreateNoOpGameInstallRootLeaseForTests(
                    Root));
        internal Task<AddonVerificationResult> Verify(AddonCatalog catalog, AddonPackage package)
            => AddonInstallServices.VerifyAsync(catalog, Root, package.Id, CancellationToken.None);
        internal Task<AddonCatalog> Load(AddonCatalog catalog)
        {
            Handler.ResponseFactories.Remove("https://animeclub.fr/catalog.json");
            Handler.Responses["https://animeclub.fr/catalog.json"] = JsonSerializer.SerializeToUtf8Bytes(catalog);
            return AddonInstallServices.LoadCatalogAsync(_http, new("https://animeclub.fr/catalog.json"), CancellationToken.None);
        }
        internal Task<AddonCatalog> LoadFrom(Uri uri)
            => AddonInstallServices.LoadCatalogAsync(_http, uri, CancellationToken.None);
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
        internal Dictionary<string, Func<HttpResponseMessage>> ResponseFactories { get; } = new(StringComparer.Ordinal);
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); string url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            if (ResponseFactories.TryGetValue(url, out Func<HttpResponseMessage>? factory))
                return Task.FromResult(factory());
            if (!Responses.TryGetValue(url, out byte[]? bytes)) throw new InvalidOperationException("Unexpected synthetic HTTP request: " + url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
