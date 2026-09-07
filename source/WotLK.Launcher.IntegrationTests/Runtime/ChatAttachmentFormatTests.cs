using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WotLK.Launcher.Chat;
using WotLK.Launcher.Runtime;
using WotLK.Launcher.Server;

/// <summary>Small container fixtures exercise the acceptance boundary, not codec playback.</summary>
internal static class ChatAttachmentFormatTests
{
    private static int _checks;
    private static readonly CancellationToken None = CancellationToken.None;

    internal static async Task<int> RunAsync(string root)
    {
        _checks = 0;
        ChatAttachmentStorage storage = new(Path.Combine(root, "formats-server"));
        ChatAttachmentFileSource source = new(Path.Combine(root, "formats-stage"), new Uri("https://fixture.invalid/api/"));
        string inputs = Path.Combine(root, "formats-input");
        Directory.CreateDirectory(inputs);
        Check(ChatAttachmentFormats.All.Count == 52 && ChatAttachmentFormats.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 52,
            "Exactly 52 distinct registered extensions.");
        foreach (ChatAttachmentFormat format in ChatAttachmentFormats.All)
        {
            Check(ChatAttachmentFormats.TryGetByFileName("FILE" + format.Extension.ToUpperInvariant(), out ChatAttachmentFormat? found) && found == format,
                "Case-insensitive registry: " + format.Extension);
            Check(ChatAttachmentFileSource.ContentTypeForName("file" + format.Extension) == format.ContentType
                && ChatAttachmentFormats.NativeFileDialogFilterPattern.Split(';').Contains("*" + format.Extension),
                "Client MIME and native picker share the registry: " + format.Extension);
            if (format.Kind is not ("audio" or "video")) continue;
            byte[] bytes = Fixture(format);
            using (MemoryStream stream = new(bytes))
            {
                stream.Position = Math.Min(3, stream.Length);
                Check(ChatMediaSignatures.Matches(stream, format) && stream.Position == 3, "Container accepted with read position restored: " + format.Extension);
            }
            ChatUploadDto upload = await Upload(storage, "sample" + format.Extension, bytes);
            Check(upload.IsComplete && upload.Attachment?.Kind == format.Kind && upload.Attachment.ContentType == format.ResponseContentType
                && upload.Attachment.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                "Server derives media kind, MIME and exact SHA despite a spoofed request MIME: " + format.Extension);
            string input = Path.Combine(inputs, "sample" + format.Extension);
            await File.WriteAllBytesAsync(input, bytes, None);
            ChatSelectedFile staged = await source.InspectAsync(7, input, None);
            Check(staged.ContentType == format.ContentType && staged.Size == bytes.Length
                && (await File.ReadAllBytesAsync(staged.SourcePath, None)).SequenceEqual(bytes),
                "Local staging preserves complete validated container bytes: " + format.Extension);
            await source.DeleteStagedAsync(7, staged.SourcePath, None);
            foreach (byte[] invalid in new[] { "MZ renamed executable"u8.ToArray(), "<html><script>bad()</script></html>"u8.ToArray(), bytes[..Math.Min(8, bytes.Length)] })
            {
                using MemoryStream rejected = new(invalid);
                Check(!ChatMediaSignatures.Matches(rejected, format), "Renamed or truncated media rejected: " + format.Extension);
                await Reject(storage, "bad" + format.Extension, invalid);
                await File.WriteAllBytesAsync(input, invalid, None);
                bool localRejected = false;
                try { await source.InspectAsync(7, input, None); }
                catch (ChatWorkspaceException error) when (error.Code == "chat-file-content-mismatch") { localRejected = true; }
                Check(localRejected, "Client rejects the same invalid container: " + format.Extension);
            }
        }
        await Mutations(storage);
        await LinkedMedia();
        Console.WriteLine($"Attachment formats PASS: {_checks} assertions, 52 extensions / 38 audio-video containers; local staging and server finalization agree. Synthetic headers, no codec-playback claim.");
        return _checks;
    }

