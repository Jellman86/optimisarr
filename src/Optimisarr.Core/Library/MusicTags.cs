using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Optimisarr.Core.Library;

/// <summary>
/// One entry in a music file's tags: what it is, and what it says. Text is decoded, so the same
/// words written in another text encoding compare equal; anything binary is the SHA-256 of its bytes.
/// An ID3 frame is one entry each; a Vorbis comment field is one entry holding every value it was
/// given, in order.
/// </summary>
public sealed record MusicTag(string Key, IReadOnlyList<string> Values)
{
    public bool Equals(MusicTag? other) =>
        other is not null && Key == other.Key && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key, Values.Count);
}

public enum MusicTagLayout
{
    /// <summary>An MP3's ID3v2 tag at its start, and an ID3v1 tag at its end.</summary>
    Id3,

    /// <summary>A FLAC file's metadata blocks.</summary>
    Flac,

    /// <summary>The comment header of an Ogg Opus stream.</summary>
    OggOpus,

    /// <summary>A WAV file's chunks, with the LIST INFO entries as its tags.</summary>
    Wav,
}

/// <param name="Id3Version">2.3 or 2.4 as 3 or 4; null when an MP3 has no ID3v2 tag, and for other layouts.</param>
/// <param name="HasId3v1">An MP3 ends with an ID3v1 tag.</param>
public sealed record MusicTagSet(MusicTagLayout Layout, IReadOnlyList<MusicTag> Tags, int? Id3Version = null, bool HasId3v1 = false);

/// <summary>The tags read from a file, or why they cannot be: unreadable tags cannot be proved kept.</summary>
public sealed record MusicTagRead(MusicTagSet? Tags, string? Problem)
{
    public static MusicTagRead Read(MusicTagSet tags) => new(tags, null);
    public static MusicTagRead Unreadable(string problem) => new(null, problem);
}

/// <summary>The bytes of one file, read where the tag reader asks; fewer than asked for at its end.</summary>
public interface IMusicTagBytes
{
    long Length { get; }

    byte[] Read(long offset, int count);
}

/// <summary>A file already in memory, as tests and small files have it.</summary>
public sealed class MusicTagBytes(byte[] bytes) : IMusicTagBytes
{
    public long Length => bytes.LongLength;

    public byte[] Read(long offset, int count) =>
        offset >= bytes.LongLength ? [] : bytes.AsSpan((int)offset, (int)Math.Min(count, bytes.LongLength - offset)).ToArray();
}

/// <summary>
/// Reads the tags of the music formats that can be trimmed without converting, asking only for the
/// bytes it needs. It never guesses: a tag it does not understand completely is unreadable.
/// </summary>
public static class MusicTagReader
{
    /// <summary>No single piece of a tag is read past this size, so a corrupt length cannot exhaust memory.</summary>
    public const int MaximumReadBytes = 64 * 1024 * 1024;

    private sealed class Truncated(string what) : Exception(what);

    public static MusicTagRead Read(string extension, IMusicTagBytes file)
    {
        try
        {
            return extension.ToLowerInvariant() switch
            {
                ".mp3" => ReadMp3(file),
                ".flac" => ReadFlac(file),
                ".opus" or ".ogg" => ReadOggOpus(file),
                ".wav" => ReadWav(file),
                var other => MusicTagRead.Unreadable($"{other} files' tags are not read"),
            };
        }
        catch (Truncated truncated)
        {
            return MusicTagRead.Unreadable(truncated.Message);
        }
    }

    private static byte[] Exactly(IMusicTagBytes file, long offset, long count, string what)
    {
        if (count < 0 || count > MaximumReadBytes) throw new Truncated($"{what} too large to read");
        var bytes = file.Read(offset, (int)count);
        if (bytes.Length != count) throw new Truncated($"{what} cut short by the end of the file");
        return bytes;
    }

