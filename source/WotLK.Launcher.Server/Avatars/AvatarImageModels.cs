namespace WotLK.Launcher.Server.Avatars;

internal sealed record NormalizedAvatarCrop(
    double X,
    double Y,
    double Width,
    double Height);

internal sealed record ProcessedAvatarImage(
    string SourceContentType,
    int SourceWidth,
    int SourceHeight,
    int OrientedWidth,
    int OrientedHeight,
    IReadOnlyDictionary<int, byte[]> Variants);

internal sealed class AvatarImageValidationException : Exception
{
    internal AvatarImageValidationException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}
