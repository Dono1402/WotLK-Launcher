using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WotLK.Launcher;
using WotLK.Launcher.Game;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Updater;

internal static class LauncherSelfUpdateSecurityTests
{
    private const string TestKeyId = "atlas-test-ephemeral-01";
    private const string ProductionKeyId = "atlas-prod-p256-2026-01";
    private const string ProductionPublicKeySha256 =
        "32bb4355e1b49ec59ad757e4bb83ed231da80a2ceae986d2abca89e6fe6faa32";
    private const string PublishedAt = "2026-09-03T04:00:00Z";

    internal static async Task<int> RunAsync()
    {
        VerifyCanonicalPayloadAndSignatureCoverage();
        VerifyPythonOpenSslCompatibilityVector();
        VerifyKeyIsolationAndMalformedSignatures();
        VerifyUriAllowlist();
        await VerifyStrictHttpPipelineAsync();
        await VerifyPackageIntegrityAsync();
        await VerifyStructuredCoordinatorFailuresAsync();
        await VerifyInvalidSignatureCannotReachPackageOrApplicationAsync();
        VerifyLegacyManifestCompatibility();
        VerifyProductionTrustResource();
        Console.WriteLine("Secure launcher update manifest OK (04C.2).");
        return 0;
    }

    internal static async Task<int> RunProductionAsync()
    {
        string root = NewRoot("production");
        string packagePath = Path.Combine(root, "WotLK-Launcher.exe");
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(3));
        using LauncherSelfUpdateHttpClient client = LauncherSelfUpdateHttpClient.CreateProduction();
        try
        {
            LauncherUpdateManifest manifest = await client.LoadManifestAsync(timeout.Token);
            Equal(ProductionKeyId, manifest.KeyId,
                "Le manifeste live doit utiliser l'ancre Atlas approuvée.");
            True(
                Version.TryParse(manifest.Version, out Version? available)
                && available > new Version(1, 1, 0),
                "Le canal signé live doit proposer une version postérieure à 1.1.0.");

            using SocketsHttpHandler legacyHandler = new() { AllowAutoRedirect = false };
            using HttpClient legacyHttp = new(legacyHandler);
            using HttpResponseMessage legacyResponse = await legacyHttp.GetAsync(
                "http://152.228.225.7/launcher/launcher-update.json",
                timeout.Token);
            legacyResponse.EnsureSuccessStatusCode();
            LegacyUpdateManifest? legacy = JsonSerializer.Deserialize<LegacyUpdateManifest>(
                await legacyResponse.Content.ReadAsByteArrayAsync(timeout.Token),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            True(legacy is not null,
                "Le manifeste de transition doit rester lisible par le contrat legacy.");
            Equal(manifest.Version, legacy!.Version,
                "Les endpoints sécurisé et legacy doivent annoncer la même version.");
            Equal(manifest.Url, legacy.Url,
                "Les endpoints sécurisé et legacy doivent annoncer le même package HTTPS.");
            Equal(manifest.Size, legacy.Size,
                "Les endpoints sécurisé et legacy doivent annoncer la même taille.");
            Equal(manifest.Sha256, legacy.Sha256,
                "Les endpoints sécurisé et legacy doivent annoncer le même hash.");

            LauncherSelfUpdateTransferProgress? lastProgress = null;
            Uri packageUri = LauncherSelfUpdateHttpClient.BuildDownloadUri(
                manifest.Url,
                manifest.Version);
            await client.DownloadAsync(
                packageUri,
                packagePath,
                manifest.Size,
                progress => lastProgress = progress,
                timeout.Token);
            await LauncherUpdatePackageIntegrity.ValidateAsync(
                packagePath,
                manifest,
                ComputeSha256Async,
                timeout.Token);

            True(lastProgress is not null
                && lastProgress.BytesProcessed == manifest.Size
                && lastProgress.Percent == 100d,
                "Le téléchargement live doit publier une progression terminale cohérente.");
            Console.WriteLine(
                $"Secure launcher production channel OK (04C.2): version={manifest.Version}, keyId={manifest.KeyId}.");
            return 0;
        }
        finally
        {
            TryDelete(root);
        }
    }

