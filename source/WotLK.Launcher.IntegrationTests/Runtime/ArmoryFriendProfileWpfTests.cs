using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2;
using WotLK.Launcher.UI.V2.Localization;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ArmoryLauncherTests
{
    private static async Task ValidateFriendProfileAsync(ArmoryFixture fixture, string? captures)
    {
        await ValidateFriendCacheLifecycleAsync(fixture);
        AccountUiState state = ConnectedAccount("ViewerAtlas");
        state.ApplyAvatarImage(AvatarWpfImageDecoder.DecodePng(CreateProfileAvatarPng(256, 42)), descriptorPresent: true);
        ProfileAvatarMediaClient avatarMedia = new();
        using AvatarImageCache avatarCache = new(avatarMedia,
            Path.Combine(fixture.BannerStoreDirectory, "avatar-cache"), CancellationToken.None);
        LauncherShellV2 window = CreateShell(state);
        ArmoryViewV2 armory = Required<ArmoryViewV2>(window, "ArmoryView");
        FriendRuntimeItem runtimeFriend = new(91, "AmiAtlas", null, ProfileAvatarDescriptor(1),
            FriendRelationship.Accepted, false, null, null, null, null, null,
            "Disponible pour un donjon", "Une bio publique de test.", IsLauncherOnline: true);
        FriendsViewState friends = FriendsStateAdapter.Project(FriendsRuntimeSnapshot.SignedOut with
        {
            CurrentUserId = 42, IsAuthenticated = true, LoadState = FriendsLoadState.Loaded, Friends = [runtimeFriend]
        });
        FriendUiItem friend = friends.Friends.Single() with
        {
            AvatarImage = AvatarWpfImageDecoder.DecodePng(CreateProfileAvatarPng(64, 64)), HasAvatarImage = true
        };
        window.FriendsState.ApplyRuntimeView(friends with { Friends = [friend] });
        ConcurrentQueue<(uint Viewer, uint Target, string Operation)> calls = new();
        uint viewer = 42;
        Task<JsonElement> Read(uint owner, uint target, LauncherArmoryDataRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Enqueue((owner, target, request.Operation));
            return Task.FromResult(request.Operation == "catalog"
                ? JsonSerializer.SerializeToElement(new { capturedAtUtc = "2026-09-06 19:00:00", items = Array.Empty<object>() })
                : JsonSerializer.SerializeToElement(new { observedAtUtc = "2026-09-06 19:00:00", characters = new[]
                {
                    new { character = new { guid = target * 10, name = "Mage" + target, race = 1, classId = 8, gender = 0,
                        level = 80, skin = 0, face = 0, hairStyle = 0, hairColor = 0, facialStyle = 0,
                        online = 0, zoneId = 0, lastLogout = 0 }, equipment = Array.Empty<object>(), snapshot = (object?)null, values = (object?)null }
                } }));
        }
        armory.Configure(_ => Task.FromResult<uint?>(viewer), state, () => fixture.AuthenticatedConfiguration,
            fixture.WebViewDataDirectory, bannerStore: new ArmoryBannerStore(fixture.BannerStoreDirectory),
            readData: (owner, request, token) => Read(owner, owner, request, token), readFriendData: Read,
            avatarImages: avatarCache);
        int mutations = 0, messageRequests = 0;
        armory.ProfileSaveRequested += (_, _) => mutations++;
        armory.AvatarChangeRequested += (_, _) => mutations++;
        armory.AvatarRemoveRequested += (_, _) => mutations++;
        armory.CustomizeRequested += (_, _) => mutations++;
        armory.FriendMessageRequested += (_, e) => { Equal((uint)91, e.AccountId, "Le message doit cibler le compte Atlas du profil."); messageRequests++; };
        ShowOffscreen(window);
        try
        {
            await PumpAsync();
            void OpenFriend(uint id, string username)
            {
                // Invoke the subscribed event handler without creating a native desktop Popup.
                MethodInfo handler = typeof(LauncherShellV2).GetMethod("FriendsDrawer_PublicProfileRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
                handler.Invoke(window, [window.FriendsOverlay, new FriendPublicProfileRequestedEventArgs(id, username)]);
            }
            OpenFriend(91, "Untrusted stale menu label");
            Equal(LauncherShellPage.Armory, window.CurrentPage, "Voir le profil doit ouvrir la véritable page du profil.");
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='AmiAtlas' && document.querySelector('.character strong')?.textContent==='Mage91'",
                "Le profil et le roster doivent provenir du compte ami.");
            True(armory.IsReadOnlyProfile && armory.FriendAccountId == 91, "Le profil ami doit être identifié comme non modifiable.");
            await ValidateFriendProfileAvatarAsync(armory, friend, avatarMedia, avatarCache);
            object? openedBrowser = armory.Browser;
            OpenFriend(91, "AmiAtlas");
            True(ReferenceEquals(openedBrowser, armory.Browser), "Rouvrir le même ami doit conserver le modèle et le helper en cours.");
            friend = friend with { Presence = "dnd", PresenceText = "Ne pas déranger" };
            window.FriendsState.ApplyRuntimeView(window.FriendsState.Current with { Friends = [friend] });
            await WaitForScriptAsync(armory, "document.getElementById('profile-presence').dataset.presence==='dnd' && document.getElementById('profile-presence-label').textContent==='Ne pas déranger'",
                "La présence doit se mettre à jour dans le profil ami ouvert.");
            True(ReferenceEquals(openedBrowser, armory.Browser), "Une mise à jour de présence ne doit pas recréer l'armurerie.");
            await AssertProfileAvatarAsync(armory, 256, 1, "La présence ne doit pas remplacer le portrait par la vignette.");
            True(calls.Any(call => call.Viewer == 42 && call.Target == 91 && call.Operation == "roster")
                && calls.All(call => call.Viewer == 42), "Le helper RPC doit transmettre le compte cible en gardant le compte viewer pour l'authentification.");
            await WaitForScriptAsync(armory, "document.getElementById('edit-profile').hidden && document.querySelector('.banner-controls').hidden && document.getElementById('change-avatar').disabled && !document.getElementById('friend-actions').hidden",
                "Les actions de modification doivent être absentes du profil ami.");
            True(Required<Button>(armory, "CustomizeButton").Visibility == Visibility.Collapsed,
                "Le repli natif ne doit pas proposer de modifier le compte personnel depuis un profil ami.");
            await ScriptAsync(armory, "['customize','save-profile','change-avatar','remove-avatar','choose-banner','save-banner','reset-banner'].forEach(action=>chrome.webview.postMessage({action,statusMessage:'Interdit',bio:'Interdit',confirmed:true})); true");
            await PumpAsync();
            Equal(0, mutations, "Des commandes forgées dans la WebView ami doivent être rejetées par le pont natif.");
            await ScriptAsync(armory, "setTimeout(()=>document.getElementById('send-message').click(),100); true");
            await WaitUntilAsync(() => messageRequests == 1, "Envoyer un message doit viser l'identité Atlas affichée.");
            Equal(LauncherShellPage.Chat, window.CurrentPage, "Le bouton Message du profil doit ouvrir la messagerie.");
            // WebView2 preview capture requires a visible host. Reopen the friend after the message action navigates away.
            OpenFriend(91, "AmiAtlas");
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='AmiAtlas'", "Le profil ami doit pouvoir être rouvert après Messages.");
            await SaveCaptureAsync(armory, captures, "friend-profile-fr.png");
            LauncherLocalization.SetLocale(LauncherLocalization.EnglishLocale);
            await WaitForScriptAsync(armory, "document.querySelector('[data-label=characters]').textContent==='Characters' && document.getElementById('send-message').textContent.includes('Send a message')", "Le profil public doit être traduit en anglais.");
            await SaveCaptureAsync(armory, captures, "friend-profile-en.png");

            FriendUiItem second = friend with { AccountId = 92, Username = "SecondFriend", Bio = "Autre profil" };
            window.FriendsState.ApplyRuntimeView(window.FriendsState.Current with { Friends = [friend, second] });
            OpenFriend(92, "SecondFriend");
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='SecondFriend' && document.querySelector('.character strong')?.textContent==='Mage92'", "Changer d'ami doit renouveler le helper et son roster.");
            window.FriendsState.ApplyRuntimeView(window.FriendsState.Current with { Friends = [friend] });
            await WaitUntilAsync(() => window.CurrentPage == LauncherShellPage.Game && !armory.IsReadOnlyProfile && armory.Browser is null,
                "Retirer l'ami doit fermer son profil et vider la WebView.");

            await OpenProfileAsync(window);
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='ViewerAtlas' && document.querySelector('.character strong')?.textContent==='Mage42' && document.getElementById('friend-actions').hidden", "Mon profil doit revenir au compte connecté après un profil ami.");
            await AssertProfileAvatarAsync(armory, 256, 42, "Mon profil doit conserver ses pixels 256 px et son propre avatar.");
            long presenceSequence = 100;
            foreach (string presence in new[] { "online", "away", "dnd", "offline" })
            {
                window.ProfileState.ApplyPresence(new LauncherPresenceSnapshot(++presenceSequence, 42, presence, presence, false, true, false, null));
                await WaitForScriptAsync(armory, $"document.getElementById('profile-presence').dataset.presence==='{presence}' && document.getElementById('profile-presence-label').textContent.length>0",
                    "Le profil personnel doit afficher immédiatement chaque état de présence.");
            }
            await SaveCaptureAsync(armory, captures, "own-profile-presence.png");
            OpenFriend(91, "AmiAtlas");
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='AmiAtlas'", "Le premier ami doit pouvoir être rouvert.");
            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            await WaitUntilAsync(() => armory.Browser is null && armory.FriendAccountId is null, "La déconnexion doit purger le profil ami.");
            viewer = 84;
            state.ApplyRuntimeView(ConnectedAccount("NewViewer").Current);
            await OpenProfileAsync(window);
            await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='NewViewer' && document.querySelector('.character strong')?.textContent==='Mage84'", "La reconnexion ne doit pas réouvrir un ami de l'ancien compte.");
            AssertOffscreen(window);
        }
        finally { window.Close(); await PumpAsync(); LauncherLocalization.SetLocale(LauncherLocalization.FrenchLocale); }
        Console.WriteLine("Friend profile WPF OK: event navigation, real RPC helper, viewer/target isolation, readonly native bridge, FR/EN, message identity, friend removal, own profile and logout/reconnect.");
    }

    private static async Task ValidateFriendCacheLifecycleAsync(ArmoryFixture fixture)
    {
        string root = fixture.AuthenticatedConfiguration.DataRoot!;
        AccountUiState state = ConnectedAccount("CacheViewer");
        int accountReads = 0;
        void Configure(ArmoryViewV2 view) => view.Configure(_ => { accountReads++; return Task.FromResult<uint?>(42); },
            state, () => fixture.AuthenticatedConfiguration, fixture.WebViewDataDirectory,
            bannerStore: new ArmoryBannerStore(fixture.BannerStoreDirectory),
            readFriendData: (_, _, _, _) => throw new InvalidOperationException("Hidden lifecycle test must never start a helper."));
        string Seed(uint viewer, uint friend)
        {
            using LauncherArmoryFriendCache cache = new();
            string directory = cache.GetDirectory(root, viewer, friend);
            string file = Path.Combine(directory, "lifecycle-model.bin");
            File.WriteAllText(file, "persisted fixture");
            return file;
        }

        string first = Seed(42, 91), removed = Seed(42, 92);
        using (ArmoryViewV2 initial = new())
        {
            Configure(initial);
            await initial.PendingCleanup;
            True(File.Exists(first) && File.Exists(removed), "La configuration initiale doit préserver les modèles du lancement précédent.");
            initial.RetainFriendCaches(new HashSet<uint> { 91 });
            await initial.PendingCleanup;
            True(File.Exists(first) && !File.Exists(removed), "Le roster doit purger un ami retiré même sans rouvrir son profil.");
            initial.Dispose();
            await initial.PendingCleanup;
        }
        True(File.Exists(first), "La fermeture normale de la vue doit conserver son cache sur disque.");
        using (ArmoryViewV2 restarted = new())
        {
            Configure(restarted);
            await restarted.PendingCleanup;
            True(File.ReadAllText(first) == "persisted fixture", "Une nouvelle vue doit retrouver le cache du lancement précédent.");
            state.ApplyRuntimeView(AccountUiState.Empty.Current);
            await restarted.PendingCleanup;
            True(!File.Exists(first), "Une déconnexion explicite doit purger aussi le cache non ouvert depuis le redémarrage.");
            state.ApplyRuntimeView(ConnectedAccount("SecondCacheViewer").Current);
            await restarted.PendingCleanup;
            string previousAccount = Seed(84, 91);
            state.ApplyRuntimeView(ConnectedAccount("ThirdCacheViewer").Current);
            await restarted.PendingCleanup;
            True(!File.Exists(previousAccount), "Un changement explicite de compte doit purger le précédent sans dépendre d'une WebView ouverte.");
            string previousConfiguration = Seed(126, 91);
            Configure(restarted);
            await restarted.PendingCleanup;
            True(!File.Exists(previousConfiguration), "Remplacer une configuration active doit invalider l'ancien périmètre.");
            True(restarted.Browser is null, "Le test de cycle de vie ne doit créer aucune WebView.");
            restarted.Dispose();
            await restarted.PendingCleanup;
        }
        Equal(0, accountReads, "L'entretien du cache au login ne doit ajouter aucun appel d'authentification/API.");
        Console.WriteLine("Friend cache WPF lifecycle OK: initial configure and dispose preserve, new view reuses, unopened removal/logout/account/configuration changes purge, zero helper and zero extra account requests.");
    }
}
