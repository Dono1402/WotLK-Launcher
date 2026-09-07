using System.Buffers.Binary;
using System.Text;

namespace WotLK.Launcher.Chat;

/// <summary>
/// Bounded container/header checks shared by local staging and upload completion.
/// This is not a codec decoder or malware scanner. Payload bytes are never run.
/// </summary>
public static class ChatMediaSignatures
{
    // Format references: mp4ra.org/registered-types/brands; Apple's QuickTime
    // File Format; matroska.org/technical/elements.html; RFC 9639 / RFC 7845;
    // xiph.org/ogg/doc/framing.html; Microsoft's RIFF AVI and ASF specifications.
    public static bool Matches(Stream stream, ChatAttachmentFormat format, CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead || !stream.CanSeek || stream.Length is <= 0 or > ChatLimits.MaximumAttachmentBytes
            || !ChatAttachmentFormats.TryGetByFileName("file" + format.Extension, out ChatAttachmentFormat? known)
            || known != format || format.Kind is not ("audio" or "video")) return false;
        long position = stream.Position;
        try
        {
            HeaderReader reader = new(stream, cancellationToken);
            return format.Container switch
            {
                "iso" => Iso(reader, format), "ebml" => Ebml(reader, format), "ogg" => Ogg(reader, format),
                "wave" => Riff(reader, false), "avi" => Riff(reader, true), "aiff" => Aiff(reader, format),
                "asf" => Asf(reader, format), "flac" => Flac(reader), "mpeg-audio" => MpegAudio(reader, format),
                "adts" => Adts(reader), "mpeg-video" => MpegVideo(reader, format),
                "mpeg-ts" => TransportStream(reader, format), "flv" => Flv(reader), _ => false
            };
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException) { return false; }
        finally { stream.Position = position; }
    }

    private static bool Iso(HeaderReader r, ChatAttachmentFormat format)
    {
        bool brand = false, ftyp = false, data = false, audio = false, video = false;
        string first = "";
        for (long offset = 0; offset < r.Length;)
        {
            Box box = ReadBox(r, offset, r.Length); if (offset == 0) first = box.Type;
            if (box.Type == "ftyp")
            {
                if (ftyp || box.Length is < 8 or > 4096 || box.Length % 4 != 0) return false;
                ftyp = true; byte[] bytes = r.Read(box.Start, (int)box.Length);
                for (int index = 0; index < bytes.Length; index += 4)
                {
                    if (index == 4) continue; // Minor version is not a compatibility brand.
                    string value = Encoding.ASCII.GetString(bytes, index, 4);
                    brand |= format.Extension switch
                    {
                        ".mov" or ".qt" => value == "qt  ",
                        ".3gp" => value.StartsWith("3gp", StringComparison.Ordinal) || value.StartsWith("3ge", StringComparison.Ordinal)
                            || value.StartsWith("3gg", StringComparison.Ordinal) || value.StartsWith("3gr", StringComparison.Ordinal),
                        ".3g2" => value.StartsWith("3g2", StringComparison.Ordinal),
                        ".m4a" or ".m4b" => value is "M4A " or "M4B " or "F4A " or "F4B " || IsoBrand(value),
                        _ => value is "M4V " or "M4VH" or "M4VP" or "MSNV" or "F4V " || IsoBrand(value)
                    };
                }
            }
            else if (box.Type == "moov") ReadMovie(r, box.Start, box.End, 0, ref audio, ref video);
            else if (box.Type == "mdat" && box.Length > 0) data = true;
            offset = box.End;
        }
        // Pre-ISO QuickTime movies may have no ftyp, but still need a parsed
        // movie hierarchy and actual media data. An arbitrary atom is insufficient.
        bool legacyQuickTime = !ftyp && format.Extension is ".mov" or ".qt" && first is "moov" or "mdat" or "wide";
        return (ftyp ? brand : legacyQuickTime) && data && (format.Kind == "audio" ? audio && !video : audio || video);
    }

    private static bool IsoBrand(string brand) => brand is "isom" or "mp41" or "mp42" or "avc1" or "dash" or "cmfc" or "cmfs"
        || brand.Length == 4 && brand.StartsWith("iso", StringComparison.Ordinal) && brand[3] is >= '2' and <= '9';

    private static void ReadMovie(HeaderReader r, long start, long end, int level, ref bool audio, ref bool video)
    {
        for (long offset = start; offset < end;)
        {
            Box box = ReadBox(r, offset, end);
            if (level == 0 && box.Type == "trak" || level == 1 && box.Type == "mdia")
                ReadMovie(r, box.Start, box.End, level + 1, ref audio, ref video);
            else if (level == 2 && box.Type == "hdlr" && box.Length >= 12)
            {
                string handler = Encoding.ASCII.GetString(r.Read(box.Start + 8, 4));
                audio |= handler == "soun"; video |= handler == "vide";
            }
            offset = box.End;
        }
    }

    private static Box ReadBox(HeaderReader r, long offset, long end)
    {
        byte[] header = r.ReadWithin(offset, 8, end); ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
        int length = 8;
        if (size == 1) { size = BinaryPrimitives.ReadUInt64BigEndian(r.ReadWithin(offset + 8, 8, end)); length = 16; }
        else if (size == 0) size = (ulong)(end - offset);
        if (size < (ulong)length || size > (ulong)(end - offset)) throw new InvalidDataException();
        return new(Encoding.ASCII.GetString(header, 4, 4), offset + length, offset + (long)size);
    }

    private static bool Ebml(HeaderReader r, ChatAttachmentFormat format)
    {
        EbmlElement header = ReadEbml(r, 0, r.Length);
        if (header.Id != 0x1a45dfa3 || header.Unknown || header.Length > 4096) return false;
        string? docType = null;
        for (long offset = header.Start; offset < header.End;)
        {
            EbmlElement field = ReadEbml(r, offset, header.End); if (field.Unknown) return false;
            if (field.Id == 0x4282)
            {
                if (docType is not null || field.Length is < 1 or > 16) return false;
                // EBML String values terminate at their first NUL (RFC 8794 §13).
                docType = Encoding.ASCII.GetString(r.Read(field.Start, (int)field.Length)).Split('\0', 2)[0];
            }
            offset = field.End;
        }
        if (docType != (format.Extension is ".webm" or ".weba" ? "webm" : "matroska")) return false;
        EbmlElement segment = ReadEbml(r, header.End, r.Length);
        // RFC 8794 permits a bounded root-level Void between EBML documents
        // and their body; it does not permit an arbitrary root CRC element.
        while (segment.Id == 0xec && !segment.Unknown) segment = ReadEbml(r, segment.End, r.Length);
        if (segment.Id != 0x18538067) return false;
        bool audio = false, video = false, cluster = false;
        for (long offset = segment.Start; offset < segment.End;)
        {
            EbmlElement element = ReadEbml(r, offset, segment.End);
            if (element.Id == 0x1654ae6b)
            {
                if (element.Unknown) return false;
                ReadTracks(r, element.Start, element.End, ref audio, ref video);
            }
            else if (element.Id == 0x1f43b675)
            {
                cluster |= element.Length > 0;
                // Streaming WebM commonly leaves the final Cluster size unknown.
                if (element.Unknown) break;
            }
            else if (element.Unknown) return false;
            offset = element.End;
        }
        return cluster && (format.Kind == "audio" ? audio && !video : audio || video);
    }

    private static void ReadTracks(HeaderReader r, long start, long end, ref bool audio, ref bool video)
    {
        for (long offset = start; offset < end;)
        {
            EbmlElement entry = ReadEbml(r, offset, end); if (entry.Unknown) throw new InvalidDataException();
            if (entry.Id == 0xae)
            {
                ulong? type = null; string? codec = null;
                for (long child = entry.Start; child < entry.End;)
                {
                    EbmlElement field = ReadEbml(r, child, entry.End); if (field.Unknown) throw new InvalidDataException();
                    if (field.Id == 0x83)
                    {
                        if (type is not null || field.Length is < 1 or > 8) throw new InvalidDataException();
                        type = 0; foreach (byte value in r.Read(field.Start, (int)field.Length)) type = (type << 8) | value;
                    }
                    else if (field.Id == 0x86)
                    {
                        if (codec is not null || field.Length is < 1 or > 128) throw new InvalidDataException();
                        codec = Encoding.ASCII.GetString(r.Read(field.Start, (int)field.Length));
                    }
                    child = field.End;
                }
                audio |= type == 2 && codec?.StartsWith("A_", StringComparison.Ordinal) == true;
                video |= type == 1 && codec?.StartsWith("V_", StringComparison.Ordinal) == true;
            }
            offset = entry.End;
        }
    }

    private static EbmlElement ReadEbml(HeaderReader r, long offset, long end)
    {
        long cursor = offset; ulong id = VariableInteger(r, ref cursor, end, true, out _);
        ulong length = VariableInteger(r, ref cursor, end, false, out bool unknown);
        if (!unknown && length > (ulong)(end - cursor)) throw new InvalidDataException();
        return new(id, cursor, unknown ? end : cursor + (long)length, unknown);
    }

    private static ulong VariableInteger(HeaderReader r, ref long offset, long end, bool identifier, out bool unknown)
    {
        byte first = r.ReadWithin(offset++, 1, end)[0]; int count = 1, marker = 0x80;
        while (marker != 0 && (first & marker) == 0) { marker >>= 1; count++; }
        if (marker == 0 || count > (identifier ? 4 : 8)) throw new InvalidDataException();
        ulong value = (ulong)(identifier ? first : first & (marker - 1));
        for (int index = 1; index < count; index++) value = (value << 8) | r.ReadWithin(offset++, 1, end)[0];
        unknown = !identifier && value == (1UL << (7 * count)) - 1;
        return value;
    }

    private static bool Ogg(HeaderReader r, ChatAttachmentFormat format)
    {
        bool audio = false, video = false, exact = false;
        for (long offset = 0, pages = 0; offset < r.Length && pages < 32; pages++)
        {
            byte[] header = r.Read(offset, 27);
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8) || header[4] != 0 || (header[5] & 0xf8) != 0) return false;
            if ((header[5] & 2) == 0) break; // All logical streams start with a BOS page.
            if ((header[5] & 1) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18)) != 0 || header[26] == 0) return false;
            byte[] lacing = r.Read(offset + 27, header[26]); int pageLength = lacing.Sum(value => (int)value);
            int packetLength = 0;
            foreach (byte length in lacing) { packetLength += length; if (length < 255) break; }
            if (packetLength == 0 || lacing[^1] == 255) return false;
            byte[] payload = r.Read(offset + 27 + lacing.Length, pageLength);
            ReadOnlySpan<byte> packet = payload.AsSpan(0, packetLength);
            bool vorbis = packet.Length >= 30 && packet.StartsWith(new byte[] { 1, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s' })
                && BinaryPrimitives.ReadUInt32LittleEndian(packet[7..]) == 0 && packet[11] > 0
                && BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]) > 0 && (packet[29] & 1) == 1;
            bool opus = packet.Length >= 19 && packet.StartsWith("OpusHead"u8) && packet[8] is > 0 and < 16 && packet[9] > 0
                && (packet[18] == 0 ? packet[9] <= 2 : packet.Length >= 21 + packet[9]);
            bool flac = packet.Length >= 51 && packet.StartsWith(new byte[] { 0x7f, (byte)'F', (byte)'L', (byte)'A', (byte)'C' })
                && packet[5] == 1 && packet.Slice(9, 4).SequenceEqual("fLaC"u8);
            bool speex = packet.Length >= 80 && packet.StartsWith("Speex   "u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(packet[32..]) >= 80 && BinaryPrimitives.ReadUInt32LittleEndian(packet[36..]) > 0;
            bool theora = packet.Length >= 42 && packet.StartsWith(new byte[] { 0x80, (byte)'t', (byte)'h', (byte)'e', (byte)'o', (byte)'r', (byte)'a' });
            bool skeleton = packet.Length >= 64 && packet.StartsWith("fishead\0"u8);
            if (!vorbis && !opus && !flac && !speex && !theora && !skeleton) return false;
            audio |= vorbis || opus || flac || speex; video |= theora;
            exact |= format.Extension == ".opus" && opus || format.Extension == ".spx" && speex;
            offset += 27 + lacing.Length + pageLength;
        }
        return format.Extension switch { ".opus" or ".spx" => exact && !video, ".ogv" => video, _ => audio && !video };
    }

    private static bool Riff(HeaderReader r, bool avi)
    {
        byte[] header = r.Read(0, 12);
        bool rf64 = !avi && header.AsSpan(0, 4).SequenceEqual("RF64"u8);
        bool little = rf64 || header.AsSpan(0, 4).SequenceEqual("RIFF"u8);
        if (!little && (avi || !header.AsSpan(0, 4).SequenceEqual("RIFX"u8))) return false;
        if (!header.AsSpan(8, 4).SequenceEqual(avi ? "AVI "u8 : "WAVE"u8)) return false;
        long end = 8L + Number32(header.AsSpan(4), little);
        Rf64Sizes? sizes = null;
        if (rf64)
        {
            // RF64 uses ds64 for its actual sizes even when a file is under 4 GiB.
            byte[] ds64 = r.Read(12, 36); uint chunkSize = Number32(ds64.AsSpan(4), true);
            if (Number32(header.AsSpan(4), true) != uint.MaxValue || !ds64.AsSpan(0, 4).SequenceEqual("ds64"u8)
                || chunkSize < 28 || chunkSize > r.Length - 20) return false;
            ulong riffSize = BinaryPrimitives.ReadUInt64LittleEndian(ds64.AsSpan(8));
            ulong dataSize = BinaryPrimitives.ReadUInt64LittleEndian(ds64.AsSpan(16));
            uint entries = Number32(ds64.AsSpan(32), true);
            if (riffSize > (ulong)(r.Length - 8) || riffSize < 40 || dataSize > (ulong)r.Length
                || entries > 4096 || 28UL + entries * 12UL > chunkSize) return false;
            end = 8 + (long)riffSize;
            if (20L + chunkSize > end) return false;
            sizes = new((long)dataSize);
            for (uint index = 0; index < entries; index++)
            {
                byte[] entry = r.ReadWithin(48L + index * 12L, 12, 20L + chunkSize);
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(4));
                if (length > (ulong)r.Length) return false;
                sizes.Chunks.Add((Encoding.ASCII.GetString(entry, 0, 4), (long)length));
            }
        }
        if (end > r.Length || end < 12) return false;
        bool format = false, data = false, stream = false;
        ReadRiffChunks(r, 12, end, little, avi, 0, ref format, ref data, ref stream, sizes);
        return format && data && (!avi || stream);
    }

    private static void ReadRiffChunks(HeaderReader r, long start, long end, bool little, bool avi, int level,
        ref bool format, ref bool data, ref bool stream, Rf64Sizes? sizes = null)
    {
        for (long offset = start; offset < end;)
        {
            byte[] h = r.ReadWithin(offset, 8, end); string name = Encoding.ASCII.GetString(h, 0, 4);
            long size = Number32(h.AsSpan(4), little); long payload = offset + 8;
            if (size == uint.MaxValue) size = sizes?.Take(name) ?? throw new InvalidDataException();
            if (size > end - payload) throw new InvalidDataException();
            if (!avi && name == "fmt " && size >= 16)
            {
                byte[] wave = r.Read(payload, 16);
                format = Number16(wave, little) != 0 && Number16(wave.AsSpan(2), little) is > 0 and <= 64
                    && Number32(wave.AsSpan(4), little) > 0 && Number16(wave.AsSpan(12), little) > 0;
            }
            else if (!avi && name == "data") data |= size > 0;
            else if (avi && name == "LIST" && size >= 4)
            {
                string list = Encoding.ASCII.GetString(r.Read(payload, 4));
                if (list == "movi") data |= size > 4;
                else if (level < 2 && (list == "hdrl" || list == "strl"))
                    ReadRiffChunks(r, payload + 4, payload + size, little, true, level + 1, ref format, ref data, ref stream);
            }
            else if (avi && name == "avih" && size >= 56)
            {
                byte[] main = r.Read(payload, 56);
                format |= Number32(main.AsSpan(24), true) > 0 && Number32(main.AsSpan(32), true) > 0 && Number32(main.AsSpan(36), true) > 0;
            }
            else if (avi && name == "strh" && size >= 56) stream |= r.Read(payload, 4).AsSpan().SequenceEqual("vids"u8);
            offset = payload + size + (size & 1);
            if (offset > end) throw new InvalidDataException();
        }
    }

    private static bool Aiff(HeaderReader r, ChatAttachmentFormat format)
    {
        byte[] header = r.Read(0, 12);
        if (!header.AsSpan(0, 4).SequenceEqual("FORM"u8)) return false;
        string type = Encoding.ASCII.GetString(header, 8, 4);
        if (type is not ("AIFF" or "AIFC") || format.Extension == ".aifc" && type != "AIFC") return false;
        long end = 8L + Number32(header.AsSpan(4), false); if (end > r.Length || end < 12) return false;
        bool common = false, sound = false;
        for (long offset = 12; offset < end;)
        {
            byte[] h = r.ReadWithin(offset, 8, end); string name = Encoding.ASCII.GetString(h, 0, 4);
            uint size = Number32(h.AsSpan(4), false); long payload = offset + 8; if (size > end - payload) return false;
            if (name == "COMM" && size >= (type == "AIFC" ? 22 : 18))
            {
                byte[] comm = r.Read(payload, 18);
                common = Number16(comm, false) is > 0 and <= 64 && Number32(comm.AsSpan(2), false) > 0
                    && Number16(comm.AsSpan(6), false) is > 0 and <= 64 && (Number16(comm.AsSpan(8), false) & 0x7fff) is > 0 and < 0x7fff;
            }
            else if (name == "SSND" && size > 8) sound = true;
            offset = payload + size + (size & 1); if (offset > end) return false;
        }
        return common && sound;
    }

    private static bool Flac(HeaderReader r)
    {
        byte[] info = r.Read(0, 42);
        if (!info.AsSpan(0, 4).SequenceEqual("fLaC"u8) || (info[4] & 0x7f) != 0 || info[5] != 0 || info[6] != 0 || info[7] != 34) return false;
        ushort minimum = Number16(info.AsSpan(8), false), maximum = Number16(info.AsSpan(10), false);
        uint rate = (uint)(info[18] << 12 | info[19] << 4 | info[20] >> 4);
        int bits = ((info[20] & 1) << 4 | info[21] >> 4) + 1;
        if (minimum < 16 || maximum < minimum || rate == 0 || bits is < 4 or > 32) return false;
        bool last = (info[4] & 0x80) != 0; long offset = 42;
        while (!last)
        {
            byte[] h = r.Read(offset, 4); int size = h[1] << 16 | h[2] << 8 | h[3];
            if ((h[0] & 0x7f) is 0 or 127 || size > r.Length - offset - 4) return false;
            last = (h[0] & 0x80) != 0; offset += 4L + size;
        }
        byte[] frame = r.Read(offset, 4);
        return frame[0] == 0xff && (frame[1] & 0xfe) == 0xf8 && (frame[2] >> 4) != 0
            && (frame[2] & 15) != 15 && (frame[3] & 1) == 0;
    }

    private static long AfterId3(HeaderReader r)
    {
        if (r.Length < 3 || !r.Read(0, 3).AsSpan().SequenceEqual("ID3"u8)) return 0;
        byte[] header = r.Read(0, 10);
        if (header[3] is < 2 or > 4 || header[4] == 255 || header.AsSpan(6, 4).ToArray().Any(value => value >= 128)) throw new InvalidDataException();
        int size = header[6] << 21 | header[7] << 14 | header[8] << 7 | header[9];
        long offset = 10L + size + (header[3] == 4 && (header[5] & 0x10) != 0 ? 10 : 0);
        if (offset >= r.Length) throw new InvalidDataException();
        return offset;
    }

    private static bool MpegAudio(HeaderReader r, ChatAttachmentFormat format)
    {
        long offset = AfterId3(r); int? signature = null;
        for (int frames = 0; frames < 2; frames++)
        {
            byte[] h = r.Read(offset, 4);
            int version = (h[1] >> 3) & 3, layer = (h[1] >> 1) & 3, bitrate = h[2] >> 4, rate = (h[2] >> 2) & 3;
            if (h[0] != 255 || (h[1] & 0xe0) != 0xe0 || version == 1 || layer != (format.Extension == ".mp2" ? 2 : 1)
                || bitrate is 0 or 15 || rate == 3 || (h[3] & 3) == 2) return false;
            int key = version << 4 | layer << 2 | rate; if (signature is int first && key != first) return false; signature = key;
            int[] bitrates = version == 3
                ? layer == 2 ? [0,32,48,56,64,80,96,112,128,160,192,224,256,320,384] : [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320]
                : [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160];
            int sampleRate = new[] { 44100, 48000, 32000 }[rate] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int length = (version == 3 || layer == 2 ? 144000 : 72000) * bitrates[bitrate] / sampleRate + ((h[2] >> 1) & 1);
            if (length <= 4 || length > r.Length - offset) return false;
            offset += length;
        }
        return true;
    }

    private static bool Adts(HeaderReader r)
    {
        long offset = AfterId3(r); int? rate = null;
        for (int frames = 0; frames < 2; frames++)
        {
            byte[] h = r.Read(offset, 7); int currentRate = (h[2] >> 2) & 15;
            int length = (h[3] & 3) << 11 | h[4] << 3 | h[5] >> 5;
            if (h[0] != 255 || (h[1] & 0xf6) != 0xf0 || currentRate >= 13 || rate is int first && currentRate != first
                || length <= ((h[1] & 1) != 0 ? 7 : 9) || length > r.Length - offset) return false;
            rate = currentRate; offset += length;
        }
        return true;
    }

    private static bool MpegVideo(HeaderReader r, ChatAttachmentFormat format)
    {
        byte[] bytes = r.Read(0, (int)Math.Min(r.Length, 65536)); ReadOnlySpan<byte> span = bytes;
        bool pack = span.StartsWith(new byte[] { 0, 0, 1, 0xba });
        bool sequence = span.StartsWith(new byte[] { 0, 0, 1, 0xb3 });
        if (format.Extension is ".m1v" or ".m2v" && !sequence || !pack && !sequence) return false;
        if (pack && (bytes.Length < 14 || (bytes[4] & 0xf0) != 0x20 && (bytes[4] & 0xc4) != 0x44)) return false;
        bool dimensions = false, payload = false;
        for (int index = 0; index + 8 < bytes.Length; index++)
        {
            if (bytes[index] != 0 || bytes[index + 1] != 0 || bytes[index + 2] != 1) continue;
            if (bytes[index + 3] == 0xb3)
                dimensions |= (bytes[index + 4] << 4 | bytes[index + 5] >> 4) > 0 && ((bytes[index + 5] & 15) << 8 | bytes[index + 6]) > 0
                    && (bytes[index + 7] & 15) is > 0 and <= 8;
            else if (bytes[index + 3] is 0x00 or >= 0x01 and <= 0xaf or >= 0xe0 and <= 0xef) payload = true;
        }
        return dimensions && payload;
    }

    private static bool TransportStream(HeaderReader r, ChatAttachmentFormat format)
    {
        int[] sizes = format.Extension == ".m2ts" ? [192] : [188,192,204];
        foreach (int size in sizes)
        {
            int lead = size == 192 ? 4 : 0; if (r.Length < size * 5) continue;
            bool valid = true;
            for (int index = 0; index < 5; index++)
            {
                byte[] h = r.Read((long)index * size + lead, 5);
                int adaptation = (h[3] >> 4) & 3;
                if (h[0] != 0x47 || (h[1] & 0x80) != 0 || adaptation == 0 || adaptation > 1 && h[4] > 183) { valid = false; break; }
            }
            if (valid) return true;
        }
        return false;
    }

    private static bool Flv(HeaderReader r)
    {
        byte[] h = r.Read(0, 9);
        if (!h.AsSpan(0, 3).SequenceEqual("FLV"u8) || h[3] != 1 || (h[4] & 0xfa) != 0 || (h[4] & 5) == 0) return false;
        uint offset = Number32(h.AsSpan(5), false); if (offset < 9 || offset > r.Length - 15) return false;
        if (Number32(r.Read(offset, 4), false) != 0) return false;
        long cursor = offset + 4;
        for (int count = 0; count < 32 && cursor < r.Length; count++)
        {
            byte[] tag = r.Read(cursor, 11); int kind = tag[0]; int size = tag[1] << 16 | tag[2] << 8 | tag[3];
            if (kind is not (8 or 9 or 18) || size <= 0 || size > r.Length - cursor - 15 || tag[8] != 0 || tag[9] != 0 || tag[10] != 0) return false;
            if (Number32(r.Read(cursor + 11 + size, 4), false) != size + 11) return false;
            if (kind is 8 or 9) return kind == 8 ? (h[4] & 4) != 0 : (h[4] & 1) != 0;
            cursor += size + 15L;
        }
        return false;
    }

    private static bool Asf(HeaderReader r, ChatAttachmentFormat format)
    {
        byte[] header = r.Read(0, 30);
        if (new Guid(header.AsSpan(0, 16)) != new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C") || header[28] != 1 || header[29] != 2) return false;
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16)); uint count = Number32(header.AsSpan(24), true);
        if (size is < 30 or > 4 * 1024 * 1024 || size > (ulong)r.Length || count is 0 or > 4096) return false;
        bool properties = false, audio = false, video = false; long offset = 30;
        for (int index = 0; index < count; index++)
        {
            byte[] h = r.ReadWithin(offset, 24, (long)size); Guid id = new(h.AsSpan(0, 16));
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(16)); if (length < 24 || length > size - (ulong)offset) return false;
            if (id == new Guid("8CABDCA1-A947-11CF-8EE4-00C00C205365")) properties |= length >= 104;
            else if (id == new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365"))
            {
                if (length < 78) return false; byte[] stream = r.Read(offset + 24, 54);
                uint typeBytes = Number32(stream.AsSpan(40), true), correction = Number32(stream.AsSpan(44), true);
                if (78UL + typeBytes + correction > length || typeBytes < 16) return false;
                Guid type = new(stream.AsSpan(0, 16));
                audio |= type == new Guid("F8699E40-5B4D-11CF-A8FD-00805F5C442B");
                video |= type == new Guid("BC19EFC0-5B4D-11CF-A8FD-00805F5C442B");
            }
            offset += (long)length;
        }
        if (offset != (long)size || !properties) return false;
        byte[] data = r.Read(offset, 50); ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(16));
        return new Guid(data.AsSpan(0,16)) == new Guid("75B22636-668E-11CF-A6D9-00AA0062CE6C") && dataLength > 50
            && dataLength <= (ulong)(r.Length - offset) && (format.Kind == "audio" ? audio && !video : format.Extension == ".wmv" ? video : audio || video);
    }

    private static uint Number32(ReadOnlySpan<byte> bytes, bool little) => little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
    private static ushort Number16(ReadOnlySpan<byte> bytes, bool little) => little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
    private readonly record struct Box(string Type, long Start, long End) { internal long Length => End - Start; }
    private readonly record struct EbmlElement(ulong Id, long Start, long End, bool Unknown) { internal long Length => End - Start; }

    private sealed class Rf64Sizes(long dataSize)
    {
        private bool _dataUsed;
        internal List<(string Name, long Size)> Chunks { get; } = [];
        internal long Take(string name)
        {
            if (name == "data" && !_dataUsed) { _dataUsed = true; return dataSize; }
            int index = Chunks.FindIndex(chunk => chunk.Name == name);
            if (index < 0) throw new InvalidDataException();
            long size = Chunks[index].Size; Chunks.RemoveAt(index); return size;
        }
    }

    private sealed class HeaderReader(Stream stream, CancellationToken cancellationToken)
    {
        private int _reads, _bytes;
        internal long Length => stream.Length;
        internal byte[] ReadWithin(long offset, int count, long end)
        {
            if (offset < 0 || count < 0 || end > Length || offset > end - count) throw new InvalidDataException();
            return Read(offset, count);
        }
        internal byte[] Read(long offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < 0 || count < 0 || offset > Length - count || ++_reads > 32768 || count > 4 * 1024 * 1024 - _bytes)
                throw new InvalidDataException();
            _bytes += count; stream.Position = offset; byte[] bytes = new byte[count]; stream.ReadExactly(bytes); return bytes;
        }
    }
}
