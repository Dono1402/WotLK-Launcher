using System.Globalization;
using System.Text.Json;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2;

internal sealed record ChatOwnCharacter(string Guid, string Name, int Level, int ClassId, int RaceId, int Gender, string RealmName = "Arthas");

public partial class LauncherShellV2
{
    private Guid _richCharacterSession;
    private uint _richCharacterOwner;
    private CancellationTokenSource _richCharacterLifetime = new();
    private Task? _richCharacterRequest;
    private string _richCharacterStatus = "idle";
    private string? _richCharacterError;
    private IReadOnlyList<ChatOwnCharacter> _richCharacters = [];

    private object OwnCharactersProjection => new { status = _richCharacterStatus, characters = _richCharacters, error = _richCharacterError };

    private void EnsureRichCharacterSession(ChatWorkspaceSnapshot snapshot)
    {
        if (_richCharacterSession == snapshot.SessionId && _richCharacterOwner == snapshot.OwnerAccountId) return;
        _richCharacterLifetime.Cancel();
        _richCharacterLifetime.Dispose();
        _richCharacterLifetime = new();
        _richCharacterSession = snapshot.SessionId;
        _richCharacterOwner = snapshot.OwnerAccountId;
        _richCharacterRequest = null;
        _richCharacterStatus = "idle";
        _richCharacterError = null;
        _richCharacters = [];
    }

    private async Task RequestRichOwnCharactersAsync(LauncherChatWorkspace workspace)
    {
        if (_richCharacterRequest is { IsCompleted: false } running) { await running; return; }
        ChatWorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        EnsureRichCharacterSession(snapshot);
        CancellationToken token = _richCharacterLifetime.Token;
        _richCharacterStatus = "loading";
        _richCharacterError = null;
        workspace.RefreshPresentation();
        _richCharacterRequest = ReadAsync();
        await _richCharacterRequest;

        async Task ReadAsync()
        {
            try
            {
                JsonElement roster = await ArmoryView.ReadOwnCharactersForChatAsync(snapshot.OwnerAccountId, token);
                token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(workspace, _chatWorkspace) || workspace.CurrentSnapshot.SessionId != snapshot.SessionId) return;
                _richCharacters = ParseOwnCharacters(roster);
                _richCharacterStatus = "ready";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                if (token.IsCancellationRequested || !ReferenceEquals(workspace, _chatWorkspace)
                    || workspace.CurrentSnapshot.SessionId != snapshot.SessionId) return;
                _richCharacters = [];
                _richCharacterStatus = "error";
                _richCharacterError = "chat-armory-unavailable";
            }
            workspace.RefreshPresentation();
        }
    }

    internal static IReadOnlyList<ChatOwnCharacter> ParseOwnCharacters(JsonElement roster)
    {
        if (!roster.TryGetProperty("characters", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 1000)
            throw new JsonException("Invalid own character roster.");
        List<ChatOwnCharacter> characters = [];
        HashSet<uint> identifiers = [];
        foreach (JsonElement row in rows.EnumerateArray())
        {
            JsonElement character = row.GetProperty("character");
            uint guid = character.GetProperty("guid").GetUInt32();
            string name = character.GetProperty("name").GetString() ?? "";
            int level = character.GetProperty("level").GetInt32(), classId = character.GetProperty("classId").GetInt32();
            int race = character.GetProperty("race").GetInt32(), gender = character.GetProperty("gender").GetInt32();
            if (guid == 0 || !identifiers.Add(guid) || name.Length is < 1 or > 80 || name.Any(char.IsControl)
                || level is < 1 or > 80 || classId is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 11)
                || race is < 1 or > 11 || gender is < 0 or > 1) throw new JsonException("Invalid own character.");
            characters.Add(new(guid.ToString(CultureInfo.InvariantCulture), name, level, classId, race, gender));
        }
        return characters;
    }

    private async Task<object> SelectRichOwnCharacterAsync(LauncherChatWorkspace workspace, string threadId, string guid)
    {
        ChatWorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        if (_richCharacterStatus != "ready" || _richCharacterSession != snapshot.SessionId || _richCharacterOwner != snapshot.OwnerAccountId
            || snapshot.SelectedThreadId != threadId) throw new ChatWorkspaceException("chat-armory-unavailable");
        ChatOwnCharacter character = _richCharacters.SingleOrDefault(item => item.Guid == guid)
            ?? throw new ChatWorkspaceException("chat-character-not-owned");
        ChatCardDto card = new() { Kind = "character", Title = character.Name, ReferenceId = character.Guid,
            Fields = new Dictionary<string, string> { ["ownerAccountId"] = snapshot.OwnerAccountId.ToString(CultureInfo.InvariantCulture),
                ["characterGuid"] = character.Guid, ["level"] = character.Level.ToString(CultureInfo.InvariantCulture),
                ["classId"] = character.ClassId.ToString(CultureInfo.InvariantCulture), ["raceId"] = character.RaceId.ToString(CultureInfo.InvariantCulture) } };
        await workspace.SetDraftCardAsync(threadId, card);
        return new { card };
    }

    private void OpenRichCharacterArmory(uint ownerAccountId, string guid)
    {
        if (!uint.TryParse(guid, NumberStyles.None, CultureInfo.InvariantCulture, out uint characterGuid) || characterGuid == 0
            || guid != characterGuid.ToString(CultureInfo.InvariantCulture) || !ArmoryView.IsConfigured || !IsAccountNavigationEnabled)
            throw new ChatWorkspaceException("chat-profile-unavailable");
        if (_chatWorkspace?.CurrentSnapshot.OwnerAccountId == ownerAccountId) ArmoryView.ShowOwnProfile();
        else if (FriendsState.Current.Friends.FirstOrDefault(friend => friend.AccountId == ownerAccountId) is { } friend)
            ArmoryView.ShowFriendProfile(friend);
        else throw new ChatWorkspaceException("chat-profile-unavailable");
        ArmoryView.SelectSharedCharacter(ownerAccountId, characterGuid);
        NavigateTo(LauncherShellPage.Armory);
    }
}
