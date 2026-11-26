using System.Buffers.Binary;
using System.Text;

namespace Optimisarr.Tests;

/// <summary>MP4 files built box by box, laid out as FFmpeg's muxer writes them, so no FFmpeg is needed.</summary>
internal static class Mp4TagFixtures
{
    public static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(child => child).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }

    public static byte[] FullBox(string type, params byte[][] children) => Box(type, [[0, 0, 0, 0], .. children]);

    public static byte[] DataBox(int type, byte[] payload) =>
        Box("data", [0, (byte)(type >> 16), (byte)(type >> 8), (byte)type], [0, 0, 0, 0], payload);

    public static byte[] Text(string atom, string value) => Box(atom, DataBox(1, Encoding.UTF8.GetBytes(value)));

    public static byte[] Freeform(string mean, string name, string value) =>
        Box("----", FullBox("mean", Encoding.UTF8.GetBytes(mean)), FullBox("name", Encoding.UTF8.GetBytes(name)),
            DataBox(1, Encoding.UTF8.GetBytes(value)));

    public static byte[] Handler(string handler) =>
        FullBox("hdlr", [0, 0, 0, 0], Encoding.Latin1.GetBytes(handler), new byte[12], [0]);

    public static byte[] ItunesMeta(params byte[][] items) => FullBox("meta", Handler("mdir"), Box("ilst", items));

    /// <summary>FFmpeg's <c>use_metadata_tags</c> layout: a key table, and items named by their index in it.</summary>
    public static byte[] QuickTimeKeysMeta(params (string Key, string Value)[] tags) =>
        FullBox("meta", Handler("mdta"),
            FullBox("keys", [[0, 0, 0, (byte)tags.Length], .. tags.Select(tag => Box("mdta", Encoding.UTF8.GetBytes(tag.Key)))]),
            Box("ilst", [.. tags.Select((tag, index) => Box(Encoding.Latin1.GetString([0, 0, 0, (byte)(index + 1)]),
                DataBox(1, Encoding.UTF8.GetBytes(tag.Value))))]));

    public static readonly byte[] Ftyp = Box("ftyp", "M4A "u8.ToArray(), [0, 0, 2, 0], "M4A isomiso2"u8.ToArray());

    public static readonly byte[] Mdat = Box("mdat", [.. Enumerable.Range(0, 64).Select(value => (byte)value)]);

    public static readonly byte[] Track = Box("trak", Box("tkhd", new byte[84]), Box("mdia", Box("mdhd", new byte[24])));

    public static byte[] Moov(params byte[][] children) => Box("moov", [Box("mvhd", new byte[100]), Track, .. children]);

    /// <summary>The order FFmpeg writes an M4A in without faststart: the sample data, then the index and tags.</summary>
    public static byte[] M4a(params byte[][] items) => [.. Ftyp, .. Box("free"), .. Mdat, .. Moov(Box("udta", ItunesMeta(items)))];

    public static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
}