    internal static async Task<int> RunProductionCheckOnlyAsync()
    {
        Equal(
            Uri.UriSchemeHttps,
            LauncherSelfUpdateHttpClient.ManifestUri.Scheme,
            "Le check live doit utiliser exclusivement le manifeste HTTPS approuvé.");

        string launcherPath = Path.Combine(AppContext.BaseDirectory, "WotLK.Launcher.exe");
        True(File.Exists(launcherPath), "L'exécutable launcher compilé est absent du smoke live.");
        Version installedAssemblyVersion = typeof(App).Assembly.GetName().Version
            ?? new Version(0, 0, 0);
        string installedVersion = "v" + installedAssemblyVersion.ToString(3);

        using LauncherSelfUpdateHttpClient productionClient =
            LauncherSelfUpdateHttpClient.CreateProduction();
        CheckOnlyProductionClient checkOnlyClient = new(productionClient);
        using LauncherOperationCoordinator operations = new();
        InertTimer timer = new(LauncherSelfUpdateCoordinator.CheckInterval);
        TrackingRejectingFinalizer finalizer = new();
        using LauncherSelfUpdateCoordinator coordinator = new(
            operations,
            checkOnlyClient,
            finalizer,
            timer,
            automaticChecksEnabled: false,
            installedVersion,
            selfUpdateRecoveryOccurred: false,
            getExecutablePath: () => launcherPath);

        LauncherSelfUpdateCheckResult result = await coordinator.CheckAsync();
        True(
            result.Outcome is LauncherSelfUpdateCheckOutcome.Completed
                or LauncherSelfUpdateCheckOutcome.NoUpdate,
            $"Le check signé live a échoué: {result.Outcome}/{result.ErrorCategory}.");
        LauncherUpdateManifest manifest = checkOnlyClient.Manifest
            ?? throw new InvalidOperationException("Le manifeste live vérifié n'a pas été capturé.");
        Equal(ProductionKeyId, manifest.KeyId,
            "Le manifeste live doit utiliser l'ancre Atlas approuvée.");
        _ = LauncherSelfUpdateHttpClient.BuildDownloadUri(manifest.Url, manifest.Version);
        Equal(1, checkOnlyClient.ManifestRequests,
            "Le check live doit charger un seul manifeste.");
        Equal(0, checkOnlyClient.DownloadRequests,
            "Le check live ne doit télécharger aucun package.");
        Equal(0, finalizer.Calls,
            "Le check live ne doit préparer aucun remplacement.");
        True(!timer.IsEnabled,
            "Le smoke ponctuel ne doit pas démarrer le timer périodique.");

        Console.WriteLine(
            $"Secure launcher production check-only OK: outcome={result.Outcome}, "
            + $"manifestVersion={manifest.Version}, keyId={manifest.KeyId}, download=0, apply=0.");
        return 0;
    }

    private static void VerifyCanonicalPayloadAndSignatureCoverage()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LauncherUpdateManifestVerifier verifier = VerifierFor(TestKeyId, signer);
        LauncherUpdateManifest manifest = CreateManifest([1, 2, 3, 4], "1.2.0");
        Sign(manifest, signer);
        verifier.Verify(manifest);

        string expected = "atlas-launcher-update-manifest-v1\n"
            + "schemaVersion=1\n"
            + "keyId=atlas-test-ephemeral-01\n"
            + "version=1.2.0\n"
            + "size=4\n"
            + $"sha256={manifest.Sha256}\n"
            + "url=https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe\n"
            + "publishedAt=2026-09-03T04:00:00Z\n";
        Equal(
            expected,
            Encoding.UTF8.GetString(LauncherUpdateManifestCanonicalizer.CreatePayload(manifest)),
            "Le payload canonique doit rester stable, explicite et terminé par LF.");

