using System.Windows.Media.Imaging;
using WotLK.Launcher.Account;
using WotLK.Launcher.UI.V2.Presentation;

namespace WotLK.Launcher.UI.V2.Views;

public partial class ArmoryViewV2
{
    private const int FriendProfileAvatarSize = 256;
    private AvatarImageCache? _friendAvatarImages;
    private FriendProfileAvatarRequest? _friendAvatarRequest;
    private BitmapSource? _friendAvatarImage;

    private BitmapSource? ResolveFriendProfileAvatar(FriendUiItem friend)
    {
        FriendProfileAvatarRequest? request = friend.AvatarDescriptor is { } descriptor
            ? new(friend.AccountId, descriptor) : null;
        if (_friendAvatarRequest != request)
        {
            _friendAvatarRequest = request;
            _friendAvatarImage = null;
            if (request is not null && _friendAvatarImages is { } cache && _lifetime is { } lifetime)
            {
                if (cache.TryGetMemory(request.Descriptor, FriendProfileAvatarSize, out BitmapSource? image))
                    _friendAvatarImage = image;
                else
                    _ = LoadFriendProfileAvatarAsync(cache, request, lifetime.Token);
            }
        }

        // The social list's 64 px thumbnail is only a loading/failure fallback.
        // The profile displays 160 CSS pixels (200 physical pixels at 125% DPI).
        return _friendAvatarImage ?? friend.AvatarImage as BitmapSource;
    }

    private async Task LoadFriendProfileAvatarAsync(
        AvatarImageCache cache, FriendProfileAvatarRequest request, CancellationToken token)
    {
        try
        {
            BitmapSource? image = await cache.GetAsync(request.Descriptor, FriendProfileAvatarSize, token);
            if (image is null || token.IsCancellationRequested || _lifetime?.Token != token
                || _friendAvatarRequest != request || _friendProfile?.AccountId != request.AccountId
                || _friendProfile.AvatarDescriptor != request.Descriptor) return;
            _friendAvatarImage = image;
            PublishProfile();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    private sealed record FriendProfileAvatarRequest(uint AccountId, AvatarDescriptor Descriptor);
}
