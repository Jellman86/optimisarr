using System.Buffers.Binary;
using System.Text;

namespace Optimisarr.Tests;

/// <summary>Builds the tag bytes of music files in memory, so the native tag reader is tested without FFmpeg.</summary>
internal static class MusicTagFixtures
{
    public static byte[] Syncsafe(int value) =>
        [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    /// <summary>A text frame: UTF-8 for 2.4, UTF-16 with a byte order mark for 2.3, as taggers write them.</summary>
    public static (string Id, byte[] Payload) Text(int version, string id, params string[] values) =>
        (id, version == 4
            ? [3, .. Encoding.UTF8.GetBytes(string.Join('\0', values))]
            : [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(string.Join('/', values))]);

    public static (string Id, byte[] Payload) Frame(string id, byte[] payload) => (id, payload);

    public static (string Id, byte[] Payload) Latin1Frame(string id, string content) => (id, Encoding.Latin1.GetBytes(content));

    public static byte[] Id3v2(int version, params (string Id, byte[] Payload)[] frames) => Id3v2(version, 0, 64, frames);

    public static byte[] Id3v2(int version, byte flags, int padding, params (string Id, byte[] Payload)[] frames)
    {
        var body = new List<byte>();
        foreach (var (id, payload) in frames)
        {
            body.AddRange(Encoding.ASCII.GetBytes(id));
            var size = new byte[4];
            if (version == 4) size = Syncsafe(payload.Length);
            else BinaryPrimitives.WriteInt32BigEndian(size, payload.Length);
            body.AddRange(size);
            body.AddRange([0, 0]);
            body.AddRange(payload);
        }
        body.AddRange(new byte[padding]);
        return [(byte)'I', (byte)'D', (byte)'3', (byte)version, 0, flags, .. Syncsafe(body.Count), .. body];
    }

    /// <summary>Stands in for the MPEG frames after the tag; the reader never looks at them.</summary>
    public static readonly byte[] AudioFrames = [0xFF, 0xFB, 0x90, 0x64, .. new byte[400]];

    public static byte[] Id3v1(string title, string artist, byte track, byte genre) =>
        Id3v1(Encoding.Latin1.GetBytes(title), Encoding.Latin1.GetBytes(artist), track, genre);

    /// <summary>An ID3v1.1 tag from raw field bytes, as a tagger writing a local code page leaves them.</summary>
    public static byte[] Id3v1(byte[] title, byte[] artist, byte track, byte genre)
    {
        var tag = new byte[128];
        "TAG"u8.CopyTo(tag);
        title.CopyTo(tag, 3);
        artist.CopyTo(tag, 33);
        tag[126] = track;
        tag[127] = genre;
        return tag;
    }

    public static byte[] VorbisComment(string vendor, params (string Field, string Value)[] comments)
    {
        var body = new List<byte>();
        void Add(byte[] bytes)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            body.AddRange(length);
            body.AddRange(bytes);
        }
        Add(Encoding.UTF8.GetBytes(vendor));
        var count = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, comments.Length);
        body.AddRange(count);
        foreach (var (field, value) in comments) Add(Encoding.UTF8.GetBytes($"{field}={value}"));
        return [.. body];
    }

    public static byte[] FlacBlock(int type, byte[] body, bool last = false) =>
        [(byte)(type | (last ? 0x80 : 0)), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];

    public static byte[] Flac(params byte[][] blocks) => [.. "fLaC"u8.ToArray(), .. blocks.SelectMany(block => block), 0xFF, 0xF8, .. new byte[64]];

    public static byte[] FlacPicture(int type, string description, byte[] data, string mime = "image/jpeg")
    {
        var body = new List<byte>();
        void Int(int value)
        {
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            body.AddRange(bytes);
        }
        Int(type);
        Int(mime.Length);
        body.AddRange(Encoding.ASCII.GetBytes(mime));
        var text = Encoding.UTF8.GetBytes(description);
        Int(text.Length);
        body.AddRange(text);
        Int(64); Int(64); Int(24); Int(0);
        Int(data.Length);
        body.AddRange(data);
        return [.. body];
    }

    /// <summary>One Ogg page holding whole packets of one stream, laced as the format says.</summary>
    public static byte[] OggPage(uint serial, uint sequence, params byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (var packet in packets)
        {
            var left = packet.Length;
            while (left >= 255) { lacing.Add(255); left -= 255; }
            lacing.Add((byte)left);
        }
        var header = new byte[27];
        "OggS"u8.CopyTo(header);
        header[5] = (byte)(sequence == 0 ? 2 : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), sequence);
        header[26] = (byte)lacing.Count;
        return [.. header, .. lacing, .. packets.SelectMany(packet => packet)];
    }

    public static byte[] OpusHead => [.. "OpusHead"u8.ToArray(), 1, 2, 0x38, 0x01, 0x80, 0xBB, 0, 0, 0, 0, 0];

    public static byte[] OpusTags(params (string Field, string Value)[] comments) => [.. "OpusTags"u8.ToArray(), .. VorbisComment("libopus", comments)];

    public static byte[] RiffChunk(string id, byte[] body) =>
        [.. Encoding.ASCII.GetBytes(id), .. BitConverter.GetBytes(body.Length), .. body, .. body.Length % 2 == 1 ? new byte[1] : []];

    public static byte[] InfoList(params (string Id, string Value)[] entries) =>
        RiffChunk("LIST", [.. "INFO"u8.ToArray(), .. entries.SelectMany(entry => RiffChunk(entry.Id, [.. Encoding.UTF8.GetBytes(entry.Value), 0]))]);

    public static byte[] Wav(params byte[][] chunks)
    {
        byte[] body = [.. "WAVE"u8.ToArray(), .. RiffChunk("fmt ", new byte[16]), .. chunks.SelectMany(chunk => chunk)];
        return [.. "RIFF"u8.ToArray(), .. BitConverter.GetBytes(body.Length), .. body];
    }
}