        LauncherUpdateManifest reordered = JsonSerializer.Deserialize<LauncherUpdateManifest>(
            """
            {
              "signature": "pending",
              "publishedAt": "2026-09-03T04:00:00Z",
              "sha256": "PLACEHOLDER",
              "size": 4,
              "url": "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
              "version": "1.2.0",
              "keyId": "atlas-test-ephemeral-01",
              "schemaVersion": 1
            }
            """.Replace("PLACEHOLDER", manifest.Sha256, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Le manifeste réordonné est absent.");
        Equal(
            Convert.ToHexString(LauncherUpdateManifestCanonicalizer.CreatePayload(manifest)),
            Convert.ToHexString(LauncherUpdateManifestCanonicalizer.CreatePayload(reordered)),
            "L'ordre et l'indentation JSON ne doivent pas modifier les octets signés.");

        AssertSignatureMutationRejected(verifier, manifest, value => value.Version = "1.2.1", "version");
        AssertSignatureMutationRejected(verifier, manifest, value => value.Size++, "size");
        AssertSignatureMutationRejected(
            verifier,
            manifest,
            value => value.Sha256 = new string('0', 64),
            "SHA-256");
        AssertSignatureMutationRejected(
            verifier,
            manifest,
            value => value.Url = "https://animeclub.fr/wotlk/launcher/releases/1.2.0/Other.exe",
            "URL");
        AssertSignatureMutationRejected(
            verifier,
            manifest,
            value => value.PublishedAt = "2026-09-03T04:00:01Z",
            "publishedAt");
        LauncherUpdateManifestVerifier multiKeyVerifier = new(
            LauncherUpdateTrustStore.FromSubjectPublicKeys(
            [
                new KeyValuePair<string, byte[]>(
                    TestKeyId,
                    signer.ExportSubjectPublicKeyInfo()),
                new KeyValuePair<string, byte[]>(
                    "atlas-test-ephemeral-02",
                    signer.ExportSubjectPublicKeyInfo())
            ]));
        AssertSignatureMutationRejected(
            multiKeyVerifier,
            manifest,
            value => value.KeyId = "atlas-test-ephemeral-02",
            "keyId");

        LauncherUpdateManifest unsupported = Clone(manifest);
        unsupported.SchemaVersion = 2;
        Throws<LauncherUpdateManifestUnsupportedException>(
            () => verifier.Verify(unsupported),
            "Un schema inconnu doit être refusé avant toute sélection de package.");

        LauncherUpdateManifest nonAscii = Clone(manifest);
        nonAscii.Version = "1.2.0-é";
        Throws<LauncherUpdateManifestFormatException>(
            () => LauncherUpdateManifestCanonicalizer.CreatePayload(nonAscii),
            "Les caractères non ASCII doivent être refusés pour éviter une ambiguïté de normalisation.");
    }

    private static void VerifyKeyIsolationAndMalformedSignatures()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa wrongSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LauncherUpdateManifest manifest = CreateManifest([5, 6, 7], "1.2.0");
        Sign(manifest, signer);

        LauncherUpdateManifestVerifier unknownKey = new(
            LauncherUpdateTrustStore.FromSubjectPublicKeys([]));
        Throws<LauncherUpdateManifestSignatureException>(
            () => unknownKey.Verify(manifest),
            "Un keyId inconnu doit échouer fermé.");

        LauncherUpdateManifestVerifier wrongKey = VerifierFor(TestKeyId, wrongSigner);
        Throws<LauncherUpdateManifestSignatureException>(
            () => wrongKey.Verify(manifest),
            "Une signature issue d'une autre clé doit être refusée.");

        LauncherUpdateManifest unsigned = Clone(manifest);
        unsigned.Signature = string.Empty;
        Throws<LauncherUpdateManifestSignatureException>(
            () => VerifierFor(TestKeyId, signer).Verify(unsigned),
            "Un manifeste non signé doit être refusé.");

        LauncherUpdateManifest invalidBase64 = Clone(manifest);
        invalidBase64.Signature = "not-base64!";
        Throws<LauncherUpdateManifestSignatureException>(
            () => VerifierFor(TestKeyId, signer).Verify(invalidBase64),
            "Une signature Base64 invalide doit être refusée.");

        LauncherUpdateManifest truncated = Clone(manifest);
        truncated.Signature = Convert.ToBase64String(
            Convert.FromBase64String(truncated.Signature)[..12]);
        Throws<LauncherUpdateManifestSignatureException>(
            () => VerifierFor(TestKeyId, signer).Verify(truncated),
            "Une signature DER tronquée doit être refusée.");

        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Throws<InvalidDataException>(
            () => LauncherUpdateTrustStore.FromSubjectPublicKeys(
            [
                new KeyValuePair<string, byte[]>(TestKeyId, p384.ExportSubjectPublicKeyInfo())
            ]),
            "Une clé qui n'est pas ECDSA P-256 doit être refusée par le trust store.");
    }

