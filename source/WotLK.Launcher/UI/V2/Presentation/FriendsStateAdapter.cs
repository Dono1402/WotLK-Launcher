using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Threading;
using WotLK.Launcher.Account;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Presentation;

internal sealed class FriendsStateAdapter : IDisposable
{
    internal const int SocialAvatarSize = 64;

    private readonly FriendsUiState _target;
    private readonly LauncherFriendsCoordinator _runtime;
    private readonly Dispatcher _dispatcher;
    private readonly AvatarImageCache? _avatarImages;
    private readonly CancellationTokenSource _avatarLoadCancellation = new();
    private readonly ConcurrentDictionary<FriendAvatarIdentity, byte> _avatarLoads = new();
    private readonly ConcurrentDictionary<FriendAvatarIdentity, byte> _avatarAttempts = new();
    private FriendsRuntimeSnapshot? _latestSnapshot;
    private long _latestSequence = -1;
    private int _disposeState;

    internal FriendsStateAdapter(
        FriendsUiState target,
        LauncherFriendsCoordinator runtime,
        Dispatcher dispatcher,
        AvatarImageCache? avatarImages = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _avatarImages = avatarImages;
        _runtime.SnapshotChanged += Runtime_SnapshotChanged;
        ApplyOrQueue(_runtime.CurrentSnapshot);
    }

    internal static FriendsViewState Project(FriendsRuntimeSnapshot snapshot)
        => Project(snapshot, avatarResolver: null);

