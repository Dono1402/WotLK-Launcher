using System.IO;
using Microsoft.Win32;

namespace WotLK.Launcher.Account;

internal interface IAvatarFilePicker
{
    string? PickImagePath();
}

internal sealed class WindowsAvatarFilePicker : IAvatarFilePicker
{
    public string? PickImagePath()
    {
        OpenFileDialog dialog = new()
        {
            Title = "Choisir une photo de profil",
            Filter = "Images JPEG, PNG ou WebP|*.jpg;*.jpeg;*.png;*.webp|JPEG|*.jpg;*.jpeg|PNG|*.png|WebP|*.webp",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

internal enum AvatarSelectionFailureCategory
{
    FileUnavailable,
    TooLarge,
    UnsupportedFormat,
    InvalidImage,
    AccessDenied
}

internal sealed class AvatarSelectionException : Exception
{
    internal AvatarSelectionException(
        AvatarSelectionFailureCategory category,
        Exception? innerException = null)
        : base($"Avatar selection failed: {category}.", innerException)
    {
        Category = category;
    }

    internal AvatarSelectionFailureCategory Category { get; }
}

internal sealed class AvatarFileSelectionService
{
    private readonly IAvatarFilePicker _picker;

    internal AvatarFileSelectionService(IAvatarFilePicker picker)
    {
        _picker = picker ?? throw new ArgumentNullException(nameof(picker));
    }

    internal async Task<AvatarPreviewImage?> PickAndLoadAsync(CancellationToken cancellationToken)
    {
        string? path = _picker.PickImagePath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string contentType = GetContentType(path);
        byte[] bytes;
        try
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                throw new AvatarSelectionException(AvatarSelectionFailureCategory.FileUnavailable);
            }
            if (file.Length > AvatarMediaClient.MaximumUploadBytes)
            {
                throw new AvatarSelectionException(AvatarSelectionFailureCategory.TooLarge);
            }

            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (AvatarSelectionException)
        {
            throw;
        }
        catch (FileNotFoundException exception)
        {
            throw new AvatarSelectionException(
                AvatarSelectionFailureCategory.FileUnavailable,
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AvatarSelectionException(
                AvatarSelectionFailureCategory.AccessDenied,
                exception);
        }
        catch (IOException exception)
        {
            throw new AvatarSelectionException(
                AvatarSelectionFailureCategory.FileUnavailable,
                exception);
        }

        try
        {
            return await Task.Run(
                () => AvatarWpfImageDecoder.DecodePreview(bytes, contentType),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                           or NotSupportedException
                                           or FileFormatException)
        {
            throw new AvatarSelectionException(
                AvatarSelectionFailureCategory.InvalidImage,
                exception);
        }
    }

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => throw new AvatarSelectionException(
                AvatarSelectionFailureCategory.UnsupportedFormat)
        };
    }
}
