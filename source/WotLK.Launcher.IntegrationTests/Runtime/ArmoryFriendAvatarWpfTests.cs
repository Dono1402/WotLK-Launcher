using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WotLK.Launcher.Account;
using WotLK.Launcher.UI.V2.Presentation;
using WotLK.Launcher.UI.V2.Views;

internal static partial class ArmoryLauncherTests
{
    private static async Task ValidateFriendProfileAvatarAsync(ArmoryViewV2 armory, FriendUiItem friend,
        ProfileAvatarMediaClient media, AvatarImageCache cache)
    {
        await AssertProfileAvatarAsync(armory, 256, 1,
            "Le grand portrait doit recevoir la variante 256 px avec ses détails natifs, pas la vignette 64 px agrandie.");
        Equal(1, media.Downloads.Count, "L'ouverture d'un profil doit télécharger une seule grande variante.");
        True(media.Downloads.All(download => download.Size == 256), "La grande vue doit demander uniquement la variante 256 px.");

        FriendUiItem revised = friend with { AvatarDescriptor = ProfileAvatarDescriptor(2) };
        armory.UpdateFriendProfile(revised);
        await AssertProfileAvatarAsync(armory, 256, 2, "Une nouvelle version doit remplacer l'ancien portrait.");
        Equal(2, media.Downloads.Count, "La nouvelle version ne doit pas réutiliser les anciens pixels.");

        AvatarDescriptor pending = ProfileAvatarDescriptor(3);
        TaskCompletionSource<byte[]> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        media.Pending[pending] = release.Task;
        armory.UpdateFriendProfile(friend with { AvatarDescriptor = pending });
        await WaitUntilAsync(() => media.Downloads.Any(download => download.Descriptor == pending),
            "Le test doit intercepter un téléchargement de l'ancien profil encore en cours.");
        await AssertProfileAvatarAsync(armory, 64, 64, "La vignette doit rester disponible pendant le chargement.");
        FriendUiItem second = friend with { AccountId = 92, Username = "AvatarSecondFriend", AvatarDescriptor = ProfileAvatarDescriptor(10) };
        armory.ShowFriendProfile(second);
        await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='AvatarSecondFriend'",
            "Le second profil doit s'ouvrir pendant le téléchargement de l'ancien.");
        await AssertProfileAvatarAsync(armory, 256, 10, "Le second profil doit charger son propre portrait.");
        release.TrySetResult(CreateProfileAvatarPng(256, 3));
        await cache.GetAsync(pending, 256, CancellationToken.None);
        await PumpAsync();
        await AssertProfileAvatarAsync(armory, 256, 10, "La réponse tardive du premier ami ne doit pas contaminer le second.");

        AvatarDescriptor unavailable = ProfileAvatarDescriptor(4);
        media.Unavailable = unavailable;
        armory.ShowFriendProfile(friend with { AvatarDescriptor = unavailable });
        await WaitForScriptAsync(armory, "document.getElementById('profile-name').textContent==='AmiAtlas'",
            "L'avatar indisponible ne doit pas empêcher l'ouverture du profil.");
        await WaitUntilAsync(() => media.Downloads.Any(download => download.Descriptor == unavailable),
            "La grande variante indisponible doit être tentée.");
        await AssertProfileAvatarAsync(armory, 64, 64, "Un échec de chargement doit conserver la vignette disponible.");
        armory.UpdateFriendProfile(friend with { AvatarDescriptor = null, AvatarImage = null, HasAvatarImage = false });
        await WaitForScriptAsync(armory, "document.getElementById('profile-avatar').hidden && !document.getElementById('profile-avatar').hasAttribute('src')",
            "Supprimer la photo doit aussi vider le portrait mis en cache par la vue.");

        int beforeReopen = media.Downloads.Count;
        armory.UpdateFriendProfile(friend);
        await AssertProfileAvatarAsync(armory, 256, 1, "Revenir à l'avatar initial doit récupérer sa grande variante en cache.");
        Equal(beforeReopen, media.Downloads.Count, "Revenir à une version connue ne doit pas retélécharger la photo.");
        Console.WriteLine("Friend avatar WPF OK: true 256px PNG/pixel detail, 64px loading and failure fallback, version cache separation, deletion, late A-to-B response isolation and memory reuse.");
    }

    private static Task AssertProfileAvatarAsync(ArmoryViewV2 armory, int size, byte marker, string message) =>
        WaitForScriptAsync(armory, $$"""
            (() => {
              const image = document.getElementById('profile-avatar');
              if (image.hidden || !image.complete || image.naturalWidth !== {{size}} || image.naturalHeight !== {{size}}) return false;
              const canvas = document.createElement('canvas'); canvas.width = canvas.height = image.naturalWidth;
              const context = canvas.getContext('2d'); context.drawImage(image, 0, 0);
              const pixels = context.getImageData(0, 0, 2, 1).data;
              return pixels[0] === 224 && pixels[4] === 24 && pixels[2] === {{marker}} && pixels[6] === {{marker}};
            })()
            """, message);

    private static AvatarDescriptor ProfileAvatarDescriptor(ulong version)
    {
        Guid id = Guid.Parse("175e96d8-b2ce-440b-a684-a4c73801b505");
        string root = $"/media/avatars/{id:N}/{version}";
        return new(id, version, $"{root}/32.png", $"{root}/64.png", $"{root}/128.png", $"{root}/256.png");
    }

    private static byte[] CreateProfileAvatarPng(int size, byte marker)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int offset = (y * size + x) * 4;
            pixels[offset] = marker;
            pixels[offset + 1] = 120;
            pixels[offset + 2] = (byte)(x % 2 == 0 ? 224 : 24);
            pixels[offset + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        using MemoryStream stream = new();
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return stream.ToArray();
    }

    private sealed class ProfileAvatarMediaClient : IAvatarMediaClient
    {
        internal ConcurrentQueue<(AvatarDescriptor Descriptor, int Size)> Downloads { get; } = new();
        internal ConcurrentDictionary<AvatarDescriptor, Task<byte[]>> Pending { get; } = new();
        internal AvatarDescriptor? Unavailable { get; set; }

        public async Task<AvatarMediaDownloadResult> DownloadAvatarAsync(AvatarDescriptor descriptor, int size, CancellationToken cancellationToken)
        {
            Downloads.Enqueue((descriptor, size));
            if (descriptor == Unavailable) throw new AvatarMediaException(AvatarMediaFailureCategory.Network);
            byte[] bytes = Pending.TryGetValue(descriptor, out Task<byte[]>? pending)
                ? await pending.WaitAsync(cancellationToken) : CreateProfileAvatarPng(size, (byte)descriptor.Version);
            return AvatarMediaDownloadResult.Success(bytes);
        }

        public Task<AvatarProfileReadResult> GetProfileAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AvatarDescriptor> UploadAvatarAsync(AvatarUploadRequest upload, IProgress<AvatarUploadTransferProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAvatarAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