    private static void VerifyPythonOpenSslCompatibilityVector()
    {
        const string publicKey =
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHT4pzXKh4Kgpe6NNanr48EbgC8LqGSf3+X5eDa57heNEdB0rTpwjqz/OIUtY6K6goPg44KuhT7yl2/LVdjLEjQ==";
        const string json =
            """
            {
              "schemaVersion": 1,
              "keyId": "atlas-test-python-vector-01",
              "version": "1.2.0",
              "url": "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
              "size": 32,
              "sha256": "630dcd2966c4336691125448bbb25b4ff412a49c732db2c8abc1b8581bd710dd",
              "publishedAt": "2026-09-03T04:00:00Z",
              "signature": "MEUCIQDczMXYVoGw5yNBeiNZ6frSij/4kv6APqXEWz/TDknFcQIgHescH2DrGR5EbmttGe1Ea9s1ioMI4cToE/jb2zFPeUw="
            }
            """;

        LauncherUpdateManifest manifest = LauncherUpdateManifestJson.ParseStrict(
            Encoding.UTF8.GetBytes(json));
        LauncherUpdateManifestVerifier verifier = new(
            LauncherUpdateTrustStore.FromSubjectPublicKeys(
            [
                new KeyValuePair<string, byte[]>(
                    "atlas-test-python-vector-01",
                    Convert.FromBase64String(publicKey))
            ]));

        verifier.Verify(manifest);
    }

    private static void VerifyUriAllowlist()
    {
        LauncherUpdateUriPolicy.RequireManifestUri(LauncherUpdateSecurityConstants.ManifestUri);
        Equal(
            "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            LauncherUpdateUriPolicy.RequirePackageUri(
                "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
                "1.2.0").AbsoluteUri,
            "L'URL HTTPS versionnée Atlas doit être acceptée.");

        string[] rejected =
        [
            "http://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://evil.example/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://animeclub.fr.evil.example/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://animeclub.fr@evil.example/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://user@animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://animeclub.fr:443/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://animeclub.fr:444/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://ANIMECLUB.FR/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe",
            "https://animeclub.fr/wotlk/launcher/releases/1.2.0/../WotLK-Launcher.exe",
            "https://animeclub.fr/wotlk/launcher/releases/1.2.0/%2e%2e/WotLK-Launcher.exe",
            "https://animeclub.fr/wotlk/launcher/releases/1.2.0%2fWotLK-Launcher.exe",
            "https://animeclub.fr/wotlk/launcher/releases//1.2.0/WotLK-Launcher.exe",
            "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe?next=1",
            "https://animeclub.fr/wotlk/launcher/releases/1.2.0/WotLK-Launcher.exe#fragment",
            "https://animeclub.fr/wotlk/launcher/releases/1.3.0/WotLK-Launcher.exe",
            "https://animeclub.fr/wotlk/launcher/WotLK-Launcher.exe"
        ];
        foreach (string url in rejected)
        {
            Throws<LauncherUpdateManifestTransportException>(
                () => LauncherUpdateUriPolicy.RequirePackageUri(url, "1.2.0"),
                "L'URL doit être refusée par la politique structurée: " + url);
        }

        Uri[] rejectedManifests =
        [
            new("http://animeclub.fr/wotlk/launcher/launcher-update.json"),
            new("https://evil.example/wotlk/launcher/launcher-update.json"),
            new("https://animeclub.fr.evil.example/wotlk/launcher/launcher-update.json"),
            new("https://user@animeclub.fr/wotlk/launcher/launcher-update.json"),
            new("https://animeclub.fr:444/wotlk/launcher/launcher-update.json"),
            new("https://animeclub.fr/wotlk/launcher/other.json")
        ];
        foreach (Uri uri in rejectedManifests)
        {
            Throws<LauncherUpdateManifestTransportException>(
                () => LauncherUpdateUriPolicy.RequireManifestUri(uri),
                "Le manifeste hors allowlist doit être refusé: " + uri);
        }

