using System.IO;
using System.Collections.Immutable;
using System.Windows;
using WotLK.Launcher.Dashboard;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Commands;
using System.Windows.Media.Imaging;

namespace WotLK.Launcher.UI.V2.Preview;

public static class LauncherV2PreviewData
{
    private const string PreviewStatePrefix = "--preview-state=";
    private const string PreviewAvatarUri =
        "/WotLK.Launcher;component/Assets/Images/AtlasProfilePreview.png";
    private const string ChangedPreviewAvatarUri =
        "/WotLK.Launcher;component/Assets/AppIcon.png";

    public static GamePreviewScenario ResolveScenario(IEnumerable<string> arguments)
    {
        string? argument = arguments.FirstOrDefault(value =>
            value.StartsWith(PreviewStatePrefix, StringComparison.OrdinalIgnoreCase));

        if (argument is null)
        {
            return GamePreviewScenario.Ready;
        }

        string normalizedScenario = argument[PreviewStatePrefix.Length..]
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalizedScenario switch
        {
            "notinstalled" => GamePreviewScenario.NotInstalled,
            "updateavailable" => GamePreviewScenario.UpdateAvailable,
            "downloading" => GamePreviewScenario.Downloading,
            "installing" => GamePreviewScenario.Installing,
            "verifying" => GamePreviewScenario.Verifying,
            "error" => GamePreviewScenario.Error,
            "launching" => GamePreviewScenario.Launching,
            "realmoffline" => GamePreviewScenario.RealmOffline,
            _ => GamePreviewScenario.Ready
        };
    }

    public static ShellUiState CreateShell(
        GamePreviewScenario scenario,
        bool isAuthenticated = true) => new()
    {
        Username = isAuthenticated ? "Dono1402" : "Compte",
        IsAuthenticated = isAuthenticated,
        IsNavigationEnabled = scenario is not GamePreviewScenario.Downloading
            and not GamePreviewScenario.Installing
    };

    internal static AuthUiState CreateAuth(AuthPreviewScenario? scenario = null)
    {
        AuthUiState state = new();
        if (scenario is AuthPreviewScenario previewScenario)
        {
            state.ApplyPreviewScenario(previewScenario);
        }

        return state;
    }

    internal static ProfileUiState CreateProfile(
        ProfilePreviewScenario scenario = ProfilePreviewScenario.SignedIn)
    {
        return CreateProfile(scenario, avatarImage: null);
    }

    internal static ProfileUiState CreateProfile(
        ProfilePreviewScenario scenario,
        BitmapSource? avatarImage)
    {
        bool verified = scenario != ProfilePreviewScenario.EmailUnverified;
        bool loggingOut = scenario == ProfilePreviewScenario.LoggingOut;
        ProfileUiState state = new();
        state.ApplyView(new ProfileViewState(
            IsAuthenticated: true,
            IsLoggingOut: loggingOut,
            Username: "Dono1402",
            Initial: "D",
            IsEmailVerified: verified,
            EmailStatusText: verified
                ? "Adresse e-mail vérifiée"
                : "Adresse e-mail non vérifiée",
            CanLogout: !loggingOut,
            LogoutLabel: loggingOut ? "Déconnexion…" : "Déconnexion",
            LogoutToolTip: loggingOut ? "Déconnexion en cours." : string.Empty,
            ErrorMessage: scenario == ProfilePreviewScenario.LogoutError
                ? "Atlas est indisponible. Ta session reste active."
                : string.Empty));
        state.AttachLogoutCommand(loggingOut
            ? DisabledCommand.Instance
            : PreviewCommand.Instance);
        state.ApplyAvatarImage(avatarImage);
        return state;
    }

