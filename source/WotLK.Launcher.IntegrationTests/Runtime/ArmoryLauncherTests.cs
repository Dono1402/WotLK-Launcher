using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WotLK.Launcher;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Preview;
using WotLK.Launcher.UI.V2.Views;

internal static class ArmoryLauncherTests
{
    internal static async Task<int> RunAsync(string? captureDirectory)
    {
        await ArmoryBannerStoreTests.RunAsync();
        using ArmoryFixture fixture = new();
        await ValidateLocalHostAsync(fixture);
        await RunWpfAsync(fixture, captureDirectory);
        Console.WriteLine("Armory launcher integration OK: private loopback host, account isolation, embedded WebView2, profile navigation, FR/EN, logout and restart.");
        return 0;
    }

    private static async Task ValidateLocalHostAsync(ArmoryFixture fixture)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
        using LauncherArmoryLocalHost first = new();
        using LauncherArmoryLocalHost second = new();
        True(Regex.IsMatch(first.Key, "^[a-f0-9]{64}$"), "La clé locale doit contenir 256 bits aléatoires.");
        True(first.Key != second.Key, "Deux sessions locales doivent utiliser des clés distinctes.");
        await first.StartAsync(42, fixture.Configuration, CancellationToken.None);
        Uri origin = first.Origin ?? throw new InvalidOperationException("Le serveur local n'a pas publié son origine.");
        True(first.Owns(new Uri(origin, "characters.json").AbsoluteUri), "L'origine HTTP locale doit être reconnue.");
        foreach (string invalid in new[]
        {
            "https://example.test/", "file:///C:/Windows/win.ini", "about:blank", "not a URL",
            $"http://localhost:{origin.Port}/", $"http://127.0.0.1:{origin.Port + (origin.Port == 65535 ? -1 : 1)}/",
            $"http://user@127.0.0.1:{origin.Port}/", $"https://127.0.0.1:{origin.Port}/"
        })
        {
            True(!first.Owns(invalid), "Une origine différente ne doit jamais recevoir la clé de session.");
        }
        using (HttpResponseMessage anonymous = await http.GetAsync(new Uri(origin, "characters.json")))
            Equal(HttpStatusCode.Forbidden, anonymous.StatusCode, "Le roster ne doit pas être accessible sans clé.");
        using (HttpResponseMessage wrongKey = await SendAsync(http, origin, "characters.json", second.Key))
            Equal(HttpStatusCode.Forbidden, wrongKey.StatusCode, "La clé d'une autre session doit être refusée.");
        using (HttpResponseMessage health = await SendAsync(http, origin, "health.json", first.Key))
        {
            health.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            Equal("atlas-launcher-armory", document.RootElement.GetProperty("protocol").GetString(), "Le protocole annoncé doit être celui de l'armurerie.");
        }
        using (HttpResponseMessage roster = await SendAsync(http, origin, "characters.json", first.Key))
        {
            roster.EnsureSuccessStatusCode();
            using JsonDocument document = JsonDocument.Parse(await roster.Content.ReadAsStringAsync());
            JsonElement characters = document.RootElement.GetProperty("characters");
            Equal(3, characters.GetArrayLength(), "Les personnages sans modèle et sans capture doivent rester dans le roster.");
            Equal("Mage42", characters[0].GetProperty("name").GetString(), "L'identifiant du compte doit être transmis au processus enfant.");
        }
        first.Dispose();
        first.Dispose();
        True(first.Origin is null && !first.Owns(origin.AbsoluteUri), "La fermeture doit invalider l'origine immédiatement.");
        await AssertStoppedAsync(origin);

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        using LauncherArmoryLocalHost aborted = new();
        try
        {
            await aborted.StartAsync(42, fixture.Configuration, cancelled.Token);
            throw new InvalidOperationException("Un démarrage annulé ne doit pas réussir.");
        }
        catch (OperationCanceledException) { }
        True(aborted.Origin is null, "Un démarrage annulé doit nettoyer son serveur.");
        Console.WriteLine("Armory loopback host OK: authenticated HTTP, account environment, origin boundary and shutdown.");
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient http, Uri origin, string route, string key)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(origin, route));
        request.Headers.Add("X-Atlas-Armory-Key", key);
        return SendAndDisposeAsync();

        async Task<HttpResponseMessage> SendAndDisposeAsync()
        {
            using (request) return await http.SendAsync(request);
        }
    }

    private static async Task RunWpfAsync(ArmoryFixture fixture, string? captureDirectory)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
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
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);

            async Task RunAsync()
            {
                Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                string originalLocale = LauncherLocalization.CurrentLocale;
                try
                {
                    foreach (string path in new[]
                    {
                        "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Tokens.xaml",
                        "/WotLK.Launcher;component/Assets/Icons/AtlasV2.Icons.xaml",
                        "/WotLK.Launcher;component/UI/V2/Resources/AtlasV2.Controls.xaml"
                    }) application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(path, UriKind.Relative) });
                    LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
                    await ValidateAvatarPublishingAndCustomizationAsync(fixture, captureDirectory);
                    await ValidateEmbeddedShellAsync(fixture, captureDirectory);
                    await ValidateBannerBridgeAsync(fixture);
                    await ValidateCancelledAccountLookupAsync(fixture);
                    await ValidateUnavailableArmoryRecoveryAsync(fixture);
                    if (Environment.GetEnvironmentVariable("ATLAS_ARMORY_PUBLIC_SMOKE") is string input)
                        await ValidatePackagedShellAsync(fixture, input, captureDirectory);
                }
                catch (Exception error) { failure ??= error; }
                finally
                {
                    LauncherLocalization.SetLocale(originalLocale);
                    application.Shutdown();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }
        }) { IsBackground = true, Name = "AtlasArmoryWpfHarness" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(6));
    }

    private static async Task ValidatePackagedShellAsync(ArmoryFixture fixture, string inputPath, string? captureDirectory)
    {
        True(!LauncherBuildFlavor.IsLocalClient && LauncherBuildFlavor.IsSelfUpdateEnabled,
            "Le test empaqueté doit utiliser les fonctionnalités du client public.");
        True(LauncherWebViewRuntime.IsSupported(LauncherWebViewRuntime.InstalledVersion()),
            "Le test exige un WebView existant et ne doit jamais installer de composant sur le PC.");
        using JsonDocument input = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath));
        JsonElement config = input.RootElement;
        uint accountId = config.GetProperty("accountId").GetUInt32();
        JsonElement roster = config.GetProperty("roster").Clone();
        JsonElement catalog = config.GetProperty("catalog").Clone();
        string expectedName = roster.GetProperty("characters")[0].GetProperty("character").GetProperty("name").GetString()!;
        string runtimeRoot;
        string payloadPath = Environment.GetEnvironmentVariable("ATLAS_ARMORY_PUBLIC_PAYLOAD")
            ?? throw new InvalidOperationException("ATLAS_ARMORY_PUBLIC_PAYLOAD requis pour le smoke public.");
        using (Stream payload = typeof(LauncherArmoryPackage).Assembly.GetManifestResourceStream(LauncherArmoryPackage.ResourceName)
            ?? throw new InvalidOperationException("Le runtime doit être réellement embarqué dans l'assembly public testé."))
        {
            using Stream expectedPayload = File.OpenRead(payloadPath);
            Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedPayload)),
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "Le ZIP embarqué doit correspondre au paquet vérifié.");
            payload.Position = 0;
            runtimeRoot = LauncherArmoryPackage.Extract(payload, Path.Combine(fixture.PublicRoot, "runtime"));
        }
        LauncherArmoryLocalConfiguration configuration = new(
            Path.Combine(runtimeRoot, "node/node.exe"), Path.Combine(runtimeRoot, "app/launcher-server.cjs"),
            IsPackaged: true, ClientRoot: config.GetProperty("clientRoot").GetString(),
            DataRoot: config.GetProperty("dataRoot").GetString(), VendorRoot: Path.Combine(runtimeRoot, "vendor/wow-export/src/js"),
            AssetRoot: Path.Combine(runtimeRoot, "assets"), MetadataRoot: Path.Combine(runtimeRoot, "metadata"));
        AccountUiState state = ConnectedAccount("PublicRelease140");
        LauncherShellV2 window = CreateShell(state);
        ArmoryViewV2 armory = Required<ArmoryViewV2>(window, "ArmoryView");
        List<string> operations = [];
        armory.Configure(_ => Task.FromResult<uint?>(accountId), state, () => configuration,
            Path.Combine(fixture.PublicRoot, "webview-data"), bannerStore: new ArmoryBannerStore(Path.Combine(fixture.PublicRoot, "banner-store")),
            readData: (account, request, token) =>
            {
                token.ThrowIfCancellationRequested();
                Equal(accountId, account, "Le pont doit utiliser uniquement le compte actif.");
                True(request.IsValid, "Le processus empaqueté doit émettre un RPC valide.");
                lock (operations) operations.Add(request.Operation);
                return Task.FromResult(request.Operation == "roster" ? roster : catalog);
            });
        ShowOffscreen(window);
        Uri? origin = null;
        try
        {
            LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
            await OpenProfileAsync(window);
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent === 'PublicRelease140' && document.querySelectorAll('.character').length > 0",
                "Le paquet public doit charger le profil et le roster du RPC C#.");
            origin = new Uri(armory.Browser!.CoreWebView2.Source);
            await WaitForScriptAsync(armory, "document.getElementById('character-view').contentWindow.armory?.ready === true",
                "La fiche empaquetée doit charger dans WebView2.");
            await WaitForScriptAsync(armory, "document.getElementById('character-view').contentWindow.armory?.root.children.length > 0 && document.getElementById('character-view').contentWindow.armory?.frames > 2",
                "Le modèle exporté par le paquet doit rendre réellement dans WebView2.", 180);
            using (JsonDocument rendered = await ScriptAsync(armory,
                "({name:document.getElementById('character-view').contentWindow.armory.data.name, slots:document.getElementById('character-view').contentDocument.querySelectorAll('.slot').length, frames:document.getElementById('character-view').contentWindow.armory.frames})"))
            {
                Equal(expectedName, rendered.RootElement.GetProperty("name").GetString(), "La fiche publique doit correspondre au personnage autorisé.");
                Equal(19, rendered.RootElement.GetProperty("slots").GetInt32(), "Le paquet doit exposer les dix-neuf emplacements.");
                if (captureDirectory is not null) await File.WriteAllTextAsync(Path.Combine(captureDirectory, "armory-public-render.json"), rendered.RootElement.GetRawText());
            }
            await ValidateEmbeddedInterFontsAsync(armory, captureDirectory);
            await SaveCaptureAsync(armory, captureDirectory, "armory-public-140-fr.png");
            await ScriptAsync(armory, "document.getElementById('edit-profile').click(); true");
            await WaitForScriptAsync(armory, "!document.getElementById('profile-editor').hidden", "Le profil public doit ouvrir son éditeur intégré.");
            Equal(LauncherShellPage.Armory, window.CurrentPage, "L'éditeur doit rester dans le profil public.");
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
            await WaitForScriptAsync(armory, "document.documentElement.lang === 'en'", "Le profil empaqueté doit basculer en anglais.");
            await SaveCaptureAsync(armory, captureDirectory, "armory-public-140-en.png");
            lock (operations) True(operations.Contains("roster"), "Le paquet doit avoir réellement demandé des données au pont C#.");
            AssertOffscreen(window);
            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            await WaitUntilAsync(() => armory.Browser is null, "La déconnexion publique doit fermer la WebView.");
            await AssertStoppedAsync(origin);
        }
        finally
        {
            window.Close();
            await PumpAsync();
        }
        if (origin is not null) await AssertStoppedAsync(origin);
        Console.WriteLine("Public packaged WPF OK: real SHA-verified ZIP, embedded Node and relocated assets, C# RPC roster, real Three.js model, 19 equipment slots, Inter fonts, profile editor, FR/EN and logout. No system installer or user-window activation.");
    }

    private static async Task ValidateEmbeddedShellAsync(ArmoryFixture fixture, string? captureDirectory)
    {
        AccountUiState state = ConnectedAccount("FirstAccount");
        LauncherShellV2 window = CreateShell(state);
        ArmoryViewV2 armory = Required<ArmoryViewV2>(window, "ArmoryView");
        uint accountId = 42;
        armory.Configure(_ => Task.FromResult<uint?>(accountId), state, () => fixture.Configuration,
            fixture.WebViewDataDirectory, bannerStore: new ArmoryBannerStore(fixture.BannerStoreDirectory));
        ShowOffscreen(window);
        Uri? finalOrigin = null;
        try
        {
            await PumpAsync();
            True(!window.IsPreviewMode, "Le shell doit exercer les règles réelles de navigation et déconnexion.");
            await OpenProfileAsync(window);
            Equal(LauncherShellPage.Armory, window.CurrentPage, "Profil doit ouvrir l'armurerie intégrée.");
            await WaitForScriptAsync(armory, "document.querySelectorAll('.character').length === 3 && document.getElementById('profile-name').textContent === 'FirstAccount'", "Le roster et le profil WPF doivent apparaître dans WebView2.");
            Uri origin = new(armory.Browser!.CoreWebView2.Source);
            await WaitForScriptAsync(armory, "document.getElementById('character-view').contentWindow.armory?.ready === true", "La vue du personnage doit charger dans la WebView intégrée.");
            using (JsonDocument result = await ScriptAsync(armory, "({name:document.getElementById('character-view').contentWindow.armory.data.name,slots:document.getElementById('character-view').contentDocument.querySelectorAll('.slot').length,frames:document.getElementById('character-view').contentWindow.armory.frames})"))
            {
                Equal("Mage42", result.RootElement.GetProperty("name").GetString(), "Le personnage du compte actif doit être affiché.");
                Equal(19, result.RootElement.GetProperty("slots").GetInt32(), "Les dix-neuf emplacements d'équipement doivent être consultables.");
            }
            if (fixture.HasModel)
            {
                await WaitForScriptAsync(armory, "document.getElementById('character-view').contentWindow.armory.root.children.length > 0 && document.getElementById('character-view').contentWindow.armory.frames > 2", "Le modèle Three.js doit rendre réellement dans WebView2.");
                Console.WriteLine("Armory embedded Three.js model OK (local existing model fixture).");
            }
            await ValidateEmbeddedInterFontsAsync(armory, captureDirectory);
            await ValidateProfileTitleBarAsync(window, armory, captureDirectory);
            await SaveCaptureAsync(armory, captureDirectory, "armory-webview-fr.png");
            CancellationToken profileSession = await ValidateProfileBridgeAsync(window, armory, state);

            await ScriptAsync(armory, "document.querySelector('.character[data-id=\"12\"]').click(); true");
            await WaitForScriptAsync(armory, "document.getElementById('character-view').contentWindow.armory?.data?.name === 'Alt42' && document.getElementById('character-view').contentWindow.armory?.ready === true", "Un autre personnage doit garder son équipement même sans modèle 3D.");
            await WaitForScriptAsync(armory, "document.getElementById('character-view').contentDocument.body.textContent.includes('Modèle 3D indisponible')", "L'absence de modèle doit être expliquée explicitement.");
            await ScriptAsync(armory, "document.querySelector('.character[data-id=\"13\"]').click(); true");
            await WaitForScriptAsync(armory, "document.getElementById('character-view').hidden && !document.getElementById('empty-state').hidden", "Le personnage en attente doit rester sélectionnable avec un état clair.");

            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
            await WaitForScriptAsync(armory, "document.documentElement.lang === 'en' && document.getElementById('edit-profile') && !document.getElementById('customize')", "Changer de langue doit actualiser la page intégrée sans rétablir l'ancien bouton.");
            await SaveCaptureAsync(armory, captureDirectory, "armory-webview-en.png");
            await ScriptAsync(armory, "document.getElementById('edit-profile').click(); true");
            await WaitForScriptAsync(armory, "!document.getElementById('profile-editor').hidden", "Personnaliser doit ouvrir le formulaire dans l'armurerie.");
            Equal(LauncherShellPage.Armory, window.CurrentPage, "Personnaliser ne doit pas changer de page.");
            True(!window.AccountPage.IsVisible, "Les réglages Compte ne doivent pas remplacer le profil.");
            await ScriptAsync(armory, "document.getElementById('cancel-profile').click(); true");

            await OpenProfileAsync(window);
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent === 'FirstAccount'", "Revenir au profil doit restaurer la page intégrée.");
            TaskCompletionSource<bool> navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            armory.Browser!.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (args.Uri == "https://example.test/") navigation.TrySetResult(args.Cancel);
            };
            armory.Browser.CoreWebView2.Navigate("https://example.test/");
            True(await navigation.Task.WaitAsync(TimeSpan.FromSeconds(3)), "Les navigations externes doivent être annulées avant tout accès réseau.");

            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            await WaitUntilAsync(() => armory.Browser is null && window.CurrentPage == LauncherShellPage.Game, "La déconnexion doit fermer le profil et sa WebView.");
            True(profileSession.IsCancellationRequested, "Une sélection avatar tardive doit être invalidée après déconnexion.");
            await AssertStoppedAsync(origin);
            accountId = 84;
            state.ApplyRuntimeView(ConnectedAccount("SecondAccount").Current);
            await OpenProfileAsync(window);
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent === 'SecondAccount' && document.querySelector('.character strong')?.textContent === 'Mage84'", "La reconnexion doit charger uniquement les données du nouveau compte.");
            finalOrigin = new Uri(armory.Browser!.CoreWebView2.Source);
            using (JsonDocument text = await ScriptAsync(armory, "document.body.textContent"))
                True(!text.RootElement.GetString()!.Contains("FirstAccount", StringComparison.Ordinal), "L'ancien compte ne doit plus être présent dans le document.");
        }
        finally
        {
            window.Close();
            await PumpAsync();
        }
        True(armory.Browser is null, "Fermer le launcher doit disposer la WebView.");
        if (finalOrigin is not null) await AssertStoppedAsync(finalOrigin);
        Console.WriteLine("Armory WPF shell OK: roster, equipment without model, profile bridge, localization, customize, navigation confinement and logout.");
    }

    private static async Task ValidateProfileTitleBarAsync(LauncherShellV2 window, ArmoryViewV2 armory, string? captureDirectory)
    {
        Border titleBar = Required<Border>(window, "TitleBar");
        Border hoverZone = Required<Border>(window, "ProfileTitleBarHoverZone");
        TranslateTransform translation = Required<TranslateTransform>(window, "ProfileTitleBarTransform");
        FrameworkElement browser = armory.Browser ?? throw new InvalidOperationException("La barre doit être testée au-dessus de la vraie WebView2 de composition.");
        FrameworkElement content = (FrameworkElement)window.Content;
        List<object> checks = [];
        int dragRequests = 0;
        EventHandler dragRequested = (_, _) => dragRequests++;
        armory.WindowDragRequested += dragRequested;
        await WaitForScriptAsync(armory, "document.querySelector('.banner-image').naturalWidth > 0", "La bannière doit être chargée avant la mesure du profil.");
        await WaitUntilAsync(() => !titleBar.IsVisible, "La barre doit se masquer à l'entrée sur Profil.");
        window.UpdateLayout();
        ProfileLayoutEvidence baseline = await ReadProfileLayoutAsync(window, armory);
        True(Math.Abs(baseline.Browser.X) < .75 && Math.Abs(baseline.Browser.Y) < .75
            && Math.Abs(baseline.Browser.Width - content.ActualWidth) < .75
            && Math.Abs(baseline.Browser.Height - content.ActualHeight) < .75,
            "Le contrôle de composition doit occuper tout le shell, sans rangée réservée à la barre.");
        True(Math.Abs(baseline.Hero.X) < .75 && Math.Abs(baseline.Hero.Y) < .75
            && Math.Abs(baseline.Hero.Width - baseline.ViewportWidth) < .75
            && Math.Abs(baseline.Banner.X) < .75 && Math.Abs(baseline.Banner.Y) < .75
            && Math.Abs(baseline.Banner.Width - baseline.ViewportWidth) < .75,
            "La bannière du profil doit commencer en haut à gauche et couvrir toute la largeur de la WebView.");
        Equal(2, Grid.GetRowSpan(armory), "L'armurerie doit traverser les deux rangées du shell.");
        True(Panel.GetZIndex(titleBar) > Panel.GetZIndex(hoverZone) && Panel.GetZIndex(hoverZone) > Panel.GetZIndex(armory),
            "La barre et sa zone d'apparition doivent passer au-dessus de la WebView de composition.");

        try
        {
            AssertHidden();
            True(HitBelongsTo(window.InputHitTest(hoverZone.TranslatePoint(new Point(hoverZone.ActualWidth / 2, 12), window)), hoverZone),
                "Le haut du profil doit atteindre la zone de survol malgré la vraie WebView2.");
            Point underTitleBar = new(content.ActualWidth / 2, titleBar.Margin.Top + titleBar.ActualHeight / 2);
            True(HitBelongsTo(window.InputHitTest(underTitleBar), browser),
                "Une barre masquée doit laisser la bannière recevoir les entrées hors de la zone de survol.");
            await RecordAsync("hidden");
            await CaptureTitleBarAsync("hidden");

            Rect originalWindowBounds = new(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            System.Reflection.MethodInfo shellDragMethod = typeof(LauncherShellV2).GetMethod("ArmoryView_WindowDragRequested",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            EventHandler shellDrag = shellDragMethod.CreateDelegate<EventHandler>(window);
            // Isolate the native move loop even if the user happens to hold their mouse button during the test.
            armory.WindowDragRequested -= shellDrag;
            try
            {
                await ScriptAsync(armory, "document.querySelector('.banner').dispatchEvent(new PointerEvent('pointerdown',{bubbles:true,cancelable:true,pointerType:'mouse',isPrimary:true,button:0,clientX:240,clientY:12}));true");
                await WaitUntilAsync(() => dragRequests == 1, "La partie haute vide du profil doit transmettre la demande de déplacement au contrôle natif.");
            }
            finally { armory.WindowDragRequested += shellDrag; }
            Equal(originalWindowBounds, new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight),
                "La vérification du message doit conserver la fenêtre de test hors écran sans boucle de déplacement native.");
            await RecordAsync("profile-blank-top-drag-bridge");

            foreach (string selector in new[] { "#profile-hero", ".avatar" })
            {
                await ScriptAsync(armory, $"(() => {{ const rect=document.querySelector('{selector}').getBoundingClientRect(); document.querySelector('#profile-hero').dispatchEvent(new PointerEvent('pointerenter',{{bubbles:false,pointerType:'mouse',clientX:rect.x+rect.width/2,clientY:rect.y+rect.height/2}})); return true; }})()");
                await WaitUntilAsync(() => titleBar.IsVisible && Math.Abs(translation.Y) < .1,
                    "Le survol de toute la bannière ou de l'avatar doit révéler la barre native.");
                await Task.Delay(260);
                RaiseMouseEvent(titleBar, UIElement.MouseLeaveEvent);
                RaiseMouseEvent(hoverZone, UIElement.MouseLeaveEvent);
                await Task.Delay(480);
                AssertShown();
                AssertTitleBarHit("ProfileButton");
                await RecordAsync("web-hover-" + selector[1..]);
                bool? hideStartedOnLeave = null;
                EventHandler<ArmoryHeaderHoverEventArgs> leaveProbe = (_, e) =>
                {
                    if (e.Hovered) return;
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    bool requestedVisible = (bool)typeof(LauncherShellV2).GetField("_profileTitleBarVisible", flags)!.GetValue(window)!;
                    DispatcherTimer timer = (DispatcherTimer)typeof(LauncherShellV2).GetField("_profileTitleBarHideTimer", flags)!.GetValue(window)!;
                    hideStartedOnLeave = !requestedVisible && !timer.IsEnabled;
                };
                armory.HeaderHoverChanged += leaveProbe;
                try
                {
                    await ScriptAsync(armory, "document.querySelector('#profile-hero').dispatchEvent(new PointerEvent('pointerleave',{bubbles:false,pointerType:'mouse'}));true");
                    await WaitUntilAsync(() => hideStartedOnLeave.HasValue, "La sortie de bannière doit atteindre le shell.");
                    True(hideStartedOnLeave == true, "Le masquage doit commencer dès le signal de sortie, sans temporisation préalable.");
                    await WaitUntilAsync(() => !titleBar.IsVisible, "Quitter la bannière doit terminer le glissement de la barre native.");
                    await RecordAsync("immediate-web-leave-" + selector[1..]);
                }
                finally { armory.HeaderHoverChanged -= leaveProbe; }
            }

            await RevealProfileTitleBarAsync(window);
            AssertTitleBarHit("ProfileButton");
            AssertTitleBarHit("GameNavigationButton");
            AssertTitleBarHit("CloseWindowButton");
            Equal(baseline, await ReadProfileLayoutAsync(window, armory), "Afficher la barre ne doit déplacer ni la bannière ni le personnage.");
            True(hoverZone.ActualHeight > titleBar.Margin.Top + titleBar.ActualHeight,
                "La zone de survol doit couvrir le trajet entre le haut du profil et la barre affichée.");
            await RecordAsync("shown");
            await CaptureTitleBarAsync("shown");

            // Routed WPF events only: no SetCursorPos, SendInput, focus, capture or OS keyboard input.
            RaiseMouseEvent(titleBar, UIElement.MouseLeaveEvent);
            await Task.Delay(80);
            RaiseMouseEvent(hoverZone, UIElement.MouseEnterEvent);
            await Task.Delay(450);
            await PumpAsync();
            AssertShown();
            await RecordAsync("reenter-cancels-hide");

            RaiseMouseEvent(titleBar, UIElement.MouseLeaveEvent);
            // Advance the timer deterministically; do not rely on a scheduler wake-up inside
            // the short 160 ms exit animation, and never change the control's visual properties.
            System.Reflection.MethodInfo hideTick = typeof(LauncherShellV2).GetMethod("ProfileTitleBarHideTimer_Tick",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Le délai de masquage du profil doit avoir son gestionnaire WPF.");
            hideTick.Invoke(window, [null, EventArgs.Empty]);
            if (SystemParameters.ClientAreaAnimation)
            {
                True(titleBar.IsVisible && titleBar.IsHitTestVisible && translation.HasAnimatedProperties,
                    "La barre qui commence à se retirer doit encore pouvoir recevoir un nouveau survol.");
                RaiseMouseEvent(titleBar, UIElement.MouseEnterEvent);
                await Task.Delay(240);
                await PumpAsync();
                AssertShown();
                AssertTitleBarHit("ProfileButton");
                Equal(baseline, await ReadProfileLayoutAsync(window, armory), "Inverser le masquage ne doit pas déplacer le profil.");
                await RecordAsync("reenter-during-hide");
            }
            else
            {
                AssertHidden();
                await RecordAsync("reduced-motion-immediate-hide");
                await RevealProfileTitleBarAsync(window);
            }
            await HideAsync();
            Equal(baseline, await ReadProfileLayoutAsync(window, armory), "Masquer la barre doit conserver les dimensions et la position du profil.");
            True(HitBelongsTo(window.InputHitTest(underTitleBar), browser), "La WebView doit retrouver les entrées après le masquage.");

            foreach ((string name, Action<bool> setOpen) in new (string, Action<bool>)[]
            {
                ("profile", value => window.ProfileState.IsOpen = value),
                ("friends", value => window.FriendsState.IsOpen = value),
                ("activity", value => window.ActivityState.IsOpen = value)
            })
            {
                await RevealProfileTitleBarAsync(window);
                setOpen(true);
                await PumpAsync();
                RaiseMouseEvent(titleBar, UIElement.MouseLeaveEvent);
                RaiseMouseEvent(hoverZone, UIElement.MouseLeaveEvent);
                await Task.Delay(480);
                await PumpAsync();
                AssertShown();
                Equal(baseline, await ReadProfileLayoutAsync(window, armory), $"Le panneau {name} ne doit pas redimensionner le profil.");
                await RecordAsync(name + "-pins-title-bar");
                setOpen(false);
                await WaitUntilAsync(() => !titleBar.IsVisible, $"Fermer le panneau {name} doit permettre le masquage différé.");
                AssertHidden();
            }

            foreach ((string buttonName, LauncherShellPage page) in new[]
            {
                ("GameNavigationButton", LauncherShellPage.Game),
                ("AddonsNavigationButton", LauncherShellPage.Addons),
                ("PatchNotesNavigationButton", LauncherShellPage.PatchNotes),
                ("SettingsButton", LauncherShellPage.Settings)
            })
            {
                await RevealProfileTitleBarAsync(window);
                AssertTitleBarHit(buttonName);
                RaiseMouseEvent(titleBar, UIElement.MouseLeaveEvent);
                Required<Button>(window, buttonName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Equal(page, window.CurrentPage, "Le bouton de la barre révélée doit ouvrir sa page habituelle.");
                await AssertNormalPageAsync(page.ToString());
                await OpenProfileAsync(window);
                await WaitUntilAsync(() => !titleBar.IsVisible, "Revenir sur Profil doit masquer à nouveau la barre.");
                True(ReferenceEquals(browser, armory.Browser), "Les allers-retours de navigation doivent conserver la même WebView2.");
                Equal(baseline, await ReadProfileLayoutAsync(window, armory), "Revenir sur Profil doit retrouver sa géométrie intégrale.");
            }

            await RevealProfileTitleBarAsync(window);
            Required<Button>(window, "ProfileButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Required<Button>(window.ProfileOverlay, "ManageAccountButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(LauncherShellPage.Account, window.CurrentPage, "Le menu Profil doit conserver l'accès aux réglages Compte.");
            await AssertNormalPageAsync("Account");
            await OpenProfileAsync(window);
            await WaitUntilAsync(() => !titleBar.IsVisible, "Le retour depuis Compte doit rétablir le profil sans barre permanente.");
            AssertHidden();
            AssertOffscreen(window);
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                Directory.CreateDirectory(captureDirectory);
                await File.WriteAllTextAsync(Path.Combine(captureDirectory, "armory-profile-title-bar.json"), JsonSerializer.Serialize(new
                {
                    window.ActualWidth, window.ActualHeight,
                    BrowserType = browser.GetType().FullName,
                    Offscreen = true, NoActivate = true, OsInputInjected = false, Baseline = baseline, Checks = checks
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            Console.WriteLine("Armory immersive title bar OK: composition hit tests, full-size banner, stable layout, hover delay/re-entry, three overlay pins and five page round trips; no OS input.");
        }
        finally
        {
            armory.WindowDragRequested -= dragRequested;
            window.ProfileState.IsOpen = false;
            window.FriendsState.IsOpen = false;
            window.ActivityState.IsOpen = false;
        }

        void AssertHidden()
        {
            window.UpdateLayout();
            True(titleBar.Visibility == Visibility.Hidden && !titleBar.IsHitTestVisible && translation.Y < -titleBar.ActualHeight,
                "La barre masquée doit être hors du profil, invisible et sans interception d'entrée.");
            True(hoverZone.IsVisible && Math.Abs(hoverZone.ActualHeight - 24) < .75,
                "La zone native de secours reste limitée aux 24 pixels supérieurs ; le reste du survol vient de la bannière WebView.");
            AssertOffscreen(window);
        }

        void AssertShown()
        {
            window.UpdateLayout();
            True(titleBar.IsVisible && titleBar.IsHitTestVisible && Math.Abs(translation.Y) < .1,
                "La barre révélée doit être entièrement visible et interactive.");
            AssertOffscreen(window);
        }

        void AssertTitleBarHit(string name)
        {
            Button button = Required<Button>(window, name);
            Point center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
            True(button.IsVisible && button.IsEnabled && HitBelongsTo(window.InputHitTest(center), button),
                $"{name} doit recevoir le hit-test WPF au-dessus de la WebView2 de composition.");
        }

        async Task HideAsync()
        {
            RaiseMouseEvent(titleBar, UIElement.MouseLeaveEvent);
            RaiseMouseEvent(hoverZone, UIElement.MouseLeaveEvent);
            await WaitUntilAsync(() => !titleBar.IsVisible, "Quitter la barre doit la masquer après le délai de survol.");
            window.UpdateLayout();
            AssertHidden();
        }

        async Task AssertNormalPageAsync(string page)
        {
            await Task.Delay(450);
            await PumpAsync();
            window.UpdateLayout();
            AssertShown();
            Equal(Visibility.Collapsed, hoverZone.Visibility, "Les pages habituelles ne doivent pas conserver la zone de survol du profil.");
            Border dragZone = Required<Border>(window, "TopChromeDragZone");
            True(dragZone.IsVisible && HitBelongsTo(window.InputHitTest(dragZone.TranslatePoint(new Point(200, 8), window)), dragZone),
                "La marge vide au-dessus de la navigation doit recevoir le déplacement de fenêtre.");
            AssertTitleBarHit("GameNavigationButton");
            True(!armory.IsVisible, "L'armurerie ne doit pas couvrir les pages habituelles.");
            await RecordAsync("normal-page-" + page);
        }

        async Task RecordAsync(string scenario)
        {
            checks.Add(new { Scenario = scenario, TitleBarVisible = titleBar.IsVisible, titleBar.IsHitTestVisible,
                TranslationY = translation.Y, HoverZoneHeight = hoverZone.ActualHeight,
                Page = window.CurrentPage.ToString(), Geometry = armory.IsVisible ? await ReadProfileLayoutAsync(window, armory) : null });
        }

        async Task CaptureTitleBarAsync(string state)
        {
            if (string.IsNullOrWhiteSpace(captureDirectory)) return;
            Directory.CreateDirectory(captureDirectory);
            RenderTargetBitmap bitmap = new((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(Path.Combine(captureDirectory, "armory-profile-title-bar-wpf-" + state + ".png"))) encoder.Save(stream);
            await SaveCaptureAsync(armory, captureDirectory, "armory-profile-title-bar-webview-" + state + ".png");
        }
    }

    private sealed record ProfileLayoutEvidence(Rect Browser, Rect Hero, Rect Banner, Rect Character, double ViewportWidth, double ViewportHeight);

    private static async Task<ProfileLayoutEvidence> ReadProfileLayoutAsync(LauncherShellV2 window, ArmoryViewV2 armory)
    {
        window.UpdateLayout();
        using JsonDocument result = await ScriptAsync(armory, """
            (() => {
              const rect = selector => { const r = document.querySelector(selector).getBoundingClientRect(); return [r.x,r.y,r.width,r.height]; };
              return {hero:rect('#profile-hero'),banner:rect('.banner'),character:rect('#character-view'),width:innerWidth,height:innerHeight};
            })()
            """);
        JsonElement root = result.RootElement;
        Rect ReadRect(string name)
        {
            double[] values = root.GetProperty(name).EnumerateArray().Select(value => value.GetDouble()).ToArray();
            return new Rect(values[0], values[1], values[2], values[3]);
        }
        FrameworkElement browser = armory.Browser!;
        return new ProfileLayoutEvidence(new Rect(browser.TranslatePoint(new Point(), window), browser.RenderSize),
            ReadRect("hero"), ReadRect("banner"), ReadRect("character"), root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble());
    }

    private static bool HitBelongsTo(IInputElement? hit, DependencyObject target)
    {
        for (DependencyObject? current = hit as DependencyObject; current is not null;)
        {
            if (ReferenceEquals(current, target)) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current)
                : current is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private static void RaiseMouseEvent(UIElement target, RoutedEvent routedEvent) =>
        target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routedEvent });

    private static async Task RevealProfileTitleBarAsync(LauncherShellV2 window)
    {
        RaiseMouseEvent(Required<Border>(window, "ProfileTitleBarHoverZone"), UIElement.MouseEnterEvent);
        // BeginAnimation may initially expose its base value before the first render tick.
        // Wait beyond the 190 ms reveal before checking hit targets or saving a rendered frame.
        await Task.Delay(240);
        await PumpAsync();
        await WaitUntilAsync(() => Required<Border>(window, "TitleBar").IsVisible
            && Math.Abs(Required<TranslateTransform>(window, "ProfileTitleBarTransform").Y) < .1,
            "Survoler le haut du profil doit révéler complètement la barre.");
        window.UpdateLayout();
        AssertOffscreen(window);
    }

    private static async Task ValidateAvatarPublishingAndCustomizationAsync(ArmoryFixture fixture, string? captureDirectory)
    {
        // AvatarImageCache decodes on a worker and freezes BitmapFrame. Its decoder
        // metadata still belongs to that worker, so encoding the original frame on
        // the WPF thread must not be required to publish the profile.
        List<(string Name, BitmapSource Image)> images = [];
        byte[] sample = await File.ReadAllBytesAsync(fixture.AvatarFixturePath);
        images.Add(("worker-decoded frozen PNG", await Task.Run(() => AvatarWpfImageDecoder.DecodePng(sample))));
        string cache = LauncherBuildFlavor.GetAvatarCacheRoot();
        if (Directory.Exists(cache))
        {
            foreach (string path in Directory.EnumerateFiles(cache, "*.png").Take(3))
            {
                byte[] bytes = await File.ReadAllBytesAsync(path);
                images.Add(("read-only native avatar cache", await Task.Run(() => AvatarWpfImageDecoder.DecodePng(bytes))));
            }
        }
        TransformedBitmap transformed = await Task.Run(() =>
        {
            TransformedBitmap result = new(AvatarWpfImageDecoder.DecodePng(sample), new RotateTransform(90));
            result.Freeze();
            return result;
        });
        images.Add(("frozen transformed PNG", transformed));

        AccountUiState state = ConnectedAccount("AvatarAccount");
        state.ApplyRuntimeView(state.Current with { AvatarImage = images[0].Image, HasProfileAvatar = true });
        LauncherShellV2 window = CreateShell(state);
        ArmoryViewV2 armory = Required<ArmoryViewV2>(window, "ArmoryView");
        armory.Configure(_ => Task.FromResult<uint?>(42), state, () => fixture.Configuration,
            fixture.WebViewDataDirectory, bannerStore: new ArmoryBannerStore(fixture.BannerStoreDirectory));
        ShowOffscreen(window);
        try
        {
            await PumpAsync();
            await OpenProfileAsync(window);
            await WaitForScriptAsync(armory, "document.querySelectorAll('.character').length === 3",
                "Le roster doit charger avant le rafraîchissement du profil natif avec avatar.");
            state.ApplyRuntimeView(state.Current with { StatusMessage = "Premier rafraîchissement natif" });
            await WaitForScriptAsync(armory,
                "document.getElementById('profile-name').textContent === 'AvatarAccount' && !document.getElementById('profile-avatar').hidden && document.getElementById('profile-avatar').naturalWidth > 0",
                "Le profil doit publier son nom et son avatar décodé en arrière-plan.");
            int revision = 0;
            foreach ((string name, BitmapSource avatar) in images)
            {
                True(avatar.IsFrozen, "L'avatar témoin doit traverser les threads exactement comme dans le cache réel.");
                await ScriptAsync(armory, "document.getElementById('edit-profile').click(); true");
                await WaitForScriptAsync(armory, "!document.getElementById('profile-editor').hidden",
                    "Personnaliser avec un avatar réel doit ouvrir le formulaire sur place.");
                True(armory.IsVisible && !window.AccountPage.IsVisible, "Le navigateur doit rester visible pendant l'édition.");
                string status = "Statut actualisé " + ++revision;
                string bio = "Biographie actualisée " + revision;
                state.ApplyRuntimeView(state.Current with
                {
                    AvatarImage = avatar, HasProfileAvatar = true, StatusMessage = status, Bio = bio,
                    AccountOperation = AccountOperationViewState.UpdatingProfile
                });
                state.ApplyRuntimeView(state.Current with { AccountOperation = AccountOperationViewState.None });
                state.ApplyRuntimeView(state.Current with
                {
                    AccountErrorOperation = AccountOperationViewState.UpdatingProfile,
                    AccountErrorMessage = "Modification temporairement indisponible."
                });
                await OpenProfileAsync(window);
                await WaitForScriptAsync(armory,
                    "document.getElementById('profile-name').textContent === 'AvatarAccount' && document.getElementById('profile-status').textContent === " + JsonSerializer.Serialize(status)
                    + " && document.getElementById('profile-bio').textContent === " + JsonSerializer.Serialize(bio)
                    + " && !document.getElementById('profile-avatar').hidden && document.getElementById('profile-avatar').naturalWidth > 0",
                    "Le retour au profil doit afficher l'avatar et les textes actualisés : " + name);
                await ScriptAsync(armory, "document.getElementById('cancel-profile').click(); true");
            }
            if (fixture.HasModel)
            {
                await WaitForScriptAsync(armory,
                    "document.getElementById('character-view').contentWindow.armory?.ready === true && document.getElementById('character-view').contentWindow.armory.root.children.length > 0 && document.getElementById('character-view').contentWindow.armory.frames > 2",
                    "La 3D doit rester disponible après les allers-retours rapides dans les réglages.");
            }
            await SaveCaptureAsync(armory, captureDirectory, "armory-webview-avatar.png");
            state.ApplyRuntimeView(state.Current with { AvatarImage = null, HasProfileAvatar = false });
            await WaitForScriptAsync(armory,
                "document.getElementById('profile-avatar').hidden && document.getElementById('profile-name').textContent === 'AvatarAccount'",
                "Retirer l'avatar doit conserver l'identité publiée.");
            Console.WriteLine($"Armory avatar publishing OK: {images.Count} frozen images, worker decoder metadata, repeated customization, hidden browser refresh and failed profile updates.");
        }
        finally
        {
            window.Close();
            await PumpAsync();
        }
    }

    private static async Task<CancellationToken> ValidateProfileBridgeAsync(LauncherShellV2 window, ArmoryViewV2 armory, AccountUiState state)
    {
        int saves = 0, removals = 0, selections = 0;
        bool accept = false;
        string? submittedStatus = null, submittedBio = null;
        CancellationToken selectionSession = default;
        EventHandler<ArmoryProfileSaveRequestedEventArgs> save = (_, args) =>
        {
            saves++; submittedStatus = args.StatusMessage; submittedBio = args.Bio; args.Accepted = accept;
        };
        EventHandler<ArmoryAvatarRequestedEventArgs> remove = (_, args) => { removals++; args.Accepted = true; };
        EventHandler<ArmoryAvatarRequestedEventArgs> select = (_, args) => { selections++; selectionSession = args.SessionToken; args.Accepted = true; };
        armory.ProfileSaveRequested += save;
        armory.AvatarRemoveRequested += remove;
        armory.AvatarChangeRequested += select;
        AccountViewState original = state.Current;
        var browser = armory.Browser;
        try
        {
            await ScriptAsync(armory, "window.__profileResults=[]; window.__profileUpdates=[]; window.chrome.webview.addEventListener('message',e=>{if(e.data?.type==='profile-save-result')window.__profileResults.push(e.data);if(e.data?.type==='profile')window.__profileUpdates.push(e.data);});true");
            await PostSaveAsync("denied", "denied");
            Equal(0, saves, "Une capacité désactivée doit bloquer même un message forgé.");
            await AssertAcceptedAsync(false);
            state.ApplyRuntimeView(state.Current with { CanUpdateSocialProfile = true, CanModifyAvatar = true, CanRemoveAvatar = true });
            foreach (object invalid in new object[]
            {
                new { action = "save-profile", statusMessage = new string('x', 81), bio = "" },
                new { action = "save-profile", statusMessage = "", bio = new string('x', 281) },
                new { action = "save-profile", statusMessage = 7, bio = "" }
            })
            {
                await PostAsync(invalid); await AssertAcceptedAsync(false);
            }
            Equal(0, saves, "Les longueurs et types invalides ne doivent pas atteindre les commandes.");
            state.ApplyRuntimeView(state.Current with { AccountOperation = AccountOperationViewState.UpdatingProfile });
            await PostSaveAsync("busy", "busy");
            Equal(0, saves, "Une deuxième sauvegarde doit être refusée pendant l'opération.");
            state.ApplyRuntimeView(state.Current with { AccountOperation = AccountOperationViewState.None });
            await PostSaveAsync("refusé", "aucune persistance");
            Equal(1, saves, "Le message valide doit atteindre le gestionnaire natif.");
            await AssertAcceptedAsync(false);
            accept = true;
            string status = new('s', 80), bio = new('b', 280);
            await PostSaveAsync(status, bio);
            await AssertAcceptedAsync(true);
            Equal(status, submittedStatus, "Les 80 caractères du statut doivent parvenir intacts à la commande.");
            Equal(bio, submittedBio, "Les 280 caractères de biographie doivent parvenir intacts à la commande.");
            Equal(original.StatusMessage, state.Current.StatusMessage, "Démarrer ne signifie pas enregistrer : pas de mutation optimiste.");
            state.ApplyRuntimeView(state.Current with { AccountOperation = AccountOperationViewState.UpdatingProfile });
            await WaitForScriptAsync(armory, "window.__profileUpdates.at(-1)?.profileBusy===true", "L'opération native doit publier l'état occupé.");
            state.ApplyRuntimeView(state.Current with { AccountOperation = AccountOperationViewState.None,
                AccountErrorOperation = AccountOperationViewState.UpdatingProfile, AccountErrorMessage = "Échec témoin" });
            await WaitForScriptAsync(armory, "window.__profileUpdates.at(-1)?.profileError==='Échec témoin' && !window.__profileUpdates.at(-1)?.profileNotice", "Une erreur native ne doit jamais annoncer un succès.");
            state.ApplyRuntimeView(state.Current with { AccountErrorOperation = AccountOperationViewState.None,
                AccountErrorMessage = "", AccountNotice = AccountNoticeViewState.ProfileUpdated,
                AccountNoticeMessage = "Profil enregistré", StatusMessage = "Persisté", Bio = "Confirmé" });
            await WaitForScriptAsync(armory, "window.__profileUpdates.at(-1)?.profileNotice==='Profil enregistré' && window.__profileUpdates.at(-1)?.statusMessage==='Persisté'", "Seul l'état confirmé doit publier les textes enregistrés.");
            await PostAsync(new { action = "remove-avatar" });
            await PostAsync(new { action = "remove-avatar", confirmed = "true" });
            Equal(0, removals, "La suppression nécessite une confirmation booléenne explicite.");
            await PostAsync(new { action = "remove-avatar", confirmed = true });
            Equal(1, removals, "La suppression confirmée doit atteindre la commande existante.");
            state.ApplyRuntimeView(state.Current with { CanRemoveAvatar = false, CanModifyAvatar = false });
            await PostAsync(new { action = "remove-avatar", confirmed = true });
            await PostAsync(new { action = "change-avatar" });
            Equal(1, removals, "La capacité de suppression doit être revalidée côté natif.");
            Equal(0, selections, "La capacité avatar désactivée doit bloquer le sélecteur.");
            state.ApplyRuntimeView(state.Current with { CanModifyAvatar = true });
            await PostAsync(new { action = "change-avatar" });
            Equal(1, selections, "Le sélecteur autorisé doit recevoir le token de la session active.");
            await WaitForScriptAsync(armory, "window.__profileUpdates.at(-1)?.avatarBusy===true", "La sélection en cours doit désactiver les actions avatar.");
            await PostAsync(new { action = "change-avatar" });
            Equal(1, selections, "Un double clic ne doit pas ouvrir deux sélecteurs.");
            armory.CompleteAvatarSelection(selectionSession);
            await WaitForScriptAsync(armory, "window.__profileUpdates.at(-1)?.avatarBusy===false", "Annuler le sélecteur doit publier un état réactivé même sans changement de compte.");
            Equal(LauncherShellPage.Armory, window.CurrentPage, "Les actions du profil doivent conserver l'armurerie.");
            True(ReferenceEquals(browser, armory.Browser), "Les actions ne doivent pas recréer la WebView.");
        }
        finally
        {
            armory.ProfileSaveRequested -= save;
            armory.AvatarRemoveRequested -= remove;
            armory.AvatarChangeRequested -= select;
            state.ApplyRuntimeView(original);
        }
        return selectionSession;

        async Task PostAsync(object request)
        {
            await ScriptAsync(armory, "window.__profileResults=[];window.__profileUpdates=[];window.chrome.webview.postMessage(" + JsonSerializer.Serialize(request) + ");true");
            await WaitForScriptAsync(armory, "window.__profileUpdates.length>0", "Chaque action doit publier son état natif avant l'assertion.");
        }
        Task PostSaveAsync(string statusMessage, string bio) => PostAsync(new { action = "save-profile", statusMessage, bio });
        async Task AssertAcceptedAsync(bool expected)
        {
            await WaitForScriptAsync(armory, "window.__profileResults.length>0", "La sauvegarde doit recevoir un accusé de démarrage.");
            using JsonDocument result = await ScriptAsync(armory, "window.__profileResults.at(-1).accepted");
            Equal(expected, result.RootElement.GetBoolean(), "L'accusé doit refléter le démarrage réel.");
        }
    }

    private static async Task ValidateBannerBridgeAsync(ArmoryFixture fixture)
    {
        AccountUiState state = ConnectedAccount("BannerAccount");
        ArmoryBannerStore store = new(fixture.BannerStoreDirectory);
        BannerTestPicker picker = new() { Pick = () => null };
        uint accountId = 42;
        ArmoryViewV2 armory = new();
        armory.Configure(_ => Task.FromResult<uint?>(accountId), state, () => fixture.Configuration,
            fixture.WebViewDataDirectory, store, picker);
        Window window = new() { Content = armory, Width = 1080, Height = 680, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false };
        ShowOffscreen(window);
        try
        {
            await ObserveAsync("BannerAccount");
            await PostAsync(new { action = "choose-banner" });
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.some(e=>e.type==='banner-selection-cancelled') && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerBusy===false",
                "Annuler le sélecteur doit réactiver la bannière sans rien enregistrer.");
            True(await store.LoadAsync(42, CancellationToken.None) is null, "Une sélection annulée ne doit pas créer de personnalisation.");

            picker.Pick = () => fixture.AvatarFixturePath;
            await ChooseAsync();
            True(await store.LoadAsync(42, CancellationToken.None) is null, "Choisir une image doit créer uniquement un brouillon natif.");
            await PostAsync(new { action = "cancel-banner" });
            await WaitForScriptAsync(armory, "window.__bannerEvents.some(e=>e.type==='profile')", "Annuler le brouillon doit republier la bannière persistée.");
            await SaveAsync(new { action = "save-banner", positionX = 0.25, positionY = 0.75 }, accepted: true, succeeded: true);
            ArmoryBannerData defaultPosition = (await store.LoadAsync(42, CancellationToken.None))!;
            True(defaultPosition.PngBytes is null && defaultPosition.PositionX == 0.25 && defaultPosition.PositionY == 0.75,
                "Après annulation du brouillon, seule la position du fond par défaut doit être sauvegardée.");
            Equal(1.0, defaultPosition.Zoom, "Une requête ancienne sans zoom doit conserver le cadrage minimal.");
            Equal("contain", defaultPosition.Fit, "Une requête ancienne sans mode doit afficher l'image entière.");
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.banner===null && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.hasBannerCustomization===true",
                "Le point focal du fond par défaut doit être annoncé comme une personnalisation réinitialisable.");
            await SaveAsync(new { action = "reset-banner", confirmed = true }, accepted: true, succeeded: true);

            await ChooseAsync();
            await SaveAsync(new { action = "save-banner", positionX = 0.25, positionY = 0.75, zoom = 1.75, fit = "cover" }, accepted: true, succeeded: true);
            ArmoryBannerData saved = await store.LoadAsync(42, CancellationToken.None)
                ?? throw new InvalidOperationException("La bannière validée doit être persistée sur cet appareil.");
            Equal(0.25, saved.PositionX, "La position horizontale doit être conservée.");
            Equal(0.75, saved.PositionY, "La position verticale doit être conservée.");
            Equal(1.75, saved.Zoom, "Le multiplicateur de zoom doit être enregistré avec l'image et sa position.");
            Equal("cover", saved.Fit, "Le choix Remplir doit être conservé avec le cadrage.");
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.banner?.startsWith('data:image/png;base64,') && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerPositionX===0.25 && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerZoom===1.75 && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerFit==='cover'",
                "Le profil doit annoncer uniquement l'image et le cadrage réellement enregistrés.");
            using (JsonDocument start = await ScriptAsync(armory, "window.__bannerEvents.some(e=>e.type==='banner-save-result' && e.accepted && !e.succeeded && e.completed===false)"))
                True(start.RootElement.GetBoolean(), "Le démarrage de sauvegarde doit être distinct de son succès final.");

            // The transport probes above post directly, so they do not create the JavaScript
            // save request that normally closes its editor after the native acknowledgement.
            await ScriptAsync(armory, "document.getElementById('cancel-banner').click();true");
            await WaitForScriptAsync(armory,
                "!document.getElementById('banner-editor').open && document.getElementById('profile-hero').dataset.fit==='cover'",
                "Le test visuel doit commencer depuis la bannière réellement persistée.");
            await ScriptAsync(armory, "document.getElementById('edit-banner').click();document.getElementById('reposition-banner').click();true");
            await WaitForScriptAsync(armory,
                "document.getElementById('banner-editor').open && !document.getElementById('banner-fit-cover') && !document.getElementById('banner-fit-contain') && document.getElementById('banner-zoom').value==='1.75'",
                "Le recadrage doit reprendre le zoom enregistré sans proposer Image entière.");
            await ScriptAsync(armory, "document.getElementById('banner-crop-stage').dispatchEvent(new WheelEvent('wheel',{deltaY:-120,bubbles:true,cancelable:true}));true");
            await WaitForScriptAsync(armory,
                "Number(document.getElementById('banner-zoom').value)>1.75 && document.getElementById('profile-hero').dataset.fit==='cover'",
                "La molette doit agrandir l'image et synchroniser le curseur en remplissage.");
            await ScriptAsync(armory, "document.getElementById('banner-crop-stage').dispatchEvent(new WheelEvent('wheel',{deltaY:120,bubbles:true,cancelable:true}));true");
            await WaitForScriptAsync(armory,
                "Math.abs(Number(document.getElementById('banner-zoom').value)-1.75)<0.02",
                "La molette opposée doit permettre de dézoomer.");
            Equal(1.75, (await store.LoadAsync(42, CancellationToken.None))!.Zoom,
                "Le zoom en brouillon ne doit pas réécrire la bannière persistée.");
            await ScriptAsync(armory, "document.getElementById('cancel-banner').click();true");
            await WaitForScriptAsync(armory,
                "!document.getElementById('banner-editor').open && document.getElementById('profile-hero').dataset.fit==='cover'",
                "Annuler le recadrage doit restaurer la bannière enregistrée.");
            ArmoryBannerData cancelledMode = (await store.LoadAsync(42, CancellationToken.None))!;
            True(cancelledMode.Fit == "cover" && cancelledMode.PositionX == 0.25 && cancelledMode.PositionY == 0.75 && cancelledMode.Zoom == 1.75,
                "Annuler le recadrage doit préserver les coordonnées et le zoom persistants.");

            await ChooseAsync();
            await PostAsync(new { action = "cancel-banner" });
            await WaitForScriptAsync(armory, "window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerZoom===1.75",
                "Annuler une nouvelle image doit conserver le zoom déjà enregistré.");
            Equal(1.75, (await store.LoadAsync(42, CancellationToken.None))!.Zoom,
                "L'annulation du brouillon ne doit pas réécrire le zoom persistant.");
            Equal("cover", (await store.LoadAsync(42, CancellationToken.None))!.Fit,
                "Annuler une nouvelle image doit préserver le mode enregistré.");

            foreach (object invalid in new object[]
            {
                new { action = "save-banner", positionX = -0.01, positionY = 0.5 },
                new { action = "save-banner", positionX = 0.5, positionY = 1.01 },
                new { action = "save-banner", positionX = "0.5", positionY = 0.5 },
                new { action = "save-banner", positionX = 0.5 },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, image = "https://example.test/banner.png" },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, zoom = 0.99 },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, zoom = 3.01 },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, zoom = "NaN" },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, zoom = true },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, fit = "stretch" },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, fit = "CONTAIN" },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, fit = true },
                new { action = "save-banner", positionX = 0.5, positionY = 0.5, fit = (string?)null },
                JsonSerializer.Deserialize<JsonElement>("{\"action\":\"save-banner\",\"positionX\":0.5,\"positionY\":0.5,\"zoom\":1e400}"),
                new { action = "reset-banner" },
                new { action = "reset-banner", confirmed = "true" }
            }) await SaveAsync(invalid, accepted: false, succeeded: false);
            Equal(0.25, (await store.LoadAsync(42, CancellationToken.None))!.PositionX,
                "Les messages mal formés et les suppressions non confirmées doivent conserver la bannière enregistrée.");
            Equal(1.75, (await store.LoadAsync(42, CancellationToken.None))!.Zoom,
                "Un zoom invalide ne doit pas modifier le fichier enregistré.");
            Equal("cover", (await store.LoadAsync(42, CancellationToken.None))!.Fit,
                "Un mode invalide ne doit pas modifier le fichier enregistré.");

            int previousPicks = picker.Calls;
            await SaveAsync(new { action = "save-banner", positionX = 0.8, positionY = 0.1, zoom = 2.5, fit = "contain" }, accepted: true, succeeded: true);
            Equal(previousPicks, picker.Calls, "Repositionner l'image enregistrée ne doit pas rouvrir le sélecteur.");
            ArmoryBannerData fullImage = (await store.LoadAsync(42, CancellationToken.None))!;
            True(fullImage.Fit == "contain" && fullImage.PositionX == 0.8 && fullImage.PositionY == 0.1 && fullImage.Zoom == 2.5,
                "Image entière doit être persisté sans effacer le cadrage du mode Remplir.");
            using (FileStream locked = new(Path.Combine(fixture.BannerStoreDirectory, "42.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                await SaveAsync(new { action = "save-banner", positionX = 0.2, positionY = 0.2, zoom = 1.25, fit = "cover" }, accepted: true, succeeded: false);
            Equal(0.8, (await store.LoadAsync(42, CancellationToken.None))!.PositionX,
                "Une erreur disque ne doit ni remplacer le fichier ni être présentée comme un succès.");
            Equal(2.5, (await store.LoadAsync(42, CancellationToken.None))!.Zoom,
                "Un échec disque doit également conserver le zoom précédent.");
            Equal("contain", (await store.LoadAsync(42, CancellationToken.None))!.Fit,
                "Un échec disque doit également conserver le mode précédent.");

            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            await WaitUntilAsync(() => armory.Browser is null, "La déconnexion doit fermer la session de bannière.");
            accountId = 84;
            state.ApplyRuntimeView(ConnectedAccount("BannerOther").Current);
            await ObserveAsync("BannerOther");
            await PostAsync(new { action = "ready" });
            await WaitForScriptAsync(armory, "window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.banner===null && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerZoom===1",
                "Le compte suivant ne doit jamais recevoir la bannière du compte précédent.");
            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            await WaitUntilAsync(() => armory.Browser is null, "Le second compte doit fermer sa session.");
            accountId = 42;
            state.ApplyRuntimeView(ConnectedAccount("BannerAccount").Current);
            await ObserveAsync("BannerAccount");
            await PostAsync(new { action = "ready" });
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.banner?.startsWith('data:image/png;base64,') && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerPositionX===0.8 && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerZoom===2.5 && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerFit==='contain'",
                "Une nouvelle session doit relire l'image et le cadrage persistés.");
            await SaveAsync(new { action = "reset-banner", confirmed = true }, accepted: true, succeeded: true);
            True(await store.LoadAsync(42, CancellationToken.None) is null, "Le reset confirmé doit supprimer le fichier local du compte.");
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerZoom===1 && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerPositionX===0.5 && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerPositionY===0.3",
                "Réinitialiser doit revenir au zoom minimal et au point focal initial du fond par défaut.");

            picker.Pick = () =>
            {
                state.ApplyRuntimeView(AccountUiState.Empty.Current);
                return fixture.AvatarFixturePath;
            };
            try { await PostAsync(new { action = "choose-banner" }); }
            catch (Exception error) when (armory.Browser is null &&
                (error is InvalidOperationException or OperationCanceledException or System.Runtime.InteropServices.COMException)) { }
            await WaitUntilAsync(() => armory.Browser is null, "Une session fermée pendant le sélecteur doit invalider son résultat.");
            True(await store.LoadAsync(42, CancellationToken.None) is null, "Une image sélectionnée après déconnexion ne doit jamais être persistée.");
            accountId = 84;
            state.ApplyRuntimeView(ConnectedAccount("BannerOther").Current);
            await ObserveAsync("BannerOther");
            await SaveAsync(new { action = "save-banner", positionX = 0.4, positionY = 0.6 }, accepted: true, succeeded: true);
            True((await store.LoadAsync(84, CancellationToken.None))!.PngBytes is null,
                "Le résultat tardif d'un sélecteur ne doit pas devenir le brouillon du compte suivant.");
            await SaveAsync(new { action = "reset-banner", confirmed = true }, accepted: true, succeeded: true);
            Console.WriteLine("Armory banner bridge OK: native picker, draft/cancel, validated focal points, final acknowledgements, disk failure, account isolation and restart.");
        }
        finally
        {
            armory.Dispose();
            window.Close();
            await PumpAsync();
        }

        async Task ObserveAsync(string username)
        {
            await WaitForScriptAsync(armory, "document.getElementById('profile-name')?.textContent===" + JsonSerializer.Serialize(username),
                "La session native de bannière doit être publiée avant de tester son transport.");
            await ScriptAsync(armory, "window.__bannerEvents=[];window.chrome.webview.addEventListener('message',e=>window.__bannerEvents.push(e.data));true");
        }
        async Task PostAsync(object request)
        {
            using JsonDocument result = await ScriptAsync(armory,
                "window.__bannerEvents=[];window.chrome.webview.postMessage(" + JsonSerializer.Serialize(request) + ");true");
        }
        async Task ChooseAsync()
        {
            await PostAsync(new { action = "choose-banner" });
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.some(e=>e.type==='banner-selected' && e.image.startsWith('data:image/png;base64,') && e.zoom===1 && e.fit==='contain') && window.__bannerEvents.filter(e=>e.type==='profile').at(-1)?.bannerBusy===false",
                "Le sélecteur doit publier le PNG normalisé sans le sauvegarder.");
        }
        async Task SaveAsync(object request, bool accepted, bool succeeded)
        {
            await PostAsync(request);
            await WaitForScriptAsync(armory,
                "window.__bannerEvents.some(e=>e.type==='banner-save-result' && e.completed===true)",
                "La sauvegarde doit publier son résultat final explicite.");
            using JsonDocument result = await ScriptAsync(armory, "window.__bannerEvents.filter(e=>e.type==='banner-save-result' && e.completed===true).at(-1)");
            Equal(accepted, result.RootElement.GetProperty("accepted").GetBoolean(), "L'accusé doit indiquer si l'opération a démarré.");
            Equal(succeeded, result.RootElement.GetProperty("succeeded").GetBoolean(), "Le succès doit correspondre à la persistance effective.");
            if (!succeeded) True(result.RootElement.GetProperty("error").ValueKind == JsonValueKind.String,
                "Un refus ou échec doit publier une erreur compréhensible.");
        }
    }

    private sealed class BannerTestPicker : IAvatarFilePicker
    {
        internal Func<string?> Pick { get; set; } = () => null;
        internal int Calls { get; private set; }
        public string? PickImagePath() { Calls++; return Pick(); }
    }

    private static async Task ValidateCancelledAccountLookupAsync(ArmoryFixture fixture)
    {
        AccountUiState state = ConnectedAccount("LateAccount");
        TaskCompletionSource<uint?> lateAccount = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource lookupStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool useLateAccount = true;
        CancellationToken firstLookupCancellation = default;
        ArmoryViewV2 armory = new();
        armory.Configure(token =>
        {
            if (!useLateAccount) return Task.FromResult<uint?>(84);
            firstLookupCancellation = token;
            lookupStarted.TrySetResult();
            return lateAccount.Task;
        }, state, () => fixture.Configuration, fixture.WebViewDataDirectory,
            bannerStore: new ArmoryBannerStore(fixture.BannerStoreDirectory));
        Window window = new() { Content = armory, Width = 1080, Height = 680, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false };
        Uri? origin = null;
        ShowOffscreen(window);
        try
        {
            await lookupStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            True(firstLookupCancellation.IsCancellationRequested, "Déconnexion pendant la résolution du compte doit annuler ce démarrage.");
            useLateAccount = false;
            state.ApplyRuntimeView(ConnectedAccount("FreshAccount").Current);
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent === 'FreshAccount' && document.querySelector('.character strong')?.textContent === 'Mage84'", "La reconnexion rapide doit démarrer sans attendre l'ancienne requête de compte.");
            origin = new Uri(armory.Browser!.CoreWebView2.Source);
            lateAccount.TrySetResult(42);
            await Task.Delay(100);
            await PumpAsync();
            await WaitForScriptAsync(armory, "document.querySelector('.character strong')?.textContent === 'Mage84'", "Une résolution de compte tardive ne doit pas écraser la nouvelle session.");
            Equal(origin.AbsoluteUri, armory.Browser!.CoreWebView2.Source, "La session tardive ne doit pas recréer un serveur.");
        }
        finally
        {
            lateAccount.TrySetResult(42);
            armory.Dispose();
            armory.Dispose();
            window.Close();
            await PumpAsync();
        }
        if (origin is not null) await AssertStoppedAsync(origin);
        Console.WriteLine("Armory lifecycle race OK: cancelled lookup cannot replace the rapidly reconnected account.");
    }

    private static async Task ValidateUnavailableArmoryRecoveryAsync(ArmoryFixture fixture)
    {
        LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale);
        AccountUiState state = ConnectedAccount("RetryAccount");
        LauncherShellV2 window = CreateShell(state);
        ArmoryViewV2 armory = Required<ArmoryViewV2>(window, "ArmoryView");
        bool configured = false;
        armory.Configure(_ => Task.FromResult<uint?>(42), state,
            () => configured ? fixture.Configuration : throw new FileNotFoundException("Fixture configuration absent."),
            fixture.WebViewDataDirectory, bannerStore: new ArmoryBannerStore(fixture.BannerStoreDirectory));
        ShowOffscreen(window);
        try
        {
            await PumpAsync();
            await OpenProfileAsync(window);
            await WaitUntilAsync(() => Required<Button>(armory, "RetryButton").IsVisible,
                "L'absence de configuration locale doit proposer une nouvelle tentative.");
            True(Required<TextBlock>(armory, "StatusText").Text.Contains("indisponible", StringComparison.OrdinalIgnoreCase),
                "L'armurerie indisponible doit être expliquée en français.");
            True(Required<Button>(armory, "CustomizeButton").IsVisible,
                "Les réglages Profil doivent rester accessibles si l'armurerie échoue.");
            Required<Button>(armory, "CustomizeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(LauncherShellPage.Account, window.CurrentPage, "Personnaliser doit fonctionner aussi sans WebView.");
            True(window.AccountPage.ProfileFallbackEnabled && window.AccountPage.SelectedSection == AccountSection.Profile,
                "Le repli doit afficher la personnalisation du profil, pas seulement la sécurité du compte.");
            await WaitUntilAsync(() => Required<FrameworkElement>(window.AccountPage, "ProfilePanel").IsVisible,
                "Le formulaire de profil doit réellement être visible en cas de panne de l'armurerie.");
            await OpenProfileAsync(window);
            configured = true;
            Required<Button>(armory, "RetryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent === 'RetryAccount'",
                "Réessayer doit récupérer une configuration devenue disponible.");
        }
        finally
        {
            window.Close();
            await PumpAsync();
        }
        Console.WriteLine("Armory unavailable/retry OK: profile settings remain accessible and startup recovers.");
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        // Profile menus and embedded views request focus as part of their normal lifecycle.
        // Exercise their routed events without moving focus away from the user's desktop.
        window.PreviewGotKeyboardFocus += (_, args) => args.Handled = true;
        window.SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x08000000); // GWL_EXSTYLE / WS_EX_NOACTIVATE
        };
        window.Show();
        AssertOffscreen(window);
    }

    private static void AssertOffscreen(Window window)
    {
        True(window.Left <= -10000 && window.Top <= -10000 && !window.IsActive && !window.ShowActivated && !window.ShowInTaskbar,
            "Le test doit rester hors écran, inactif et absent de la barre des tâches.");
        True((GetWindowLong(new WindowInteropHelper(window).Handle, -20) & 0x08000000) != 0,
            "La fenêtre de test doit conserver le style natif WS_EX_NOACTIVATE.");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    private static LauncherShellV2 CreateShell(AccountUiState account) => new(
        LauncherV2PreviewData.CreateShell(GamePreviewScenario.Ready, isAuthenticated: true),
        LauncherV2PreviewData.CreateGame(GamePreviewScenario.Ready),
        LauncherV2PreviewData.CreateDashboard(GamePreviewScenario.Ready),
        LauncherV2PreviewData.CreateFriends(),
        LauncherV2PreviewData.CreateProfile(ProfilePreviewScenario.SignedIn),
        // Bind the real navigation buttons to an initially connected fake state, as in the runtime shell.
        new SettingsUiState(SettingsUiState.Empty.Current with { IsRuntimeConnected = true }),
        account,
        new AvatarCropUiState(AvatarCropUiState.Empty.Current))
    {
        Left = -20000, Top = -20000,
        WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false, ShowActivated = false
    };

    private static AccountUiState ConnectedAccount(string username) => new(AccountUiState.Empty.Current with
    {
        IsRuntimeConnected = true, Username = username, Initial = username[..1],
        StatusMessage = "Test local", Bio = "Profil de vérification sans authentification distante."
    });

    private static async Task OpenProfileAsync(LauncherShellV2 window)
    {
        True(Required<ArmoryViewV2>(window, "ArmoryView").IsConfigured,
            "Le parcours Profil doit exercer une armurerie réellement raccordée au shell.");
        if (window.CurrentPage == LauncherShellPage.Armory) await RevealProfileTitleBarAsync(window);
        Button avatar = Required<Button>(window, "ProfileButton");
        True(avatar.IsVisible && avatar.IsEnabled, "L'avatar du compte connecté doit être accessible depuis la barre du launcher.");
        avatar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(ShellOverlayKind.Profile, window.CurrentOverlay, "L'avatar doit ouvrir le véritable menu Profil avant la navigation.");
        Button manageProfile = Required<Button>(window.ProfileOverlay, "ManageProfileButton");
        True(manageProfile.IsEnabled, "Gérer mon profil doit être autorisé pour le compte connecté.");
        manageProfile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(LauncherShellPage.Armory, window.CurrentPage, "Gérer mon profil doit ouvrir l'armurerie par son événement natif, sans navigation forcée.");
        Equal(ShellOverlayKind.None, window.CurrentOverlay, "Le menu Profil doit se fermer après l'ouverture de l'armurerie.");
        True(!window.AccountPage.IsVisible, "Le formulaire Compte ne doit pas remplacer le profil avec personnage et équipement.");
    }

    private static T Required<T>(FrameworkElement element, string name) where T : class =>
        element.FindName(name) as T ?? throw new InvalidOperationException($"Contrôle WPF absent : {name}.");

    private static async Task<JsonDocument> ScriptAsync(ArmoryViewV2 armory, string script) =>
        JsonDocument.Parse(await armory.Browser!.CoreWebView2.ExecuteScriptAsync(script));

    private static async Task WaitForScriptAsync(ArmoryViewV2 armory, string script, string message, int timeoutSeconds = 30)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (armory.Browser?.CoreWebView2 is not null)
            {
                using JsonDocument result = await ScriptAsync(armory, "(() => { try { return Boolean(" + script + "); } catch { return false; } })()");
                if (result.RootElement.ValueKind == JsonValueKind.True) return;
            }
            await Task.Delay(75);
            await PumpAsync();
        }
        string status = Required<TextBlock>(armory, "StatusText").Text;
        throw new TimeoutException(message + " État WPF : " + status);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string message)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
            await Task.Delay(35);
            await PumpAsync();
        }
    }

    private static async Task ValidateEmbeddedInterFontsAsync(ArmoryViewV2 armory, string? directory)
    {
        // FontFaceSet.check alone can report success for a system fallback. Require
        // actual loaded @font-face entries for every packaged weight in both documents.
        using (JsonDocument started = await ScriptAsync(armory, """
            (() => {
              window.__atlasInterFontEvidence = {status:'loading'};
              const family = value => value.replaceAll('"','').replaceAll("'",'').trim();
              const probe = async (doc, name) => {
                const weights = [400,500,600,800];
                const sample = 'Atlas Équipements Français été à 123';
                const loaded = await Promise.all(weights.map(weight => doc.fonts.load(`${weight} 16px Inter`, sample)));
                await doc.fonts.ready;
                const elements = [doc.body,...doc.querySelectorAll('h1,h2,h3,p,button,input,select,textarea,.character strong,.slot')]
                  .filter(element => element.getClientRects().length > 0);
                return {
                  name, url:doc.location.pathname,
                  weights:weights.map((weight,index) => ({weight,checked:doc.fonts.check(`${weight} 16px Inter`,sample),
                    faces:loaded[index].map(face => ({family:family(face.family),weight:face.weight,status:face.status}))})),
                  elements:elements.map(element => ({tag:element.tagName,id:element.id,
                    family:family(doc.defaultView.getComputedStyle(element).fontFamily.split(',')[0])}))
                };
              };
              const inner = document.getElementById('character-view').contentDocument;
              Promise.all([probe(document,'profile'),probe(inner,'character')])
                .then(documents => {window.__atlasInterFontEvidence={status:'loaded',documents};})
                .catch(error => {window.__atlasInterFontEvidence={status:'error',error:String(error)};});
              return true;
            })()
            """))
            True(started.RootElement.ValueKind == JsonValueKind.True, "Le contrôle des fontes WebView doit démarrer.");
        await WaitForScriptAsync(armory, "window.__atlasInterFontEvidence?.status !== 'loading'",
            "Les quatre fontes Inter doivent terminer leur chargement dans le profil et le personnage.");
        using JsonDocument evidence = await ScriptAsync(armory, "window.__atlasInterFontEvidence");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "armory-inter-fonts.json"), evidence.RootElement.GetRawText());
        }
        Equal("loaded", evidence.RootElement.GetProperty("status").GetString(), "Le chargement Inter ne doit pas échouer.");
        foreach (JsonElement document in evidence.RootElement.GetProperty("documents").EnumerateArray())
        {
            string name = document.GetProperty("name").GetString()!;
            foreach (JsonElement weight in document.GetProperty("weights").EnumerateArray())
            {
                string expected = weight.GetProperty("weight").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture);
                True(weight.GetProperty("checked").GetBoolean(), $"{name}: Inter {expected} doit fournir les glyphes demandés.");
                JsonElement[] faces = weight.GetProperty("faces").EnumerateArray().ToArray();
                True(faces.Length > 0 && faces.All(face => face.GetProperty("family").GetString() == "Inter"
                    && face.GetProperty("weight").GetString() == expected && face.GetProperty("status").GetString() == "loaded"),
                    $"{name}: Inter {expected} doit provenir d'une véritable face chargée, sans fallback système.");
            }
            JsonElement[] elements = document.GetProperty("elements").EnumerateArray().ToArray();
            True(elements.Length > 1 && elements.All(element => element.GetProperty("family").GetString() == "Inter"),
                $"{name}: les textes et contrôles visibles doivent avoir Inter comme famille CSS effective.");
            Console.WriteLine($"Armory Inter OK: {name}, 4 loaded physical faces, {elements.Length} visible text/control elements.");
        }
    }

    private static async Task SaveCaptureAsync(ArmoryViewV2 armory, string? directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using FileStream file = File.Create(Path.Combine(directory, name));
        await armory.Browser!.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }

    private static async Task AssertStoppedAsync(Uri origin)
    {
        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(3) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            try { using HttpResponseMessage response = await http.GetAsync(new Uri(origin, "/health.json")); }
            catch (HttpRequestException) { return; }
            catch (TaskCanceledException) { }
            await Task.Delay(75);
        }
        throw new InvalidOperationException("Le serveur de l'armurerie répond encore après fermeture.");
    }

    private static async Task PumpAsync() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message} Attendu : {expected} ; obtenu : {actual}.");
    }

    private sealed class ArmoryFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AtlasArmoryIntegration", Guid.NewGuid().ToString("N"));
        internal LauncherArmoryLocalConfiguration Configuration { get; }
        internal string WebViewDataDirectory => Path.Combine(_root, "webview-data");
        internal string BannerStoreDirectory => Path.Combine(_root, "banner-store");
        internal string PublicRoot => Path.Combine(_root, "public");
        internal bool HasModel { get; }
        internal string AvatarFixturePath { get; }

        internal ArmoryFixture()
        {
            string repo = FindRepository();
            AvatarFixturePath = Path.Combine(repo, "source", "WotLK.Launcher", "Assets", "Images", "AtlasProfilePreview.png");
            string node = Environment.GetEnvironmentVariable("ATLAS_ARMORY_TEST_NODE")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "node", "bin", "node.exe");
            if (!File.Exists(node)) throw new FileNotFoundException("Node.js requis : définir ATLAS_ARMORY_TEST_NODE.", node);
            Directory.CreateDirectory(_root);
            string server = Path.Combine(_root, "launcher-server.cjs");
            string artifacts = Path.Combine(repo, "artifacts", "armory-prototype");
            string? assets = null;
            string manifest = Path.Combine(artifacts, "armory-current.json");
            if (File.Exists(manifest))
            {
                using JsonDocument current = JsonDocument.Parse(File.ReadAllText(manifest));
                string candidate = Path.Combine(artifacts, "snapshots", current.RootElement.GetProperty("revision").GetString()!, "assets");
                if (File.Exists(Path.Combine(candidate, "character.json"))) assets = candidate;
            }
            HasModel = assets is not null;
            File.WriteAllText(server, "const repo=" + JsonSerializer.Serialize(repo) + "; const assets=" + JsonSerializer.Serialize(assets) + ";\n" + """
                const fs = require('node:fs');
                const path = require('node:path');
                const {createLauncherServer} = require(path.join(repo,'prototypes/armory-3d/launcher-server.cjs'));
                const account = Number(process.env.ATLAS_ARMORY_ACCOUNT_ID);
                const revision = '11111111111111111111111111111111';
                const baseline = assets ? JSON.parse(fs.readFileSync(path.join(assets,'character.json'),'utf8')) :
                  {name:'Fixture',classId:8,raceId:1,level:80,realm:'Arthas',capturedAt:'2026-09-05T00:00:00Z',equipment:[],attached:[]};
                const details = assets ? JSON.parse(fs.readFileSync(path.join(assets,'item-details.json'),'utf8')) :
                  {characterCapturedAt:baseline.capturedAt,items:[]};
                const entries = new Map([
                  ['11',{revision,modelReady:!!assets,assetDir:assets,character:{...baseline,characterId:'11',name:'Mage'+account},details}],
                  ['12',{revision,modelReady:false,assetDir:null,character:{...baseline,characterId:'12',name:'Alt'+account},details}]
                ]);
                const armory = {
                  list:()=>({status:'ready',characters:[
                    {id:'11',name:'Mage'+account,classId:8,race:1,level:80,online:true,available:true},
                    {id:'12',name:'Alt'+account,classId:8,race:1,level:80,online:false,available:true},
                    {id:'13',name:'Pending'+account,classId:1,race:1,level:1,online:false,available:false}
                  ]}),
                  entry:id=>entries.get(id)
                };
                const server = createLauncherServer({key:process.env.ATLAS_ARMORY_BRIDGE_KEY,armory});
                let stopping=false;
                const stop=()=>{
                  if(stopping)return; stopping=true;
                  server.closeAllConnections(); server.close(); process.stdin.destroy();
                };
                process.stdin.resume();
                process.stdin.on('end',stop);
                process.stdin.on('data',chunk=>{if(chunk.toString().includes('shutdown'))stop();});
                server.listen(0,'127.0.0.1',()=>console.log('ATLAS_ARMORY_READY '+JSON.stringify({port:server.address().port})));
                """);
            Configuration = new LauncherArmoryLocalConfiguration(node, server);
        }

        private static string FindRepository()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "prototypes", "armory-3d", "launcher-server.cjs"))) return directory.FullName;
                }
            }
            throw new DirectoryNotFoundException("Le prototype armory-3d est requis pour le test intégré.");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