        Throws<LauncherUpdateManifestTransportException>(
            () => LauncherUpdateUriPolicy.RequirePackageUri(new Uri(
                "https://animeclub.fr/wotlk/launcher/releases/1.2.0/extra/WotLK-Launcher.exe")),
            "La validation défensive d'un Uri package doit aussi exiger exactement cinq segments.");
    }

    private static async Task VerifyStrictHttpPipelineAsync()
    {
        byte[] package = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LauncherUpdateManifest manifest = CreateManifest(package, "1.2.0");
        Sign(manifest, signer);
        LauncherUpdateManifestVerifier verifier = VerifierFor(TestKeyId, signer);
        int manifestRequests = 0;
        int packageRequests = 0;
        using RoutingHandler handler = new(request =>
        {
            if (request.RequestUri == LauncherUpdateSecurityConstants.ManifestUri)
            {
                manifestRequests++;
                True(request.Headers.Authorization is null,
                    "Le canal public self-update ne doit pas transporter le bearer Atlas.");
                return JsonResponse(manifest);
            }

            packageRequests++;
            True(request.Headers.Authorization is null,
                "Le package public self-update ne doit pas transporter le bearer Atlas.");
            True(request.Headers.TryGetValues("X-WotLK-Launcher-Update", out IEnumerable<string>? values)
                && values.Single() == "1", "Le marqueur de téléchargement legacy doit rester présent.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(package)
            };
        });
        using HttpClient http = new(handler);
        using LauncherSelfUpdateHttpClient client = new(http, verifier);
        LauncherUpdateManifest loaded = await client.LoadManifestAsync(CancellationToken.None);
        Equal("1.2.0", loaded.Version, "Le manifeste signé valide doit être retourné.");

        string root = NewRoot("secure-http");
        try
        {
            string target = Path.Combine(root, "candidate.exe");
            Uri packageUri = LauncherSelfUpdateHttpClient.BuildDownloadUri(
                loaded.Url,
                loaded.Version);
            await client.DownloadAsync(
                packageUri,
                target,
                loaded.Size,
                _ => { },
                CancellationToken.None);
            Equal(package.Length, File.ReadAllBytes(target).Length,
                "Le package exact doit être téléchargé dans la cible temporaire.");
            Equal(1, manifestRequests, "Le manifeste ne doit être obtenu qu'une fois.");
            Equal(1, packageRequests, "Le package ne doit être téléchargé qu'une fois.");
        }
        finally
        {
            TryDelete(root);
        }

        await AssertManifestLoadFailureAsync<LauncherUpdateManifestTransportException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("http://animeclub.fr/wotlk/launcher/launcher-update.json") }
            },
            "Une redirection du manifeste vers HTTP doit être refusée.");
        await AssertManifestLoadFailureAsync<LauncherUpdateManifestTransportException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/launcher-update.json") }
            },
            "Une redirection du manifeste vers un autre host doit être refusée.");
        await AssertManifestLoadFailureAsync<LauncherUpdateManifestUnsupportedException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    new byte[LauncherUpdateSecurityConstants.MaximumManifestBytes + 1])
            },
            "Un manifeste dépassant la borne doit être refusé avant parsing.");

        string jsonWithUnknownField = JsonSerializer.Serialize(manifest)[..^1] + ",\"unexpected\":true}";
        await AssertManifestLoadFailureAsync<JsonException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonWithUnknownField, Encoding.UTF8, "application/json")
            },
            "Le parseur schema 1 doit refuser un champ inconnu.");

        string jsonWithDuplicateVersion = JsonSerializer.Serialize(manifest).Replace(
            "\"version\":\"1.2.0\"",
            "\"version\":\"9.9.9\",\"version\":\"1.2.0\"",
            StringComparison.Ordinal);
        await AssertManifestLoadFailureAsync<JsonException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    jsonWithDuplicateVersion,
                    Encoding.UTF8,
                    "application/json")
            },
            "Le parseur strict doit refuser une propriété JSON dupliquée.");
        await AssertManifestLoadFailureAsync<JsonException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xff, 0x7d])
            },
            "Un manifeste qui n'est pas un document UTF-8 valide doit être refusé.");
        byte[] jsonWithBom = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)))
            .ToArray();
        await AssertManifestLoadFailureAsync<JsonException>(
            verifier,
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(jsonWithBom)
            },
            "Le manifeste doit utiliser UTF-8 sans BOM comme le générateur de publication.");

        using RoutingHandler packageRedirectHandler = new(_ =>
            new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/WotLK-Launcher.exe") }
            });
        using HttpClient redirectHttp = new(packageRedirectHandler);
        using LauncherSelfUpdateHttpClient redirectClient = new(redirectHttp, verifier);
        string redirectRoot = NewRoot("package-redirect");
        try
        {
            await ThrowsAsync<LauncherUpdateManifestTransportException>(
                () => redirectClient.DownloadAsync(
                    new Uri(manifest.Url),
                    Path.Combine(redirectRoot, "candidate.exe"),
                    manifest.Size,
                    _ => { },
                    CancellationToken.None),
                "Une redirection package externe doit être refusée.");
        }
        finally
        {
            TryDelete(redirectRoot);
        }
    }

    private static async Task VerifyPackageIntegrityAsync()
    {
        byte[] package = [10, 20, 30, 40, 50];
        LauncherUpdateManifest manifest = CreateManifest(package, "1.2.0");
        string root = NewRoot("integrity");
        try
        {
            string path = Path.Combine(root, "candidate.exe");
            await File.WriteAllBytesAsync(path, package);
            await LauncherUpdatePackageIntegrity.ValidateAsync(
                path,
                manifest,
                ComputeSha256Async,
                CancellationToken.None);

            await File.WriteAllBytesAsync(path, [10, 20, 30, 40, 51]);
            await ThrowsAsync<LauncherUpdatePackageIntegrityException>(
                () => LauncherUpdatePackageIntegrity.ValidateAsync(
                    path,
                    manifest,
                    ComputeSha256Async,
                    CancellationToken.None),
                "Un package de même taille mais modifié doit échouer au SHA-256.");

            await File.WriteAllBytesAsync(path, [10, 20]);
            await ThrowsAsync<LauncherUpdatePackageIntegrityException>(
                () => LauncherUpdatePackageIntegrity.ValidateAsync(
                    path,
                    manifest,
                    ComputeSha256Async,
                    CancellationToken.None),
                "Un package de taille différente doit être refusé avant application.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task VerifyStructuredCoordinatorFailuresAsync()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LauncherUpdateManifest valid = CreateManifest([1, 3, 3, 7], "1.2.0");
        Sign(valid, signer);
        LauncherUpdateManifest invalidSignature = Clone(valid);
        invalidSignature.Size++;
        Equal(
            LauncherSelfUpdateErrorCategory.ManifestSignatureInvalid,
            await CheckCategoryAsync(JsonResponse(invalidSignature), VerifierFor(TestKeyId, signer)),
            "Le coordinateur doit exposer ManifestSignatureInvalid.");

        HttpResponseMessage redirect = new(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://evil.example/launcher-update.json");
        Equal(
            LauncherSelfUpdateErrorCategory.ManifestTransportRejected,
            await CheckCategoryAsync(redirect, VerifierFor(TestKeyId, signer)),
            "Le coordinateur doit exposer ManifestTransportRejected.");

        LauncherUpdateManifest unsupported = Clone(valid);
        unsupported.SchemaVersion = 2;
        Equal(
            LauncherSelfUpdateErrorCategory.ManifestUnsupported,
            await CheckCategoryAsync(JsonResponse(unsupported), VerifierFor(TestKeyId, signer)),
            "Le coordinateur doit exposer ManifestUnsupported.");

        Equal(
            "La mise à jour n’a pas pu être vérifiée.",
            LauncherSelfUpdateCoordinator.GetUserMessage(
                LauncherSelfUpdateErrorCategory.ManifestSignatureInvalid),
            "L'UI ne doit ni exposer la crypto ni proposer un bypass.");
    }

    private static void VerifyLegacyManifestCompatibility()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LauncherUpdateManifest manifest = CreateManifest([2, 4, 6, 8], "1.2.0");
        Sign(manifest, signer);
        string json = JsonSerializer.Serialize(manifest);
        LegacyUpdateManifest? legacy = JsonSerializer.Deserialize<LegacyUpdateManifest>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        True(legacy is not null, "Le contrat legacy doit ignorer les champs signés supplémentaires.");
        Equal(manifest.Version, legacy!.Version, "La version legacy doit rester lisible.");
        Equal(manifest.Url, legacy.Url, "L'URL HTTPS versionnée doit rester lisible.");
        Equal(manifest.Size, legacy.Size, "La taille legacy doit rester lisible.");
        Equal(manifest.Sha256, legacy.Sha256, "Le hash legacy doit rester lisible.");
    }

    private static async Task VerifyInvalidSignatureCannotReachPackageOrApplicationAsync()
    {
        using ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        LauncherUpdateManifest manifest = CreateManifest([1, 2, 3, 4], "1.2.0");
        Sign(manifest, signer);
        manifest.Sha256 = new string('0', 64);
        int packageRequests = 0;
        using RoutingHandler handler = new(request =>
        {
            if (request.RequestUri == LauncherUpdateSecurityConstants.ManifestUri)
            {
                return JsonResponse(manifest);
            }

            packageRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4])
            };
        });
        using HttpClient http = new(handler);
        using LauncherOperationCoordinator operations = new();
        TrackingRejectingFinalizer finalizer = new();
        string root = NewRoot("invalid-signature-gate");
        string executable = Path.Combine(root, "AtlasLauncher.exe");
        await File.WriteAllBytesAsync(executable, [9, 8, 7, 6]);
        try
        {
            using LauncherSelfUpdateCoordinator coordinator = new(
                operations,
                new LauncherSelfUpdateHttpClient(http, VerifierFor(TestKeyId, signer)),
                finalizer,
                new InertTimer(LauncherSelfUpdateCoordinator.CheckInterval),
                automaticChecksEnabled: false,
                installedVersion: "1.1.0",
                selfUpdateRecoveryOccurred: false,
                getExecutablePath: () => executable,
                writeLog: _ => { });

            LauncherSelfUpdateCheckResult result = await coordinator.CheckAsync();
            Equal(
                LauncherSelfUpdateErrorCategory.ManifestSignatureInvalid,
                result.ErrorCategory,
                "La signature invalide doit arrêter le check avant le package.");
            True(!coordinator.CurrentSnapshot.IsUpdateAvailable,
                "Un manifeste non authentifié ne doit jamais devenir disponible.");
            Equal(
                LauncherSelfUpdateStartStatus.NoUpdate,
                coordinator.TryStartUpdate().Status,
                "Aucun démarrage ne doit être proposé après une signature invalide.");
            Equal(0, packageRequests,
                "Le package ne doit pas être demandé après une signature invalide.");
            Equal(0, finalizer.Calls,
                "Le finalizer atomique ne doit pas recevoir de candidat non authentifié.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void VerifyProductionTrustResource()
    {
        LauncherUpdateTrustStore production = LauncherUpdateTrustStore.LoadEmbeddedProduction();
        True(
            !production.TryGetSubjectPublicKeyInfo(TestKeyId, out _),
            "Une clé de test ne doit jamais être acceptée par le trust store production.");
        True(
            production.TryGetSubjectPublicKeyInfo(ProductionKeyId, out byte[] productionKey),
            "L'ancre de confiance Atlas approuvée doit être embarquée.");
        Equal(
            ProductionPublicKeySha256,
            Convert.ToHexString(SHA256.HashData(productionKey)).ToLowerInvariant(),
            "L'ancre embarquée doit correspondre exactement à la clé publique Atlas.");
        Equal(1, production.Count,
            "Aucune autre ancre de confiance production ne doit être ajoutée silencieusement.");
    }

    private static async Task<LauncherSelfUpdateErrorCategory?> CheckCategoryAsync(
        HttpResponseMessage response,
        LauncherUpdateManifestVerifier verifier)
    {
        string root = NewRoot("coordinator-error");
        string executable = Path.Combine(root, "AtlasLauncher.exe");
        await File.WriteAllBytesAsync(executable, [1, 2, 3]);
        using RoutingHandler handler = new(_ => response);
        using HttpClient http = new(handler);
        using LauncherOperationCoordinator operations = new();
        using LauncherSelfUpdateCoordinator coordinator = new(
            operations,
            new LauncherSelfUpdateHttpClient(http, verifier),
            new RejectingFinalizer(),
            new InertTimer(LauncherSelfUpdateCoordinator.CheckInterval),
            automaticChecksEnabled: false,
            installedVersion: "1.1.0",
            selfUpdateRecoveryOccurred: false,
            getExecutablePath: () => executable,
            writeLog: _ => { });
        try
        {
            LauncherSelfUpdateCheckResult result = await coordinator.CheckAsync();
            Equal(LauncherSelfUpdateCheckOutcome.Failed, result.Outcome,
                "Une violation de sécurité doit produire un échec contrôlé.");
            return result.ErrorCategory;
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task AssertManifestLoadFailureAsync<TException>(
        LauncherUpdateManifestVerifier verifier,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        string message)
        where TException : Exception
    {
        using RoutingHandler handler = new(responseFactory);
        using HttpClient http = new(handler);
        using LauncherSelfUpdateHttpClient client = new(http, verifier);
        await ThrowsAsync<TException>(
            () => client.LoadManifestAsync(CancellationToken.None),
            message);
    }

    private static LauncherUpdateManifest CreateManifest(byte[] package, string version) => new()
    {
        SchemaVersion = 1,
        KeyId = TestKeyId,
        Version = version,
        Url = $"https://animeclub.fr/wotlk/launcher/releases/{version}/WotLK-Launcher.exe",
        Size = package.LongLength,
        Sha256 = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant(),
        PublishedAt = PublishedAt,
        Signature = string.Empty
    };

    private static void Sign(LauncherUpdateManifest manifest, ECDsa signer)
    {
        manifest.Signature = Convert.ToBase64String(signer.SignData(
            LauncherUpdateManifestCanonicalizer.CreatePayload(manifest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }

    private static LauncherUpdateManifestVerifier VerifierFor(string keyId, ECDsa signer) => new(
        LauncherUpdateTrustStore.FromSubjectPublicKeys(
        [
            new KeyValuePair<string, byte[]>(keyId, signer.ExportSubjectPublicKeyInfo())
        ]));

    private static LauncherUpdateManifest Clone(LauncherUpdateManifest manifest) =>
        JsonSerializer.Deserialize<LauncherUpdateManifest>(JsonSerializer.Serialize(manifest))
        ?? throw new InvalidOperationException("Le clone de manifeste est absent.");

    private static void AssertSignatureMutationRejected(
        LauncherUpdateManifestVerifier verifier,
        LauncherUpdateManifest source,
        Action<LauncherUpdateManifest> mutate,
        string field)
    {
        LauncherUpdateManifest changed = Clone(source);
        mutate(changed);
        Throws<LauncherUpdateManifestSignatureException>(
            () => verifier.Verify(changed),
            "La modification du champ signé doit invalider la signature: " + field);
    }

    private static HttpResponseMessage JsonResponse(LauncherUpdateManifest manifest) => new(
        HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(manifest),
            Encoding.UTF8,
            "application/json")
    };

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static string NewRoot(string scenario)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "AtlasSecureSelfUpdate",
            scenario,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(message);
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

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Attendu={expected}; actuel={actual}.");
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RoutingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponseMessage response = responseFactory(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class InertTimer(TimeSpan interval) : ILauncherSelfUpdateTimer
    {
        public event EventHandler? Tick
        {
            add { }
            remove { }
        }

        public TimeSpan Interval { get; } = interval;

        public bool IsEnabled { get; private set; }

        public void Start() => IsEnabled = true;

        public void Stop() => IsEnabled = false;
    }

    private sealed class RejectingFinalizer : ILauncherSelfUpdateFinalizer
    {
        public Task<LauncherUpdateTransaction> PrepareAndLaunchAsync(
            string targetPath,
            string downloadedCandidatePath,
            long expectedSize,
            string expectedSha256,
            string authenticatedTargetVersion,
            int parentProcessId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Le finalizer ne doit pas être appelé par ces tests.");
    }

    private sealed class TrackingRejectingFinalizer : ILauncherSelfUpdateFinalizer
    {
        internal int Calls { get; private set; }

        public Task<LauncherUpdateTransaction> PrepareAndLaunchAsync(
            string targetPath,
            string downloadedCandidatePath,
            long expectedSize,
            string expectedSha256,
            string authenticatedTargetVersion,
            int parentProcessId,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Le finalizer ne doit pas être appelé.");
        }
    }

    private sealed class CheckOnlyProductionClient(
        ILauncherSelfUpdateClient inner) : ILauncherSelfUpdateClient
    {
        internal int ManifestRequests { get; private set; }

        internal int DownloadRequests { get; private set; }

        internal LauncherUpdateManifest? Manifest { get; private set; }

        public async Task<LauncherUpdateManifest> LoadManifestAsync(
            CancellationToken cancellationToken)
        {
            ManifestRequests++;
            using CancellationTokenSource timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Manifest = await inner.LoadManifestAsync(timeout.Token);
            return Manifest;
        }

        public Task DownloadAsync(
            Uri uri,
            string targetPath,
            long expectedSize,
            Action<LauncherSelfUpdateTransferProgress> reportProgress,
            CancellationToken cancellationToken)
        {
            DownloadRequests++;
            throw new InvalidOperationException(
                "Le check live ne doit jamais atteindre le téléchargement.");
        }
    }

    private sealed class LegacyUpdateManifest
    {
        public string Version { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public long Size { get; set; }

        public string Sha256 { get; set; } = string.Empty;
    }
}