    internal static SettingsUiState CreateSettings(
        SettingsPreviewScenario scenario = SettingsPreviewScenario.General)
    {
        SettingsCategory category = scenario switch
        {
            SettingsPreviewScenario.Game => SettingsCategory.Game,
            SettingsPreviewScenario.Updates => SettingsCategory.Updates,
            SettingsPreviewScenario.Notifications => SettingsCategory.Notifications,
            SettingsPreviewScenario.Diagnostic => SettingsCategory.Diagnostic,
            _ => SettingsCategory.General
        };
        SettingsSavePreviewState saveState = scenario switch
        {
            SettingsPreviewScenario.Dirty => SettingsSavePreviewState.Dirty,
            SettingsPreviewScenario.Saving => SettingsSavePreviewState.Saving,
            SettingsPreviewScenario.Saved => SettingsSavePreviewState.Saved,
            SettingsPreviewScenario.SaveError => SettingsSavePreviewState.Error,
            _ => SettingsSavePreviewState.None
        };

        SettingsUiState state = new(new SettingsViewState(
            category,
            saveState,
            new GeneralSettingsViewState(
                InterfaceLanguage: "Français",
                InterfaceLocale: "fr-FR",
                StartWithWindows: false,
                MinimizeToTrayOnClose: true),
            new GameSettingsViewState(
                InstallPath: @"C:\Program Files (x86)\WotLK",
                GameLanguage: "Français",
                InstantQuestText: true,
                ClientVersion: "3.4.3.54261"),
            new UpdateSettingsViewState(
                InstalledLauncherVersion: "v1.1.0",
                AvailableLauncherVersion: "v1.1.0"),
            new NotificationSettingsViewState(FriendPresence: true),
            new DiagnosticSettingsViewState(
                LogLocation: @"%LOCALAPPDATA%\Atlas Launcher\Logs",
                LauncherLocation: @"C:\Program Files\Atlas Launcher",
                LauncherVersion: "v1.1.0",
                ClientVersion: "3.4.3.54261",
                LocalState: "Client prêt · non vérifié",
                ServiceState: "Services disponibles")));
        state.AttachPreviewActions();
        return state;
    }

    internal static AccountUiState CreateAccount(
        AccountPreviewScenario scenario = AccountPreviewScenario.Profile)
    {
        return CreateAccount(scenario, ResolvePreviewAvatar(scenario));
    }

    internal static AccountUiState CreateAccount(
        AccountPreviewScenario scenario,
        BitmapSource? avatarImage)
    {
        AccountSection section = scenario switch
        {
            AccountPreviewScenario.Security
                or AccountPreviewScenario.PasswordChange
                or AccountPreviewScenario.PasswordError
                or AccountPreviewScenario.EmailUnverified
                or AccountPreviewScenario.EmailChange => AccountSection.Security,
            AccountPreviewScenario.Sessions
                or AccountPreviewScenario.SessionRevoke
                or AccountPreviewScenario.SessionRevokeError => AccountSection.Sessions,
            _ => AccountSection.Profile
        };
        bool removing = scenario == AccountPreviewScenario.Removing;
        bool hasAvatar = avatarImage is not null;
        bool emailVerified = scenario is not (
            AccountPreviewScenario.EmailUnverified
            or AccountPreviewScenario.EmailChange);
        AccountOperationViewState accountOperation = scenario switch
        {
            AccountPreviewScenario.PasswordChange => AccountOperationViewState.ChangingPassword,
            AccountPreviewScenario.EmailChange => AccountOperationViewState.ChangingEmail,
            AccountPreviewScenario.SessionRevoke => AccountOperationViewState.RevokingSession,
            _ => AccountOperationViewState.None
        };
        bool accountBusy = accountOperation != AccountOperationViewState.None;
        string accountError = scenario switch
        {
            AccountPreviewScenario.PasswordError => "Le mot de passe actuel est incorrect.",
            AccountPreviewScenario.SessionRevokeError => "Cette session n’est plus active.",
            _ => string.Empty
        };
        AccountOperationViewState errorOperation = scenario switch
        {
            AccountPreviewScenario.PasswordError => AccountOperationViewState.ChangingPassword,
            AccountPreviewScenario.SessionRevokeError => AccountOperationViewState.RevokingSession,
            _ => AccountOperationViewState.None
        };

        return new AccountUiState(new AccountViewState(
            IsPreview: true,
            IsRuntimeConnected: false,
            SelectedSection: section,
            Username: "Dono1402",
            Email: "dono1402@outlook.com",
            Initial: "D",
            IsEmailVerified: emailVerified,
            HasProfileAvatar: hasAvatar,
            AvatarImage: avatarImage,
            AvatarOperation: removing
                ? AvatarPreviewOperation.Removing
                : AvatarPreviewOperation.None,
            AvatarStatusMessage: removing
                ? "Suppression de la photo en cours…"
                : string.Empty,
            AvatarErrorMessage: string.Empty,
            IsAvatarBackendAvailable: true,
            IsAvatarBackendChecking: false,
            AvatarAvailabilityMessage: string.Empty,
            CanModifyAvatar: !removing,
            CanRemoveAvatar: hasAvatar && !removing,
            IsDeleteConfirmationOpen: false,
            MemberSince: "Membre Atlas depuis juillet 2026",
            LastPasswordChange: "Modifié il y a 18 jours",
            ActiveSessionCount: 2,
            AccountOperation: accountOperation,
            AccountErrorOperation: errorOperation,
            AccountErrorMessage: accountError,
            AccountNoticeMessage: string.Empty,
            AccountNotice: AccountNoticeViewState.None,
            CanChangeEmail: !accountBusy,
            CanResendVerification: !emailVerified && !accountBusy,
            CanChangePassword: !accountBusy,
            IsEmailEditorOpen: scenario == AccountPreviewScenario.EmailChange,
            IsPasswordEditorOpen: scenario is AccountPreviewScenario.PasswordChange
                or AccountPreviewScenario.PasswordError,
            SessionsState: AccountSessionsViewState.Loaded,
            Sessions: [],
            SessionsMessage: string.Empty));
    }