    private static MusicTagRead ReadMp3(IMusicTagBytes file)
    {
        var tags = new List<MusicTag>();
        int? version = null;
        var start = file.Read(0, 10);
        if (start.Length >= 3 && start.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            if (start.Length < 10) throw new Truncated("an ID3v2 header");
            if (!Id3v2.TrySyncsafe(start.AsSpan(6, 4), out var size)) return MusicTagRead.Unreadable("an ID3v2 tag with an invalid size");
            var tag = Exactly(file, 0, 10 + size, "an ID3v2 tag");
            if (Id3v2.Read(tag, tags, out var id3Version) is { } problem) return MusicTagRead.Unreadable(problem);
            version = id3Version;
        }

        // ID3v1 is the last 128 bytes; an APE tag ends with its footer, and Lyrics3 with its marker,
        // just before it or at the very end.
        var tailLength = (int)Math.Min(256, file.Length);
        var tail = Exactly(file, file.Length - tailLength, tailLength, "the end of the file").AsSpan();
        var hasId3v1 = tail.Length >= 128 && tail[^128..^125].SequenceEqual("TAG"u8);
        var beforeId3v1 = hasId3v1 ? tail[..^128] : tail;
        if (beforeId3v1.Length >= 32 && beforeId3v1[^32..^24].SequenceEqual("APETAGEX"u8))
            return MusicTagRead.Unreadable("an APE tag");
        if (beforeId3v1.Length >= 9 && (beforeId3v1[^9..].SequenceEqual("LYRICS200"u8) || beforeId3v1[^9..].SequenceEqual("LYRICSEND"u8)))
            return MusicTagRead.Unreadable("a Lyrics3 tag");
        if (beforeId3v1.Length >= 10 && beforeId3v1[^10..^7].SequenceEqual("3DI"u8))
            return MusicTagRead.Unreadable("an ID3v2 tag at the end of the file");
        if (hasId3v1) Id3v1.Read(tail[^128..], tags);
        return MusicTagRead.Read(new MusicTagSet(MusicTagLayout.Id3, tags, version, hasId3v1));
    }

    private static MusicTagRead ReadFlac(IMusicTagBytes file)
    {
        var magic = file.Read(0, 4);
        if (magic.AsSpan().StartsWith("ID3"u8)) return MusicTagRead.Unreadable("an ID3 tag before the FLAC stream");
        if (!magic.AsSpan().SequenceEqual("fLaC"u8)) return MusicTagRead.Unreadable("it does not start as a FLAC stream");

        var tags = new List<MusicTag>();
        var pictures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var position = 4L;
        while (true)
        {
            var header = Exactly(file, position, 4, "a FLAC metadata block");
            var type = header[0] & 0x7F;
            var size = (header[1] << 16) | (header[2] << 8) | header[3];
            switch (type)
            {
                // Stream facts, padding and the seek table are written again for the new length.
                case 0 or 1 or 3:
                    break;
                case 4:
                    if (!VorbisComment.TryRead(Exactly(file, position + 4, size, "a comment block"), tags, out var problem))
                        return MusicTagRead.Unreadable(problem);
                    break;
                case 6:
                    if (!FlacPicture.TryRead(Exactly(file, position + 4, size, "a picture block"), out var key, out var digest))
                        return MusicTagRead.Unreadable("a picture block it cannot read");
                    (pictures.TryGetValue(key, out var list) ? list : pictures[key] = []).Add(digest);
                    break;
                case 127:
                    return MusicTagRead.Unreadable("an invalid metadata block");
                default:
                {
                    var body = Exactly(file, position + 4, size, "a metadata block");
                    tags.Add(type switch
                    {
                        2 when size >= 4 => new($"APPLICATION:{Convert.ToHexString(body, 0, 4)}", [Sha256(body)]),
                        5 => new("CUESHEET", [Sha256(body)]),
                        _ => new($"BLOCK:{type.ToString(CultureInfo.InvariantCulture)}", [Sha256(body)]),
                    });
                    break;
                }
            }
            position += 4 + size;
            if ((header[0] & 0x80) != 0) break;
        }
        tags.AddRange(pictures.Select(picture => new MusicTag(picture.Key, picture.Value)));
        return MusicTagRead.Read(new MusicTagSet(MusicTagLayout.Flac, tags));
    }

