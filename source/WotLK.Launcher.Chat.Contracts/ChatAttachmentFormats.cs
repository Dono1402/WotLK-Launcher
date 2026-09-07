using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace WotLK.Launcher.Chat;

public sealed record ChatAttachmentFormat(string Extension, string ContentType, string Kind, string Container)
{
    public string ResponseContentType => ContentType.StartsWith("text/", StringComparison.Ordinal)
        ? ContentType + "; charset=utf-8" : ContentType;
}

/// <summary>
/// One upload allowlist for the launcher, native picker and service. Acceptance
/// describes the container; embedded playback still depends on installed codecs.
/// </summary>
public static class ChatAttachmentFormats
{
    public static IReadOnlyList<ChatAttachmentFormat> All { get; } = Array.AsReadOnly(new ChatAttachmentFormat[]
    {
        new(".png", "image/png", "image", "png"), new(".jpg", "image/jpeg", "image", "jpeg"),
        new(".jpeg", "image/jpeg", "image", "jpeg"), new(".gif", "image/gif", "image", "gif"),
        new(".webp", "image/webp", "image", "webp"), new(".pdf", "application/pdf", "document", "pdf"),
        new(".txt", "text/plain", "document", "text"), new(".md", "text/markdown", "document", "text"),
        new(".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "document", "office"),
        new(".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "document", "office"),
        new(".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "document", "office"),
        new(".odt", "application/vnd.oasis.opendocument.text", "document", "office"),
        new(".ods", "application/vnd.oasis.opendocument.spreadsheet", "document", "office"),
        new(".odp", "application/vnd.oasis.opendocument.presentation", "document", "office"),
        new(".mp3", "audio/mpeg", "audio", "mpeg-audio"), new(".mp2", "audio/mpeg", "audio", "mpeg-audio"),
        new(".aac", "audio/aac", "audio", "adts"), new(".m4a", "audio/mp4", "audio", "iso"),
        new(".m4b", "audio/mp4", "audio", "iso"), new(".flac", "audio/flac", "audio", "flac"),
        new(".ogg", "audio/ogg", "audio", "ogg"), new(".oga", "audio/ogg", "audio", "ogg"),
        new(".opus", "audio/ogg", "audio", "ogg"), new(".spx", "audio/ogg", "audio", "ogg"),
        new(".wav", "audio/wav", "audio", "wave"), new(".aif", "audio/aiff", "audio", "aiff"),
        new(".aiff", "audio/aiff", "audio", "aiff"), new(".aifc", "audio/aiff", "audio", "aiff"),
        new(".wma", "audio/x-ms-wma", "audio", "asf"), new(".mka", "audio/x-matroska", "audio", "ebml"),
        new(".weba", "audio/webm", "audio", "ebml"),
        new(".mp4", "video/mp4", "video", "iso"), new(".m4v", "video/mp4", "video", "iso"),
        new(".mov", "video/quicktime", "video", "iso"), new(".qt", "video/quicktime", "video", "iso"),
        new(".3gp", "video/3gpp", "video", "iso"), new(".3g2", "video/3gpp2", "video", "iso"),
        new(".webm", "video/webm", "video", "ebml"), new(".mkv", "video/x-matroska", "video", "ebml"),
        new(".avi", "video/x-msvideo", "video", "avi"),
        new(".mpg", "video/mpeg", "video", "mpeg-video"), new(".mpeg", "video/mpeg", "video", "mpeg-video"),
        new(".mpe", "video/mpeg", "video", "mpeg-video"), new(".m1v", "video/mpeg", "video", "mpeg-video"),
        new(".m2v", "video/mpeg", "video", "mpeg-video"),
        new(".ts", "video/mp2t", "video", "mpeg-ts"), new(".mts", "video/mp2t", "video", "mpeg-ts"),
        new(".m2ts", "video/mp2t", "video", "mpeg-ts"), new(".ogv", "video/ogg", "video", "ogg"),
        new(".wmv", "video/x-ms-wmv", "video", "asf"), new(".asf", "video/x-ms-asf", "video", "asf"),
        new(".flv", "video/x-flv", "video", "flv")
    });

    private static readonly FrozenDictionary<string, ChatAttachmentFormat> ByExtension = All.ToFrozenDictionary(
        format => format.Extension, StringComparer.OrdinalIgnoreCase);
    public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(All.Select(format => format.Extension).ToArray());
    public static string NativeFileDialogFilterPattern { get; } = string.Join(';', Extensions.Select(extension => "*" + extension));

    public static bool TryGetByFileName(string fileName, [NotNullWhen(true)] out ChatAttachmentFormat? format)
        => ByExtension.TryGetValue(Path.GetExtension(fileName), out format);

    public static string? MediaKindForContentType(string? contentType)
    {
        string mime = (contentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
        mime = mime switch
        {
            "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "audio/wav",
            "audio/x-flac" => "audio/flac", "audio/x-aiff" => "audio/aiff",
            "audio/x-m4a" or "audio/x-m4b" => "audio/mp4", "audio/x-aac" => "audio/aac",
            "video/avi" or "video/msvideo" => "video/x-msvideo", "video/x-m4v" => "video/mp4",
            _ => mime
        };
        return All.FirstOrDefault(format => format.ContentType == mime && format.Kind is "audio" or "video")?.Kind;
    }
}