    internal static AccountAvatarPreviewComposition CreateAccountAvatarComposition(
        GamePreviewScenario gameScenario,
        AccountPreviewScenario accountScenario)
    {
        BitmapSource? avatar = ResolvePreviewAvatar(accountScenario);
        ShellUiState shell = CreateShell(gameScenario, isAuthenticated: true);
        shell.ApplyProfileAvatar(avatar);
        return new AccountAvatarPreviewComposition(
            shell,
            CreateProfile(ProfilePreviewScenario.SignedIn, avatar),
            CreateAccount(accountScenario, avatar),
            CreateAvatarCrop(accountScenario));
    }

    internal static AvatarCropUiState CreateAvatarCrop(
        AccountPreviewScenario scenario = AccountPreviewScenario.Profile)
    {
        bool isOpen = scenario is AccountPreviewScenario.Crop
            or AccountPreviewScenario.Uploading
            or AccountPreviewScenario.UploadError;
        AvatarCropPreviewStatus status = scenario switch
        {
            AccountPreviewScenario.Uploading => AvatarCropPreviewStatus.Uploading,
            AccountPreviewScenario.UploadError => AvatarCropPreviewStatus.Error,
            _ => AvatarCropPreviewStatus.Idle
        };

        return new AvatarCropUiState(new AvatarCropViewState(
            IsPreview: true,
            IsOpen: isOpen,
            Status: status,
            AvatarImage: CreatePreviewAvatar(),
            ErrorMessage: status == AvatarCropPreviewStatus.Error
                ? "La photo n’a pas pu être préparée. Réessaie avec une autre image."
                : string.Empty,
            StatusMessage: status == AvatarCropPreviewStatus.Uploading
                ? "Envoi… 48 %"
                : string.Empty,
            UploadPercentage: status == AvatarCropPreviewStatus.Uploading ? 48 : null,
            IsProgressIndeterminate: false,
            Zoom: 1.18,
            OffsetX: 0,
            OffsetY: -8,
            OrientedPixelWidth: 1024,
            OrientedPixelHeight: 1024,
            MaximumZoom: 2.4));
    }

    private static BitmapSource? ResolvePreviewAvatar(AccountPreviewScenario scenario)
    {
        return scenario switch
        {
            AccountPreviewScenario.Fallback or AccountPreviewScenario.AvatarDeleted => null,
            AccountPreviewScenario.AvatarChanged => CreatePreviewAvatar(ChangedPreviewAvatarUri),
            _ => CreatePreviewAvatar(PreviewAvatarUri)
        };
    }