    private static MusicTagRead ReadOggOpus(IMusicTagBytes file)
    {
        var packets = new List<byte[]>();
        var packet = new List<byte>();
        uint? serial = null;
        var position = 0L;
        while (packets.Count < 2)
        {
            var page = Exactly(file, position, 27, "an Ogg page");
            if (!page.AsSpan(0, 4).SequenceEqual("OggS"u8) || page[4] != 0) return MusicTagRead.Unreadable("it does not start as an Ogg stream");
            var pageSerial = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(14, 4));
            serial ??= pageSerial;
            // One logical stream only: a multiplexed file is not a plain Opus file.
            if (pageSerial != serial) return MusicTagRead.Unreadable("more than one Ogg stream");
            var table = Exactly(file, position + 27, page[26], "an Ogg page");
            var body = Exactly(file, position + 27 + table.Length, table.Sum(lacing => lacing), "an Ogg page");
            var offset = 0;
            foreach (var lacing in table)
            {
                packet.AddRange(body.AsSpan(offset, lacing));
                offset += lacing;
                if (packet.Count > MaximumReadBytes) return MusicTagRead.Unreadable("a comment header too large to read");
                if (lacing < 255)
                {
                    packets.Add([.. packet]);
                    packet.Clear();
                    if (packets.Count == 2) break;
                }
            }
            position += 27 + table.Length + body.Length;
        }