    private static async Task Mutations(ChatAttachmentStorage storage)
    {
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("wrong.mp4", Iso("evil", false)), ("wrong.mov", Iso("isom", false)),
            ("wrong.3gp", Iso("3g2a", false)), ("wrong.3g2", Iso("3gp6", false)),
            ("video.m4a", Iso("M4A ", false)), ("wrong.webm", Ebml("matroska", false)),
            ("wrong.mkv", Ebml("webm", false)), ("video.mka", Ebml("matroska", false)),
            ("vorbis.opus", Ogg("vorbis")), ("opus.spx", Ogg("opus")),
            ("theora.ogg", Ogg("theora")), ("vorbis.ogv", Ogg("vorbis")),
            ("video.wav", Avi()), ("audio.avi", Wave()), ("video.wma", Asf(false)),
            ("audio.wmv", Asf(true)), ("wrong.aifc", Aiff(false)),
            ("layer2.mp3", MpegAudio(true)), ("layer3.mp2", MpegAudio(false)),
            ("packets.m2ts", Transport(188)), ("old.mp3", "ID3sample"u8.ToArray()),
            ("old.ogg", "OggSsample"u8.ToArray()), ("old.wav", "RIFF0000WAVEsample"u8.ToArray()),
            ("old.mp4", [0,0,0,12,102,116,121,112,105,115,111,109]),
            ("unknown.webm", Join(Element(0x1a45dfa3, Element(0x4282, Text("evil")), Element(0xec, Text("webm"))), Element(0x18538067, [])))
        }) await Reject(storage, name, bytes);
        byte[] badWave = Wave(); BinaryPrimitives.WriteUInt32LittleEndian(badWave.AsSpan(4), uint.MaxValue);
        await Reject(storage, "bad-length.wav", badWave);
        byte[] badFlac = Flac(); badFlac[7] = 33;
        await Reject(storage, "bad-streaminfo.flac", badFlac);
        byte[] badAac = Adts(); badAac[2] |= 0x3c;
        await Reject(storage, "reserved-rate.aac", badAac);
        byte[] badTs = Transport(188); badTs[188 * 3] = 0;
        await Reject(storage, "bad-sync.ts", badTs);
        byte[] badFlv = Flv(); badFlv[^1] = 99;
        await Reject(storage, "bad-tag.flv", badFlv);
        byte[] zeroBox = Iso("isom", false); zeroBox[0] = zeroBox[1] = zeroBox[2] = 0; zeroBox[3] = 4;
        await Reject(storage, "short-box.mp4", zeroBox);
        byte[] rf64 = Rf64(); rf64[28] = 255;
        await Reject(storage, "oversized-data.wav", rf64);
        rf64 = Rf64(); rf64[12] = (byte)'x';
        await Reject(storage, "missing-ds64.wav", rf64);
        rf64 = Rf64(); U32(5000).CopyTo(rf64, 44);
        await Reject(storage, "oversized-ds64-table.wav", rf64);
        byte[] ebml = Ebml("webm", false); int headerEnd = Element(0x1a45dfa3, Element(0x4282, Text("webm"))).Length;
        await Reject(storage, "root-crc.webm", Join(ebml[..headerEnd], Element(0xbf, new byte[4]), ebml[headerEnd..]));
        await Reject(storage, "unknown-void.webm", Join(ebml[..headerEnd], [0xec,0xff], ebml[headerEnd..]));
        await Reject(storage, "false-terminated-doctype.webm", Ebml("evil\0webm", false));
        foreach (var (name, bytes) in new (string, byte[])[]
        {
            ("rf64.wav", Rf64()), ("rf64-table.wav", Rf64(true)),
            ("root-void.webm", Join(ebml[..headerEnd], Element(0xec, []), ebml[headerEnd..])),
            ("terminated-doctype.webm", Ebml("webm\0suffix", false)),
            ("terminated-doctype.mkv", Ebml("matroska\0", false))
        }) Check((await Upload(storage, name, bytes)).IsComplete, "Valid container variant accepted: " + name);

        ChatAttachmentFormats.TryGetByFileName("sample.mp4", out ChatAttachmentFormat? mp4);
        using MemoryStream cancelled = new(Iso("isom", false));
        cancelled.Position = 5;
        bool cancellation = false;
        try { ChatMediaSignatures.Matches(cancelled, mp4!, new CancellationToken(true)); }
        catch (OperationCanceledException) { cancellation = true; }
        Check(cancellation && cancelled.Position == 5, "Cancellation propagates and restores stream position.");
        using MemoryStream counterfeit = new(Iso("isom", false));
        Check(!ChatMediaSignatures.Matches(counterfeit, mp4! with { ContentType = "text/html" }), "Caller cannot substitute MIME via a forged format record.");
        using MemoryStream oversizedHeaders = new(Join(Enumerable.Repeat(Box("free", []), 33000).ToArray()));
        Check(!ChatMediaSignatures.Matches(oversizedHeaders, mp4!), "Header traversal is bounded independently of file size.");
        ChatAttachmentFormats.TryGetByFileName("sample.mov", out ChatAttachmentFormat? mov);
        using MemoryStream legacy = new(Iso("qt  ", false)[16..]);
        Check(ChatMediaSignatures.Matches(legacy, mov!), "Legacy QuickTime without ftyp still requires parsed movie and media data.");
    }

    private static async Task LinkedMedia()
    {
        Dictionary<string, string> types = ChatAttachmentFormats.All.Where(format => format.Kind is "audio" or "video")
            .DistinctBy(format => format.ContentType).ToDictionary(format => format.ContentType, format => format.Kind);
        foreach (var alias in new[] { "audio/x-wav", "audio/wave", "audio/vnd.wave", "audio/x-flac", "audio/x-aiff", "audio/x-m4a", "audio/x-m4b", "audio/x-aac" }) types[alias] = "audio";
        types["video/avi"] = types["video/msvideo"] = types["video/x-m4v"] = "video";
        foreach (var (mime, kind) in types)
        {
            using ChatLinkPreviewService service = new(new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent([1,2,3]) { Headers = { ContentType = new(mime) } } }), (_, _) => Task.CompletedTask);
            Check((await service.BuildAsync("https://public.example/sample", None)).Single().Kind == kind, "Linked preview recognizes MIME: " + mime);
            ChatLinkedMediaRead opened = await service.OpenMediaAsync("https://public.example/sample", null, None);
            await using (opened.Stream) Check(opened.ContentType == mime && opened.Length == 3, "Linked proxy retains recognized MIME: " + mime);
        }
        foreach (string mime in new[] { "text/html", "application/javascript", "application/octet-stream", "audio/unknown", "video/unknown" })
        {
            Check(ChatAttachmentFormats.MediaKindForContentType(mime) is null, "Unknown or active MIME remains excluded: " + mime);
            using ChatLinkPreviewService service = new(new FakeHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent([1]) { Headers = { ContentType = new(mime) } } }), (_, _) => Task.CompletedTask);
            bool rejected = false;
            try { ChatLinkedMediaRead opened = await service.OpenMediaAsync("https://public.example/sample", null, None); await opened.Stream.DisposeAsync(); }
            catch (ChatOperationException error) when (error.Code == "chat-link-media-unavailable") { rejected = true; }
            Check(rejected, "Linked proxy rejects unknown MIME: " + mime);
        }
    }

    private static byte[] Fixture(ChatAttachmentFormat format) => format.Container switch
    {
        "iso" => Iso(format.Extension switch { ".mov" or ".qt" => "qt  ", ".3gp" => "3gp6", ".3g2" => "3g2a", ".m4a" => "M4A ", ".m4b" => "M4B ", _ => "isom" }, format.Kind == "audio"),
        "ebml" => Ebml(format.Extension is ".webm" or ".weba" ? "webm" : "matroska", format.Kind == "audio"),
        "ogg" => Ogg(format.Extension switch { ".opus" => "opus", ".spx" => "speex", ".ogv" => "theora", _ => "vorbis" }),
        "wave" => Wave(), "avi" => Avi(), "aiff" => Aiff(format.Extension == ".aifc"),
        "flac" => Flac(), "mpeg-audio" => MpegAudio(format.Extension == ".mp2"), "adts" => Adts(),
        "mpeg-video" => [0,0,1,0xb3,2,0,32,0x13,0,0,0,0,0,0,1,0,0,0,0,0,0,0,0,0],
        "mpeg-ts" => Transport(format.Extension == ".m2ts" ? 192 : 188), "flv" => Flv(),
        "asf" => Asf(format.Kind == "audio"), _ => throw new InvalidOperationException(format.Extension)
    };

    private static byte[] Iso(string brand, bool audio) => Join(Box("ftyp", Join(Text(brand), new byte[4])),
        Box("moov", Box("trak", Box("mdia", Box("hdlr", Join(new byte[8], Text(audio ? "soun" : "vide"), new byte[12]))))), Box("mdat", new byte[8]));
    private static byte[] Box(string name, byte[] payload) => Join(U32((uint)payload.Length + 8, false), Text(name), payload);
    private static byte[] Ebml(string docType, bool audio) => Join(Element(0x1a45dfa3, Element(0x4282, Text(docType))),
        Element(0x18538067, Element(0x1654ae6b, Element(0xae, Element(0x83, [audio ? (byte)2 : (byte)1]), Element(0x86, Text(audio ? "A_OPUS" : "V_VP8")))),
            Element(0x1f43b675, Element(0xe7, [0]), Element(0xa3, [0x81,0,0,0x80,1]))));
    private static byte[] Element(uint id, params byte[][] payloads)
    {
        byte[] payload = Join(payloads), identifier = U32(id, false).SkipWhile(value => value == 0).ToArray();
        byte[] length = payload.Length < 127 ? [(byte)(0x80 | payload.Length)] : [(byte)(0x40 | payload.Length >> 8), (byte)payload.Length];
        return Join(identifier, length, payload);
    }
    private static byte[] Ogg(string codec)
    {
        byte[] packet;
        if (codec == "opus") { packet = new byte[19]; Text("OpusHead").CopyTo(packet, 0); packet[8] = 1; packet[9] = 2; }
        else if (codec == "speex") { packet = new byte[80]; Text("Speex   ").CopyTo(packet, 0); U32(80).CopyTo(packet, 32); U32(16000).CopyTo(packet, 36); }
        else if (codec == "theora") { packet = new byte[42]; packet[0] = 0x80; Text("theora").CopyTo(packet, 1); }
        else { packet = new byte[30]; packet[0] = 1; Text("vorbis").CopyTo(packet, 1); packet[11] = 2; U32(44100).CopyTo(packet, 12); packet[28] = 0x88; packet[29] = 1; }
        byte[] header = new byte[28]; Text("OggS").CopyTo(header, 0); header[5] = 2; header[26] = 1; header[27] = (byte)packet.Length;
        byte[] page = Join(header, packet); uint crc = 0;
        foreach (byte value in page) { crc ^= (uint)value << 24; for (int bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1; }
        U32(crc).CopyTo(page, 22); return page;
    }
    private static byte[] Riff(string kind, params byte[][] chunks)
    { byte[] payload = Join(Text(kind), Join(chunks)); return Join(Text("RIFF"), U32((uint)payload.Length), payload); }
    private static byte[] Chunk(string name, byte[] payload, bool little = true)
        => Join(Text(name), U32((uint)payload.Length, little), payload, payload.Length % 2 == 0 ? [] : [0]);
    private static byte[] Wave()
    {
        byte[] format = new byte[16]; format[0] = 1; format[2] = 2; U32(44100).CopyTo(format, 4); U32(176400).CopyTo(format, 8); format[12] = 4; format[14] = 16;
        return Riff("WAVE", Chunk("fmt ", format), Chunk("data", new byte[4]));
    }
    private static byte[] Rf64(bool table = false)
    {
        byte[] wave = Wave(), sizes = new byte[table ? 40 : 28];
        BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(8), 4);
        byte[] extra = [];
        if (table)
        {
            U32(1).CopyTo(sizes, 24); Text("JUNK").CopyTo(sizes, 28); BinaryPrimitives.WriteUInt64LittleEndian(sizes.AsSpan(32), 2);
            extra = Join(Text("JUNK"), U32(uint.MaxValue), [1,2]);
        }
        byte[] bytes = Join(Text("RF64"), U32(uint.MaxValue), Text("WAVE"), Chunk("ds64", sizes), wave[12..36], extra,
            Text("data"), U32(uint.MaxValue), new byte[4]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), (ulong)bytes.Length - 8);
        return bytes;
    }
    private static byte[] Avi()
    {
        byte[] main = new byte[56]; U32(1).CopyTo(main, 24); U32(32).CopyTo(main, 32); U32(32).CopyTo(main, 36);
        byte[] stream = new byte[56]; Text("vids").CopyTo(stream, 0);
        return Riff("AVI ", Chunk("LIST", Join(Text("hdrl"), Chunk("avih", main), Chunk("LIST", Join(Text("strl"), Chunk("strh", stream))))),
            Chunk("LIST", Join(Text("movi"), Chunk("00db", new byte[8]))));
    }
    private static byte[] Aiff(bool compressed)
    {
        byte[] common = new byte[compressed ? 22 : 18]; common[1] = 2; U32(1, false).CopyTo(common, 2); common[7] = 16; common[8] = 0x40; common[9] = 0x0e; common[10] = 0xac; common[11] = 0x44;
        if (compressed) Text("NONE").CopyTo(common, 18);
        byte[] payload = Join(Text(compressed ? "AIFC" : "AIFF"), Chunk("COMM", common, false), Chunk("SSND", new byte[12], false));
        return Join(Text("FORM"), U32((uint)payload.Length, false), payload);
    }
    private static byte[] Flac()
    {
        byte[] bytes = new byte[46]; Text("fLaC").CopyTo(bytes, 0); bytes[4] = 0x80; bytes[7] = 34; bytes[8] = bytes[10] = 0x10;
        ulong info = 44100UL << 44 | 1UL << 41 | 15UL << 36 | 1UL; BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(18), info);
        new byte[] {255,248,0x19,0x18}.CopyTo(bytes, 42); return bytes;
    }
    private static byte[] MpegAudio(bool layer2)
    {
        byte[] frame = new byte[417]; frame[0] = 255; frame[1] = layer2 ? (byte)0xfd : (byte)0xfb; frame[2] = layer2 ? (byte)0x80 : (byte)0x90;
        return Join(frame, frame);
    }
    private static byte[] Adts() => Join([255,241,0x50,0x80,1,0x7f,0xfc,0,0,0,0], [255,241,0x50,0x80,1,0x7f,0xfc,0,0,0,0]);
    private static byte[] Transport(int size)
    { byte[] bytes = new byte[size * 5]; for (int i = 0; i < 5; i++) { int start = i * size + (size == 192 ? 4 : 0); bytes[start] = 0x47; bytes[start + 3] = 0x10; } return bytes; }
    private static byte[] Flv() => Join(Text("FLV"), [1,5], U32(9, false), new byte[4], [9,0,0,1,0,0,0,0,0,0,0], [0x17], U32(12, false));
    private static byte[] Asf(bool audio)
    {
        byte[] properties = AsfObject("8CABDCA1-A947-11CF-8EE4-00C00C205365", new byte[80]);
        byte[] stream = new byte[72]; new Guid(audio ? "F8699E40-5B4D-11CF-A8FD-00805F5C442B" : "BC19EFC0-5B4D-11CF-A8FD-00805F5C442B").ToByteArray().CopyTo(stream, 0); U32(18).CopyTo(stream, 40);
        byte[] header = AsfObject("75B22630-668E-11CF-A6D9-00AA0062CE6C", Join(U32(2), [1,2], properties, AsfObject("B7DC0791-A9B7-11CF-8EE6-00C00C205365", stream)));
        return Join(header, AsfObject("75B22636-668E-11CF-A6D9-00AA0062CE6C", new byte[28]));
    }
    private static byte[] AsfObject(string guid, byte[] payload)
    { byte[] size = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(size, (ulong)payload.Length + 24); return Join(new Guid(guid).ToByteArray(), size, payload); }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);
    private static byte[] U32(uint value, bool little = true)
    { byte[] bytes = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); else BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
    private static byte[] Join(params byte[][] pieces) => pieces.SelectMany(piece => piece).ToArray();
    private static async Task<ChatUploadDto> Upload(ChatAttachmentStorage storage, string name, byte[] bytes)
    {
        ChatUploadDto upload = await storage.BeginAsync(1, new(name, "text/html", bytes.Length), None);
        await storage.AppendAsync(1, upload.Id, 0, new MemoryStream(bytes), bytes.Length, None);
        return await storage.CompleteAsync(1, upload.Id, None);
    }
    private static async Task Reject(ChatAttachmentStorage storage, string name, byte[] bytes)
    {
        bool rejected = false;
        try { await Upload(storage, name, bytes); }
        catch (ChatOperationException error) when (error.Code == "chat-file-content-mismatch") { rejected = true; }
        Check(rejected, "Server rejects mismatched container: " + name);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; }
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request)); }
}