    private static BitmapImage CreatePreviewAvatar(string resourceUri = PreviewAvatarUri)
    {
        System.Windows.Resources.StreamResourceInfo resource = Application.GetResourceStream(
            new Uri(resourceUri, UriKind.Relative))
            ?? throw new InvalidOperationException("La ressource avatar de prévisualisation est absente.");
        using Stream stream = resource.Stream;
        BitmapImage image = new();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    internal static DashboardUiState CreateDashboard(GamePreviewScenario scenario)
    {
        DashboardRealmState realmState = scenario == GamePreviewScenario.RealmOffline
            ? DashboardRealmState.Offline
            : DashboardRealmState.Online;
        string realmLabel = realmState == DashboardRealmState.Offline ? "Hors ligne" : "En ligne";
        DashboardUiState state = new();
        state.ApplyView(new DashboardViewState(
            realmState,
            realmLabel,
            realmState == DashboardRealmState.Online
                ? "Serveur de jeu en ligne"
                : "Serveur de jeu hors ligne",
            "Données fictives du mode de prévisualisation.",
            IsLoading: false,
            LatestPatchNoteVersion: "v1.1.0",
            LatestPatchNoteTitle: "Atlas Launcher 1.1",
            LatestPatchNoteSummary:
                "Une nouvelle expérience de lancement, plus claire et plus directe, pensée pour Arthas.",
            LatestPatchNoteMetaText: "30 août 2026",
            HasPatchNote: true,
            IsStale: false,
            CanOpenLatestPatchNote: true,
            PatchNotes: CreatePatchNotesPreview()));
        state.AttachRefreshCommand(PreviewCommand.Instance);
        return state;
    }

    private static ImmutableArray<PatchNoteEntryViewState> CreatePatchNotesPreview() =>
    [
        LocalPatchNotesDraft.Create(),
        new(
            Id: "atlas-launcher-1-2-0",
            Version: "v1.2.0",
            Title: "Atlas Launcher 1.2",
            PublishedText: "4 septembre 2026",
            Intro: string.Empty,
            HasIntro: false,
            IsLatest: false,
            IsDraft: false,
            Sections:
            [
                new("Launcher",
                [
                    "La nouvelle expérience Atlas devient l’interface par défaut.",
                    "Le centre d’activité permet de suivre les opérations en cours."
                ]),
                new("Addons",
                [
                    "Le catalogue permet d’installer, mettre à jour et supprimer les addons compatibles."
                ]),
                new("Compte et amis",
                [
                    "Le profil, les sessions et la liste d’amis sont maintenant synchronisés avec Atlas."
                ])
            ]),
        new(
            Id: "atlas-launcher-1-1-0",
            Version: "v1.1.0",
            Title: "Atlas Launcher 1.1",
            PublishedText: "29 août 2026",
            Intro: string.Empty,
            HasIntro: false,
            IsLatest: false,
            IsDraft: false,
            Sections:
            [
                new("Launcher",
                [
                    "L’écran Jeu a été réorganisé pour accéder plus rapidement aux actions principales.",
                    "Les téléchargements disposent d’un suivi plus détaillé."
                ])
            ])
    ];

    public static GameUiState CreateGame(GamePreviewScenario scenario)
    {
        GameUiState state = scenario switch
        {
            GamePreviewScenario.NotInstalled => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Warning,
                ClientStatus = "Client non installé",
                PrimaryActionLabel = "Installer",
                InstallBadgeText = "Non installé",
                ClientVersion = string.Empty,
                InstallPath = "Aucun dossier sélectionné",
                IsClientReady = false,
                Progress = 0
            },
            GamePreviewScenario.UpdateAvailable => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Warning,
                ClientStatus = "Mise à jour disponible",
                PrimaryActionLabel = "Mettre à jour",
                InstallBadgeText = "Mise à jour",
                AvailableClientVersion = "3.4.3.54289",
                IsClientReady = false,
                Progress = 0
            },
            GamePreviewScenario.Downloading => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "Téléchargement en cours",
                PrimaryActionLabel = "Annuler",
                IsOptionsEnabled = false,
                InstallBadgeText = "42 %",
                IsClientReady = false,
                Progress = 42,
                ProgressTitle = "Téléchargement du client",
                ProgressPercentText = "42 %",
                ProgressPrimaryDetail = "2,10 Go / 5,00 Go",
                ProgressSecondaryDetail = "18,4 Mo/s · 2 min restantes"
            },
            GamePreviewScenario.Installing => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "Installation en cours",
                PrimaryActionLabel = "Annuler",
                IsOptionsEnabled = false,
                InstallBadgeText = "78 %",
                IsClientReady = false,
                Progress = 78,
                ProgressTitle = "Application de la mise à jour",
                ProgressPercentText = "78 %",
                ProgressPrimaryDetail = "2 541 / 3 284 fichiers",
                ProgressSecondaryDetail = "Écriture des fichiers du client"
            },
            GamePreviewScenario.Verifying => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "Analyse en arrière-plan",
                PrimaryActionLabel = "Lancer le jeu",
                InstallBadgeText = "En arrière-plan",
                IsClientReady = false,
                Progress = 0,
                IsProgressIndeterminate = true,
                ProgressTitle = "Vérification des fichiers",
                ProgressPrimaryDetail = "Analyse des fichiers locaux en cours",
                ProgressSecondaryDetail = "Tu peux jouer pendant l’analyse"
            },
            GamePreviewScenario.Error => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Error,
                ClientStatus = "Une erreur est survenue",
                PrimaryActionLabel = "Réessayer",
                InstallBadgeText = "Erreur",
                IsClientReady = false,
                Progress = 0,
                ErrorTitle = "Mise à jour interrompue",
                ErrorSummary = "Le téléchargement n’a pas pu être terminé. Tu peux réessayer ou ouvrir le diagnostic."
            },
            GamePreviewScenario.Launching => new GameUiState
            {
                Scenario = GamePreviewScenario.Ready,
                SemanticTone = GameSemanticTone.Accent,
                ClientStatus = "En cours de lancement",
                PrimaryActionLabel = "En cours de lancement",
                IsPrimaryActionEnabled = false,
                IsOptionsEnabled = false,
                InstallBadgeText = "À jour",
                IsClientReady = true,
                Progress = 0,
                IsLaunchInProgress = true,
                PrimaryActionUnavailableReason = "En cours de lancement"
            },
            GamePreviewScenario.RealmOffline => new GameUiState
            {
                Scenario = scenario,
                SemanticTone = GameSemanticTone.Success
            },
            _ => new GameUiState
            {
                Scenario = GamePreviewScenario.Ready,
                SemanticTone = GameSemanticTone.Success
            }
        };
        state.AttachPrimaryActionCommand(PreviewCommand.Instance);
        return state;
    }

    public static FriendsUiState CreateFriends(
        FriendsPreviewScenario scenario = FriendsPreviewScenario.Populated)
    {
        BitmapSource avatar = CreatePreviewAvatar();
        BitmapSource changedAvatar = CreatePreviewAvatar(ChangedPreviewAvatarUri);
        ImmutableArray<FriendUiItem> populated =
        [
            Friend(2, "warthoon", "Ophntfranck", "Mage · niveau 12", true, "En jeu", "#DEB75A", true, avatar),
            Friend(3, "lyssara", "Lyssara", "Prêtre · niveau 32", true, "En jeu", "#61D5E8", true, avatar),
            Friend(4, "kaelorn", "Kaelorn", "Paladin · niveau 28", false, "Hors ligne · vu hier à 22:14", "#51D7A2", true, avatar),
            Friend(5, "nerya-au-nom-particulièrement-long", "Nerya", "Druide · niveau 18", false, "Hors ligne", "#DEB75A", false),
            Friend(6, "thalion", "Thalion", "Guerrier · niveau 44", false, "Hors ligne · vu le 29/08 à 18:02", "#EE6571", true, avatar),
            Friend(7, "elyndra", "Elyndra", "Chasseur · niveau 26", false, "Hors ligne", "#61D5E8", true)
        ];
        ImmutableArray<FriendUiItem> incoming =
        [
            Request(12, "aelwen", "#61D5E8", accept: true, avatar),
            Request(13, "franck", "#DEB75A", accept: true, avatar: null)
        ];
        ImmutableArray<FriendUiItem> outgoing =
        [
            Request(18, "valdyr", "#51D7A2", accept: false, avatar)
        ];
        ImmutableArray<FriendUiItem> avatarOnly = populated
            .Select(item => item.HasAvatarImage
                ? item
                : item with
                {
                    AvatarId = PreviewAvatarId(item.AccountId),
                    AvatarVersion = 1,
                    AvatarImage = avatar,
                    HasAvatarImage = true
                })
            .ToImmutableArray();
        ImmutableArray<FriendUiItem> manyFriends = Enumerable.Range(1, 100)
            .Select(index => Friend(
                (uint)(1000 + index),
                $"aventurier{index:000}",
                $"Personnage{index:000}",
                $"{(index % 2 == 0 ? "Mage" : "Paladin")} · niveau {(index % 80) + 1}",
                online: index <= 14,
                presence: index <= 14 ? "En jeu" : "Hors ligne",
                color: index % 2 == 0 ? "#61D5E8" : "#DEB75A",
                themed: index % 3 != 0,
                avatar: index % 3 == 0 ? null : avatar))
            .ToImmutableArray();
        FriendsViewState view = scenario switch
        {
            FriendsPreviewScenario.Empty => PreviewFriendsView(
                ImmutableArray<FriendUiItem>.Empty,
                ImmutableArray<FriendUiItem>.Empty,
                ImmutableArray<FriendUiItem>.Empty,
                string.Empty),
            FriendsPreviewScenario.IncomingRequests => PreviewFriendsView(
                populated[..2], incoming, ImmutableArray<FriendUiItem>.Empty, "2 amis Atlas"),
            FriendsPreviewScenario.OutgoingRequests => PreviewFriendsView(
                populated[..2], ImmutableArray<FriendUiItem>.Empty, outgoing, "2 amis Atlas"),
            FriendsPreviewScenario.AddFriend => PreviewFriendsView(
                populated[..3], ImmutableArray<FriendUiItem>.Empty, outgoing, "3 amis Atlas",
                notice: "Demande d’ami envoyée."),
            FriendsPreviewScenario.AddFriendError => PreviewFriendsView(
                populated[..3], ImmutableArray<FriendUiItem>.Empty, ImmutableArray<FriendUiItem>.Empty,
                "3 amis Atlas", error: "Aucun compte Atlas ne porte ce nom."),
            FriendsPreviewScenario.AvatarFallback => PreviewFriendsView(
                [Friend(23, "sansavatar", "", "", false, "Hors ligne", "#DEB75A", false)],
                ImmutableArray<FriendUiItem>.Empty,
                ImmutableArray<FriendUiItem>.Empty,
                "1 ami Atlas"),
            FriendsPreviewScenario.NetworkError => PreviewFriendsView(
                ImmutableArray<FriendUiItem>.Empty,
                ImmutableArray<FriendUiItem>.Empty,
                ImmutableArray<FriendUiItem>.Empty,
                string.Empty,
                error: "Impossible de joindre Atlas pour le moment.",
                loadState: FriendsViewLoadState.Failed),
            FriendsPreviewScenario.Avatars => PreviewFriendsView(
                avatarOnly,
                incoming.Select(item => item with
                {
                    AvatarId = PreviewAvatarId(item.AccountId),
                    AvatarVersion = 1,
                    AvatarImage = avatar,
                    HasAvatarImage = true
                }).ToImmutableArray(),
                outgoing,
                "6 amis Atlas"),
            FriendsPreviewScenario.MixedAvatars => PreviewFriendsView(
                populated,
                incoming,
                outgoing,
                "6 amis Atlas"),
            FriendsPreviewScenario.AvatarChanged => PreviewFriendsView(
                [Friend(2, "warthoon", "Ophntfranck", "Mage · niveau 12", true, "En jeu", "#DEB75A", true, changedAvatar, avatarVersion: 2)],
                ImmutableArray<FriendUiItem>.Empty,
                ImmutableArray<FriendUiItem>.Empty,
                "1 ami Atlas",
                notice: "Photo de profil actualisée."),
            FriendsPreviewScenario.NetworkStale => PreviewFriendsView(
                populated[..3],
                incoming[..1],
                ImmutableArray<FriendUiItem>.Empty,
                "3 amis Atlas · actualisation indisponible",
                isStale: true),
            FriendsPreviewScenario.ManyFriends => PreviewFriendsView(
                manyFriends,
                incoming,
                outgoing,
                "100 amis Atlas"),
            _ => PreviewFriendsView(populated, incoming, outgoing, "6 amis Atlas")
        };
        FriendsUiState state = new(view)
        {
            SearchText = scenario is FriendsPreviewScenario.AddFriend
                or FriendsPreviewScenario.AddFriendError
                    ? "franck"
                    : string.Empty
        };
        state.AttachPreviewCommands();
        return state;
    }

    private static FriendsViewState PreviewFriendsView(
        ImmutableArray<FriendUiItem> friends,
        ImmutableArray<FriendUiItem> incoming,
        ImmutableArray<FriendUiItem> outgoing,
        string status,
        string error = "",
        string notice = "",
        FriendsViewLoadState loadState = FriendsViewLoadState.Loaded,
        bool isStale = false)
    {
        return new FriendsViewState(
            IsPreview: true,
            IsRuntimeConnected: true,
            LoadState: loadState,
            Friends: friends,
            IncomingRequests: incoming,
            OutgoingRequests: outgoing,
            Operation: FriendsViewOperation.None,
            StatusMessage: status,
            ErrorMessage: error,
            NoticeMessage: notice,
            CanRefresh: true,
            CanSendRequest: true,
            IsStale: isStale);
    }

    private static FriendUiItem Friend(
        uint accountId,
        string username,
        string character,
        string details,
        bool online,
        string presence,
        string color,
        bool themed,
        BitmapSource? avatar = null,
        ulong avatarVersion = 1)
    {
        return new FriendUiItem(
            accountId,
            username,
            username[..1].ToUpperInvariant(),
            color,
            themed,
            avatar is null ? null : PreviewAvatarId(accountId),
            avatar is null ? null : avatarVersion,
            avatar,
            avatar is not null,
            online,
            presence,
            character,
            details,
            character.Length > 0,
            IsBusy: false,
            CanAccept: false,
            CanReject: false,
            CanCancel: false,
            CanRemove: true);
    }

    private static FriendUiItem Request(
        uint accountId,
        string username,
        string color,
        bool accept,
        BitmapSource? avatar)
    {
        return new FriendUiItem(
            accountId,
            username,
            username[..1].ToUpperInvariant(),
            color,
            HasAvatarTheme: true,
            AvatarId: avatar is null ? null : PreviewAvatarId(accountId),
            AvatarVersion: avatar is null ? null : 1,
            AvatarImage: avatar,
            HasAvatarImage: avatar is not null,
            IsOnline: false,
            PresenceText: accept ? "Souhaite devenir votre ami" : "En attente",
            CharacterName: string.Empty,
            CharacterDetails: string.Empty,
            HasCharacter: false,
            IsBusy: false,
            CanAccept: accept,
            CanReject: accept,
            CanCancel: !accept,
            CanRemove: false);
    }

    private static Guid PreviewAvatarId(uint accountId) =>
        Guid.Parse($"00000000-0000-0000-0000-{accountId:000000000000}");
}

internal sealed record AccountAvatarPreviewComposition(
    ShellUiState Shell,
    ProfileUiState Profile,
    AccountUiState Account,
    AvatarCropUiState Crop);