        if (!packets[0].AsSpan().StartsWith("OpusHead"u8)) return MusicTagRead.Unreadable("it is not an Opus stream");
        var comments = packets[1].AsSpan();
        if (!comments.StartsWith("OpusTags"u8)) return MusicTagRead.Unreadable("no Opus comment header");
        var tags = new List<MusicTag>();
        if (!VorbisComment.TryRead(comments[8..], tags, out var commentProblem, out var used)) return MusicTagRead.Unreadable(commentProblem);
        // Anything after the comments is binary data that only counts when its first bit is set.
        var rest = comments[(8 + used)..];
        if (rest.Length > 0 && (rest[0] & 1) == 1) tags.Add(new("OPUSTAGS:BINARY", [Sha256(rest)]));
        return MusicTagRead.Read(new MusicTagSet(MusicTagLayout.OggOpus, tags));
    }

    /// <summary>
    /// A RIFF WAVE file's chunks. The format, the samples and their count are the audio, and padding
    /// is nothing; the LIST INFO entries are its tags; every other chunk (bext, iXML, an id3 chunk, cue
    /// points, sampler loops) is kept whole as a digest, so its loss is seen.
    /// </summary>
    private static MusicTagRead ReadWav(IMusicTagBytes file)
    {
        var header = Exactly(file, 0, 12, "a WAV header");
        if (!header.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !header.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return MusicTagRead.Unreadable("it is not a plain RIFF WAVE file");
        var tags = new List<MusicTag>();
        var position = 12L;
        while (position + 8 <= file.Length)
        {
            var chunk = Exactly(file, position, 8, "a WAV chunk");
            var id = Encoding.ASCII.GetString(chunk, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            if (position + 8 + size > file.Length) return MusicTagRead.Unreadable($"a truncated {id.Trim()} chunk");
            switch (id)
            {
                case "fmt " or "data" or "fact" or "JUNK" or "PAD " or "junk":
                    break;
                case "LIST" when size >= 4:
                {
                    var list = Exactly(file, position + 8, size, "a LIST chunk");
                    var kind = Encoding.ASCII.GetString(list, 0, 4);
                    if (kind != "INFO")
                    {
                        tags.Add(new($"LIST:{kind}", [Sha256(list)]));
                        break;
                    }
                    var entry = 4;
                    while (entry + 8 <= list.Length)
                    {
                        var entryId = Encoding.ASCII.GetString(list, entry, 4);
                        var entrySize = (int)BinaryPrimitives.ReadUInt32LittleEndian(list.AsSpan(entry + 4));
                        if (entrySize < 0 || entry + 8 + entrySize > list.Length) return MusicTagRead.Unreadable("a truncated INFO entry");
                        // The bytes as they are: Latin-1 maps each byte to one character.
                        tags.Add(new($"INFO:{entryId}", [Encoding.Latin1.GetString(list.AsSpan(entry + 8, entrySize).TrimEnd((byte)0))]));
                        entry += 8 + entrySize + (entrySize & 1);
                    }
                    break;
                }
                default:
                    tags.Add(new($"CHUNK:{id}", [Sha256(Exactly(file, position + 8, size, $"a {id.Trim()} chunk"))]));
                    break;
            }
            position += 8 + size + (size & 1);
        }
        return MusicTagRead.Read(new MusicTagSet(MusicTagLayout.Wav, tags));
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static class VorbisComment
    {
        public static bool TryRead(ReadOnlySpan<byte> body, List<MusicTag> tags, out string problem) =>
            TryRead(body, tags, out problem, out _);

        public static bool TryRead(ReadOnlySpan<byte> body, List<MusicTag> tags, out string problem, out int used)
        {
            problem = "a comment block it cannot read";
            used = 0;
            if (body.Length < 8) return false;
            var vendorLength = BinaryPrimitives.ReadUInt32LittleEndian(body);
            if (vendorLength > body.Length - 8) return false;
            var position = 4 + (int)vendorLength;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(body[position..]);
            position += 4;
            // Field names compare without case, as Vorbis says; each field keeps its values in order.
            var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (var index = 0u; index < count; index++)
            {
                if (body.Length - position < 4) return false;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(body[position..]);
                if (length > body.Length - position - 4) return false;
                string comment;
                try
                {
                    comment = StrictUtf8.GetString(body.Slice(position + 4, (int)length));
                }
                catch (DecoderFallbackException)
                {
                    problem = "a comment that is not valid UTF-8";
                    return false;
                }
                var equals = comment.IndexOf('=');
                if (equals <= 0)
                {
                    problem = "a comment without a field name";
                    return false;
                }
                var name = comment[..equals].ToUpperInvariant();
                (fields.TryGetValue(name, out var values) ? values : fields[name] = []).Add(comment[(equals + 1)..]);
                position += 4 + (int)length;
            }
            tags.AddRange(fields.Select(field => new MusicTag(field.Key, field.Value)));
            used = position;
            return true;
        }
    }

    private static class FlacPicture
    {
        // Type, MIME type and description identify a picture, and its data is what it shows; the
        // stated size and colour depth are worked out again by the writer.
        public static bool TryRead(ReadOnlySpan<byte> body, out string key, out string digest)
        {
            key = digest = "";
            try
            {
                var type = BinaryPrimitives.ReadUInt32BigEndian(body);
                var mimeLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                var descriptionStart = 8 + mimeLength;
                var descriptionLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body[descriptionStart..]);
                var description = StrictUtf8.GetString(body.Slice(descriptionStart + 4, descriptionLength));
                var dataStart = descriptionStart + 4 + descriptionLength + 16;
                var dataLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body[dataStart..]);
                if (dataLength < 0 || dataLength > body.Length - dataStart - 4) return false;
                key = string.Create(CultureInfo.InvariantCulture, $"PICTURE:{type}:{description}");
                digest = Sha256(body.Slice(dataStart + 4, dataLength));
                return true;
            }
            catch (Exception error) when (error is ArgumentOutOfRangeException or DecoderFallbackException)
            {
                return false;
            }
        }
    }

    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}

/// <summary>ID3v2.3 and 2.4 frames, decoded so that the same text in another encoding compares equal.</summary>
internal static class Id3v2
{
    private static string Fail(string problem) => problem;

