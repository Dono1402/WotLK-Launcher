using System.Text.Json;
using WotLK.Launcher.Runtime;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ArmoryViewV2
{
    private uint? _requestedCharacterOwner;
    private string? _requestedCharacterGuid;
    private string? _requestedCharacterNonce;

    internal async Task<JsonElement> ReadOwnCharactersForChatAsync(uint ownerAccountId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var getAccount = _getAccount;
        var readData = _readData;
        if (ownerAccountId == 0 || getAccount is null || readData is null || _state?.IsNavigationEnabled != true)
            throw new UnauthorizedAccessException("Armory session unavailable.");
        if (await getAccount(cancellationToken) != ownerAccountId)
            throw new UnauthorizedAccessException("Armory session changed.");
        // This is the authenticated own-account endpoint, never a helper cache
        // or the friend profile currently displayed by the armory page.
        JsonElement roster = await readData(ownerAccountId, new LauncherArmoryDataRequest(1, "roster"), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(getAccount, _getAccount) || !ReferenceEquals(readData, _readData)
            || _state?.IsNavigationEnabled != true)
            throw new UnauthorizedAccessException("Armory session changed.");
        return roster;
    }

    internal void SelectSharedCharacter(uint ownerAccountId, uint characterGuid)
    {
        if (ownerAccountId == 0 || characterGuid == 0) throw new ArgumentOutOfRangeException(nameof(characterGuid));
        _requestedCharacterOwner = ownerAccountId;
        _requestedCharacterGuid = characterGuid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _requestedCharacterNonce = Guid.NewGuid().ToString("N");
        PublishProfile();
    }

    private string? RequestedCharacterGuid => (_friendProfile?.AccountId ?? _sessionAccountId) == _requestedCharacterOwner
        ? _requestedCharacterGuid : null;

    private void ResetSharedCharacter()
    {
        _requestedCharacterOwner = null;
        _requestedCharacterGuid = null;
        _requestedCharacterNonce = null;
    }
}