    private static FriendsViewState Project(
        FriendsRuntimeSnapshot snapshot,
        Func<AvatarDescriptor, ImageSource?>? avatarResolver)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool canAct = snapshot.IsAuthenticated && !snapshot.IsBusy;
        ImmutableArray<FriendUiItem> friends = snapshot.Friends
            .Select(friend => ProjectFriend(friend, snapshot, canAct, avatarResolver))
            .ToImmutableArray();
        ImmutableArray<FriendUiItem> incoming = snapshot.IncomingRequests
            .Select(friend => ProjectFriend(friend, snapshot, canAct, avatarResolver))
            .ToImmutableArray();
        ImmutableArray<FriendUiItem> outgoing = snapshot.OutgoingRequests
            .Select(friend => ProjectFriend(friend, snapshot, canAct, avatarResolver))
            .ToImmutableArray();
        FriendsViewLoadState loadState = snapshot.LoadState switch
        {
            FriendsLoadState.Idle => FriendsViewLoadState.Idle,
            FriendsLoadState.Loading => FriendsViewLoadState.Loading,
            FriendsLoadState.Loaded => FriendsViewLoadState.Loaded,
            FriendsLoadState.Failed => FriendsViewLoadState.Failed,
            _ => FriendsViewLoadState.SignedOut
        };
        FriendsViewOperation operation = snapshot.OperationState switch
        {
            FriendsOperationState.Refreshing => FriendsViewOperation.Refreshing,
            FriendsOperationState.SendingRequest => FriendsViewOperation.SendingRequest,
            FriendsOperationState.AcceptingRequest => FriendsViewOperation.AcceptingRequest,
            FriendsOperationState.RejectingRequest => FriendsViewOperation.RejectingRequest,
            FriendsOperationState.CancellingRequest => FriendsViewOperation.CancellingRequest,
            FriendsOperationState.RemovingFriend => FriendsViewOperation.RemovingFriend,
            _ => FriendsViewOperation.None
        };
        return new FriendsViewState(
            IsPreview: false,
            IsRuntimeConnected: snapshot.IsAuthenticated,
            LoadState: loadState,
            Friends: friends,
            IncomingRequests: incoming,
            OutgoingRequests: outgoing,
            Operation: operation,
            StatusMessage: GetStatusMessage(snapshot),
            ErrorMessage: MapError(snapshot.ErrorState.Category),
            NoticeMessage: MapNotice(snapshot.Notice),
            CanRefresh: canAct,
            CanSendRequest: canAct,
            IsStale: snapshot.IsStale);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) == 0)
        {
            _runtime.SnapshotChanged -= Runtime_SnapshotChanged;
            _avatarLoadCancellation.Cancel();
            _avatarLoadCancellation.Dispose();
            _avatarLoads.Clear();
            _avatarAttempts.Clear();
        }
    }

    private void Runtime_SnapshotChanged(object? sender, FriendsRuntimeSnapshotEventArgs e)
    {
        ApplyOrQueue(e.Snapshot);
    }

    private void ApplyOrQueue(FriendsRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }
        if (_dispatcher.CheckAccess())
        {
            Apply(snapshot);
            return;
        }

        _ = _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() => Apply(snapshot)));
    }

    private void Apply(FriendsRuntimeSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposeState) != 0 || snapshot.Sequence <= _latestSequence)
        {
            return;
        }

        _latestSequence = snapshot.Sequence;
        _latestSnapshot = snapshot;
        _target.ApplyRuntimeView(Project(snapshot, ResolveAvatarImage));
        QueueMissingAvatars(snapshot);
    }

    private static FriendUiItem ProjectFriend(
        FriendRuntimeItem friend,
        FriendsRuntimeSnapshot snapshot,
        bool canAct,
        Func<AvatarDescriptor, ImageSource?>? avatarResolver)
    {
        bool isTarget = snapshot.TargetAccountId == friend.AccountId;
        bool busy = snapshot.IsBusy && isTarget;
        string username = string.IsNullOrWhiteSpace(friend.Username)
            ? "Compte Atlas"
            : friend.Username;
        string characterName = friend.CharacterName?.Trim() ?? string.Empty;
        bool hasCharacter = characterName.Length > 0;
        string characterDetails = hasCharacter
            ? $"{GetClassName(friend.ClassId)} niveau {friend.Level?.ToString() ?? "?"}"
            : string.Empty;
        ImmutableArray<FriendCharacterUiItem> characters = ProjectCharacters(friend, hasCharacter);
        ImageSource? avatarImage = friend.Avatar is null
            ? null
            : avatarResolver?.Invoke(friend.Avatar);
        return new FriendUiItem(
            friend.AccountId,
            username,
            username[..1].ToUpperInvariant(),
            GetAvatarColor(friend.AvatarKey),
            !string.IsNullOrWhiteSpace(friend.AvatarKey),
            friend.Avatar?.AvatarId,
            friend.Avatar?.Version,
            avatarImage,
            avatarImage is not null,
            friend.IsOnline,
            GetPresenceText(friend),
            characterName,
            characterDetails,
            hasCharacter,
            busy,
            canAct && friend.Relationship == FriendRelationship.Incoming,
            canAct && friend.Relationship == FriendRelationship.Incoming,
            canAct && friend.Relationship == FriendRelationship.Outgoing,
            canAct && friend.Relationship == FriendRelationship.Accepted,
            friend.StatusMessage,
            friend.Bio,
            GetZoneName(friend.ZoneId),
            characters);
    }

    private static ImmutableArray<FriendCharacterUiItem> ProjectCharacters(
        FriendRuntimeItem friend,
        bool hasLegacyCharacter)
    {
        IEnumerable<FriendCharacterRuntimeItem> source = !friend.Characters.IsDefaultOrEmpty
            ? friend.Characters
            : hasLegacyCharacter
                ?
                [
                    new FriendCharacterRuntimeItem(
                        friend.CharacterName!,
                        friend.Level ?? 0,
                        friend.ClassId ?? 0,
                        friend.ZoneId ?? 0,
                        friend.IsOnline,
                        friend.LastSeenAt)
                ]
                : [];
        return source.Select(character => new FriendCharacterUiItem(
                character.Name,
                GetClassName(character.ClassId),
                character.Level,
                GetZoneName(character.ZoneId),
                character.IsOnline,
                GetCharacterPresenceText(character)))
            .ToImmutableArray();
    }

    private static string GetStatusMessage(FriendsRuntimeSnapshot snapshot)
    {
        if (!snapshot.IsAuthenticated)
        {
            return "Connecte-toi pour retrouver tes amis Atlas.";
        }
        return snapshot.OperationState switch
        {
            FriendsOperationState.Refreshing => "Actualisation des amis…",
            FriendsOperationState.SendingRequest => "Envoi de la demande…",
            FriendsOperationState.AcceptingRequest => "Acceptation de la demande…",
            FriendsOperationState.RejectingRequest => "Refus de la demande…",
            FriendsOperationState.CancellingRequest => "Annulation de la demande…",
            FriendsOperationState.RemovingFriend => "Retrait de l’ami…",
            _ when snapshot.LoadState == FriendsLoadState.Idle =>
                "Ouvre ou actualise le panneau pour charger tes amis.",
            _ when snapshot.LoadState == FriendsLoadState.Loaded && snapshot.IsStale =>
                "Données précédentes conservées · actualisation indisponible.",
            _ => string.Empty
        };
    }

    private ImageSource? ResolveAvatarImage(AvatarDescriptor descriptor)
    {
        if (_avatarImages is not null
            && _avatarImages.TryGetMemory(descriptor, SocialAvatarSize, out System.Windows.Media.Imaging.BitmapSource? image))
        {
            return image;
        }

        return null;
    }

    private void QueueMissingAvatars(FriendsRuntimeSnapshot snapshot)
    {
        if (_avatarImages is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        FriendRuntimeItem[] items = Enumerate(snapshot).ToArray();
        HashSet<FriendAvatarIdentity> expected = items
            .Where(friend => friend.Avatar is not null)
            .Select(friend => new FriendAvatarIdentity(
                friend.AccountId,
                friend.Avatar!.AvatarId,
                friend.Avatar.Version))
            .ToHashSet();
        foreach (FriendAvatarIdentity attempted in _avatarAttempts.Keys)
        {
            if (!expected.Contains(attempted))
            {
                _avatarAttempts.TryRemove(attempted, out _);
            }
        }

        foreach (FriendRuntimeItem friend in items)
        {
            if (friend.Avatar is not AvatarDescriptor descriptor
                || ResolveAvatarImage(descriptor) is not null)
            {
                continue;
            }

            FriendAvatarIdentity identity = new(
                friend.AccountId,
                descriptor.AvatarId,
                descriptor.Version);
            if (_avatarAttempts.TryAdd(identity, 0)
                && _avatarLoads.TryAdd(identity, 0))
            {
                _ = LoadAvatarAsync(identity, descriptor);
            }
        }
    }

    private async Task LoadAvatarAsync(
        FriendAvatarIdentity identity,
        AvatarDescriptor descriptor)
    {
        ImageSource? image = null;
        try
        {
            image = await _avatarImages!.GetAsync(
                descriptor,
                SocialAvatarSize,
                _avatarLoadCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_avatarLoadCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _avatarLoads.TryRemove(identity, out _);
        }

        if (image is null || Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (Volatile.Read(ref _disposeState) != 0
                    || _latestSnapshot is not FriendsRuntimeSnapshot latest
                    || !ContainsAvatar(latest, identity))
                {
                    return;
                }

                _target.ApplyAvatarImage(
                    identity.AccountId,
                    identity.AvatarId,
                    identity.Version,
                    image);
            }, DispatcherPriority.DataBind);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private static IEnumerable<FriendRuntimeItem> Enumerate(FriendsRuntimeSnapshot snapshot) =>
        snapshot.Friends
            .Concat(snapshot.IncomingRequests)
            .Concat(snapshot.OutgoingRequests);

    private static bool ContainsAvatar(
        FriendsRuntimeSnapshot snapshot,
        FriendAvatarIdentity identity)
    {
        return Enumerate(snapshot).Any(friend =>
            friend.AccountId == identity.AccountId
            && friend.Avatar is AvatarDescriptor descriptor
            && descriptor.AvatarId == identity.AvatarId
            && descriptor.Version == identity.Version);
    }

    private readonly record struct FriendAvatarIdentity(
        uint AccountId,
        Guid AvatarId,
        ulong Version);

    internal static string MapError(FriendsErrorCategory category)
    {
        return category switch
        {
            FriendsErrorCategory.None => string.Empty,
            FriendsErrorCategory.Validation => "Vérifie le nom saisi puis réessaie.",
            FriendsErrorCategory.Self => "Tu ne peux pas t’ajouter toi-même.",
            FriendsErrorCategory.UserNotFound => "Aucun compte Atlas ne porte ce nom.",
            FriendsErrorCategory.RelationNotFound => "Cette demande n’est plus disponible.",
            FriendsErrorCategory.AlreadyPending => "Une demande est déjà en attente.",
            FriendsErrorCategory.AlreadyFriends => "Ce compte fait déjà partie de tes amis.",
            FriendsErrorCategory.Unauthorized => "Ta session Atlas doit être renouvelée.",
            FriendsErrorCategory.Forbidden => "Cette action n’est pas autorisée.",
            FriendsErrorCategory.Network => "Impossible de joindre Atlas pour le moment.",
            FriendsErrorCategory.Timeout => "Atlas met trop de temps à répondre. Réessaie.",
            FriendsErrorCategory.ServiceUnavailable => "Le service Amis est temporairement indisponible.",
            FriendsErrorCategory.ServerRejected => "Atlas n’a pas pu effectuer cette action.",
            _ => "Une erreur inattendue empêche cette action."
        };
    }

    private static string MapNotice(FriendsNoticeKind notice)
    {
        return notice switch
        {
            FriendsNoticeKind.RequestSent => "Demande d’ami envoyée.",
            FriendsNoticeKind.FriendshipAccepted => "Vous êtes maintenant amis sur Atlas.",
            FriendsNoticeKind.RequestAccepted => "Demande d’ami acceptée.",
            FriendsNoticeKind.RequestRejected => "Demande refusée.",
            FriendsNoticeKind.RequestCancelled => "Demande envoyée annulée.",
            FriendsNoticeKind.FriendRemoved => "Ami retiré de ta liste.",
            _ => string.Empty
        };
    }

    internal static string GetPresenceText(
        FriendRuntimeItem friend,
        DateTimeOffset? now = null)
    {
        if (friend.IsOnline)
        {
            string zone = GetZoneName(friend.ZoneId);
            return zone.Length == 0 ? "En jeu" : $"En jeu · {zone}";
        }

        if (friend.LastSeenAt is null)
        {
            return "Hors ligne";
        }

        DateTimeOffset localNow = (now ?? DateTimeOffset.Now).ToLocalTime();
        DateTimeOffset localLastSeen = friend.LastSeenAt.Value.ToLocalTime();
        int elapsedDays = (localNow.Date - localLastSeen.Date).Days;
        return elapsedDays switch
        {
            0 => $"Aujourd’hui à {localLastSeen:HH:mm}",
            1 => $"Hier à {localLastSeen:HH:mm}",
            _ when localNow.Year == localLastSeen.Year => $"Vu le {localLastSeen:dd/MM à HH:mm}",
            _ => $"Vu le {localLastSeen:dd/MM/yyyy à HH:mm}"
        };
    }

    private static string GetCharacterPresenceText(FriendCharacterRuntimeItem character)
    {
        if (character.IsOnline)
        {
            string zone = GetZoneName(character.ZoneId);
            return zone.Length == 0 ? "En jeu" : $"En jeu · {zone}";
        }
        if (character.LastSeenAt is null)
        {
            return "Hors ligne";
        }

        DateTimeOffset localLastSeen = character.LastSeenAt.Value.ToLocalTime();
        return $"Vu le {localLastSeen:dd/MM/yyyy à HH:mm}";
    }

    internal static string GetZoneName(uint? zoneId) => zoneId switch
    {
        1 => "Dun Morogh",
        3 => "Terres ingrates",
        4 => "Terres foudroyées",
        8 => "Marais des Chagrins",
        10 => "Bois de la Pénombre",
        11 => "Les Paluns",
        12 => "Forêt d’Elwynn",
        14 => "Durotar",
        15 => "Marécage d’Âprefange",
        16 => "Azshara",
        17 => "Les Tarides",
        28 => "Maleterres de l’Ouest",
        33 => "Vallée de Strangleronce",
        36 => "Montagnes d’Alterac",
        38 => "Loch Modan",
        40 => "Marche de l’Ouest",
        41 => "Défilé de Deuillevent",
        44 => "Les Carmines",
        45 => "Hautes-terres d’Arathi",
        46 => "Steppes ardentes",
        47 => "Les Hinterlands",
        51 => "Gorge des Vents brûlants",
        65 => "Désolation des dragons",
        66 => "Zul’Drak",
        67 => "Les pics Foudroyés",
        85 => "Clairières de Tirisfal",
        130 => "Forêt des Pins argentés",
        139 => "Maleterres de l’Est",
        141 => "Teldrassil",
        148 => "Sombrivage",
        210 => "Couronne de glace",
        215 => "Mulgore",
        267 => "Contreforts de Hautebrande",
        331 => "Orneval",
        357 => "Féralas",
        361 => "Gangrebois",
        394 => "Les Grisonnes",
        400 => "Mille pointes",
        405 => "Désolace",
        406 => "Les Serres-Rocheuses",
        440 => "Tanaris",
        490 => "Cratère d’Un’Goro",
        493 => "Reflet-de-Lune",
        495 => "Fjord Hurlant",
        618 => "Berceau-de-l’Hiver",
        1377 => "Silithus",
        1497 => "Fossoyeuse",
        1519 => "Hurlevent",
        1537 => "Forgefer",
        1637 => "Orgrimmar",
        1638 => "Les Pitons-du-Tonnerre",
        1657 => "Darnassus",
        2817 => "Forêt du Chant de cristal",
        3430 => "Bois des Chants éternels",
        3433 => "Les Terres fantômes",
        3483 => "Péninsule des Flammes infernales",
        3518 => "Nagrand",
        3519 => "Forêt de Terokkar",
        3520 => "Vallée d’Ombrelune",
        3521 => "Marécage de Zangar",
        3522 => "Les Tranchantes",
        3523 => "Raz-de-Néant",
        3524 => "Île de Brume-Azur",
        3525 => "Île de Brume-Sang",
        3537 => "Toundra Boréenne",
        3557 => "L’Exodar",
        3703 => "Shattrath",
        3711 => "Bassin de Sholazar",
        4080 => "Île de Quel’Danas",
        4197 => "Joug-d’hiver",
        4395 => "Dalaran",
        _ => string.Empty
    };

    private static string GetClassName(byte? classId) => classId switch
    {
        1 => "Guerrier",
        2 => "Paladin",
        3 => "Chasseur",
        4 => "Voleur",
        5 => "Prêtre",
        6 => "Chevalier de la mort",
        7 => "Chaman",
        8 => "Mage",
        9 => "Démoniste",
        11 => "Druide",
        _ => "Personnage"
    };

    private static string GetAvatarColor(string? avatarKey) =>
        avatarKey?.Trim().ToLowerInvariant() switch
        {
            "ice" => "#61D5E8",
            "emerald" => "#51D7A2",
            "crimson" => "#EE6571",
            _ => "#DEB75A"
        };
}