    /// <summary>Adds the frames of a whole tag, header included, to <paramref name="tags"/>; null when it read them all, or why not.</summary>
    public static string? Read(byte[] head, List<MusicTag> tags, out int version)
    {
        version = head[3];
        if (version is not (3 or 4)) return Fail($"an ID3v2.{version} tag");
        var flags = head[5];
        TrySyncsafe(head.AsSpan(6, 4), out var size);
        // An experimental tag, or flags this reader does not know, is not guessed at.
        if ((flags & 0x20) != 0 || (version == 3 ? flags & 0x1F : flags & 0x0F) != 0) return Fail("ID3v2 tag flags it does not know");

        var body = head.AsSpan(10, (int)size).ToArray();
        // In 2.3 unsynchronisation covers the whole tag; in 2.4 the flag means every frame is.
        var tagUnsynchronised = (flags & 0x80) != 0;
        if (version == 3 && tagUnsynchronised) body = Resynchronise(body);
        var position = 0;
        if ((flags & 0x40) != 0)
        {
            if (body.Length < 4) return Fail("a truncated extended header");
            var extendedLength = version == 3
                ? (long)BinaryPrimitives.ReadUInt32BigEndian(body) + 4
                : TrySyncsafe(body.AsSpan(0, 4), out var syncsafe) ? syncsafe : -1;
            if (extendedLength < 6 || extendedLength > body.Length) return Fail("an invalid extended header");
            position = (int)extendedLength;
        }

        while (position + 10 <= body.Length && body[position] != 0)
        {
            var id = Encoding.ASCII.GetString(body, position, 4);
            if (!id.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')) return Fail("an ID3v2 frame it cannot read");
            var frameSize = version == 3
                ? (long)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(position + 4))
                : TrySyncsafe(body.AsSpan(position + 4, 4), out var syncsafe) ? syncsafe : -1;
            if (frameSize < 0 || frameSize > body.Length - position - 10) return Fail($"a truncated {id} frame");
            var format = body[position + 9];
            var payload = body.AsSpan(position + 10, (int)frameSize).ToArray();
            position += 10 + (int)frameSize;

            if (version == 3 && (format & 0xE0) != 0 || version == 4 && (format & 0x4C) != 0)
                return Fail($"a compressed, encrypted or grouped {id} frame");
            if (version == 4)
            {
                if ((format & 0x02) != 0 || tagUnsynchronised) payload = Resynchronise(payload);
                if ((format & 0x01) != 0)
                {
                    if (payload.Length < 4) return Fail($"a truncated {id} frame");
                    payload = payload[4..];
                }
            }

            var tag = Decode(id, payload);
            if (tag is null) return Fail($"a {id} frame it cannot read");
            tags.Add(tag);
        }

        // Whatever follows the last frame must be padding.
        for (; position < body.Length; position++)
            if (body[position] != 0) return Fail("data after the ID3v2 padding");
        return null;
    }

    private static MusicTag? Decode(string id, byte[] payload)
    {
        try
        {
            switch (id)
            {
                case "TXXX" or "WXXX":
                {
                    var description = SplitText(payload[0], payload.AsSpan(1), out var rest);
                    var values = id == "WXXX" ? [Encoding.Latin1.GetString(TrimNul(payload.AsSpan(1 + rest)))] : TextValues(payload[0], payload.AsSpan(1 + rest));
                    return new($"{id}:{description}", values);
                }
                case "COMM" or "USLT":
                {
                    var language = Encoding.Latin1.GetString(payload, 1, 3);
                    var description = SplitText(payload[0], payload.AsSpan(4), out var rest);
                    return new($"{id}:{language}:{description}", [string.Join('\0', TextValues(payload[0], payload.AsSpan(4 + rest)))]);
                }
                case "APIC":
                {
                    var mimeEnd = Array.IndexOf(payload, (byte)0, 1);
                    if (mimeEnd < 0 || mimeEnd + 2 > payload.Length) return null;
                    var pictureType = payload[mimeEnd + 1];
                    var description = SplitText(payload[0], payload.AsSpan(mimeEnd + 2), out var data);
                    return new(string.Create(CultureInfo.InvariantCulture, $"APIC:{pictureType}:{description}"),
                        [MusicTagReader.Sha256(payload.AsSpan(mimeEnd + 2 + data))]);
                }
                case ['T', ..]:
                    return new(id, TextValues(payload[0], payload.AsSpan(1)));
                case ['W', ..]:
                    return new(id, [Encoding.Latin1.GetString(TrimNul(payload))]);
                default:
                    return new(id, [MusicTagReader.Sha256(payload)]);
            }
        }
        catch (Exception error) when (error is IndexOutOfRangeException or ArgumentException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Text frames may hold several NUL-separated values; a writer may add a final NUL.</summary>
    private static List<string> TextValues(byte encoding, ReadOnlySpan<byte> bytes)
    {
        var values = DecodeText(encoding, bytes).Split('\0').Select(value => value.TrimStart('\uFEFF')).ToList();
        while (values.Count > 1 && values[^1].Length == 0) values.RemoveAt(values.Count - 1);
        return values;
    }

    /// <summary>A NUL-terminated string in the frame's encoding, and where the bytes after it start.</summary>
    private static string SplitText(byte encoding, ReadOnlySpan<byte> bytes, out int rest)
    {
        var wide = encoding is 1 or 2;
        var end = -1;
        for (var index = 0; index + (wide ? 1 : 0) < bytes.Length; index += wide ? 2 : 1)
        {
            if (bytes[index] == 0 && (!wide || bytes[index + 1] == 0))
            {
                end = index;
                break;
            }
        }
        if (end < 0) throw new ArgumentException("Unterminated ID3 string");
        rest = end + (wide ? 2 : 1);
        return DecodeText(encoding, bytes[..end]).TrimStart('\uFEFF');
    }

    private static string DecodeText(byte encoding, ReadOnlySpan<byte> bytes) => encoding switch
    {
        0 => Encoding.Latin1.GetString(bytes),
        1 => DecodeUtf16WithBom(bytes),
        2 => StrictUtf16(bigEndian: true).GetString(bytes),
        3 => MusicTagReader.StrictUtf8.GetString(bytes),
        _ => throw new ArgumentException("Unknown ID3 text encoding"),
    };

    private static string DecodeUtf16WithBom(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "";
        var bigEndian = bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF;
        var littleEndian = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
        if (!bigEndian && !littleEndian) throw new ArgumentException("UTF-16 without a byte order mark");
        return StrictUtf16(bigEndian).GetString(bytes[2..]);
    }

    private static UnicodeEncoding StrictUtf16(bool bigEndian) => new(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true);

    private static ReadOnlySpan<byte> TrimNul(ReadOnlySpan<byte> bytes) => bytes.TrimEnd((byte)0);

    private static ReadOnlySpan<byte> TrimNul(byte[] bytes) => bytes.AsSpan().TrimEnd((byte)0);

    private static byte[] Resynchronise(byte[] bytes)
    {
        var result = new List<byte>(bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            result.Add(bytes[index]);
            if (bytes[index] == 0xFF && index + 1 < bytes.Length && bytes[index + 1] == 0x00) index++;
        }
        return [.. result];
    }

    public static bool TrySyncsafe(ReadOnlySpan<byte> bytes, out long value)
    {
        value = 0;
        foreach (var b in bytes)
        {
            if ((b & 0x80) != 0) return false;
            value = (value << 7) | b;
        }
        return true;
    }
}

/// <summary>The fixed fields of an ID3v1 or ID3v1.1 tag.</summary>
internal static class Id3v1
{
    public static void Read(ReadOnlySpan<byte> tag, List<MusicTag> tags)
    {
        void Field(string name, ReadOnlySpan<byte> bytes)
        {
            var text = Encoding.Latin1.GetString(bytes.TrimEnd((byte)0)).TrimEnd(' ', '\0');
            if (text.Length > 0) tags.Add(new($"ID3V1:{name}", [text]));
        }

        Field("TITLE", tag.Slice(3, 30));
        Field("ARTIST", tag.Slice(33, 30));
        Field("ALBUM", tag.Slice(63, 30));
        Field("YEAR", tag.Slice(93, 4));
        // ID3v1.1 puts the track number in the comment's last byte, after a zero.
        var hasTrack = tag[125] == 0 && tag[126] != 0;
        Field("COMMENT", tag.Slice(97, hasTrack ? 28 : 30));
        if (hasTrack) tags.Add(new("ID3V1:TRACK", [tag[126].ToString(CultureInfo.InvariantCulture)]));
        tags.Add(new("ID3V1:GENRE", [tag[127].ToString(CultureInfo.InvariantCulture)]));
    }
}
