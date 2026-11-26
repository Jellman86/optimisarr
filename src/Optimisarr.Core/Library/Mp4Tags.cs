using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Optimisarr.Core.Library;

/// <summary>
/// One item of an MP4 file's iTunes tag list (<c>moov/udta/meta/ilst</c>): its atom, or for a
/// freeform item <c>----:mean:name</c>, and each of its values as its data type and the SHA-256 of
/// its bytes. Two items are the same when those match; <see cref="Atom"/> is the whole item as
/// written, so it can be copied into another file exactly.
/// </summary>
public sealed record Mp4TagItem(string Key, IReadOnlyList<string> Values, ReadOnlyMemory<byte> Atom)
{
    public bool Equals(Mp4TagItem? other) =>
        other is not null && Key == other.Key && Values.SequenceEqual(other.Values, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Key, Values.Count);
}

public enum Mp4TagLayout
{
    /// <summary>The file has no tag list.</summary>
    None,

    /// <summary>A tag list under an <c>mdir</c> handler: the atoms Apple Music, TagLib and mutagen read.</summary>
    Itunes,

    /// <summary>
    /// QuickTime key/value metadata under an <c>mdta</c> handler, as FFmpeg writes with
    /// <c>-movflags use_metadata_tags</c>. FFmpeg reads it back, but most music software does not.
    /// </summary>
    QuickTimeKeys,
}

/// <param name="Items">The iTunes items in file order; empty for any other layout.</param>
/// <param name="MoovOffset">Where the movie box, which holds the tags, starts in the file.</param>
/// <param name="MoovIsLast">Nothing follows the movie box, so it can be written again in place.</param>
public sealed record Mp4Tags(Mp4TagLayout Layout, IReadOnlyList<Mp4TagItem> Items, long MoovOffset, bool MoovIsLast);

/// <summary>An MP4 file's tags, or why they cannot be read: unreadable tags cannot be proved kept.</summary>
public sealed record Mp4TagRead(Mp4Tags? Tags, string? Problem)
{
    public static Mp4TagRead Read(Mp4Tags tags) => new(tags, null);
    public static Mp4TagRead Unreadable(string problem) => new(null, problem);
}

/// <summary>
/// Reads the tags of an MP4-family file from its boxes, asking only for the bytes it needs. Like
/// <see cref="MusicTagReader"/> it never guesses: a box it does not understand completely makes the
/// tags unreadable.
/// </summary>
public static class Mp4TagReader
{
    // Far more boxes than any real file has at its top level, so a corrupt file cannot keep it reading.
    private const int MaximumTopLevelBoxes = 10_000;

    public static bool IsMp4Family(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".m4a" or ".m4b";

    public static Mp4TagRead Read(IMusicTagBytes file)
    {
        try
        {
            return ReadBoxes(file);
        }
        // Whatever goes wrong inside a box, it is a box this reader cannot follow: a caller must see
        // unreadable tags, never an error. Only a cancelled read is the caller's own.
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Mp4TagRead.Unreadable(error is Mp4BoxException ? error.Message : $"a box it cannot follow ({error.GetType().Name})");
        }
    }

    private static Mp4TagRead ReadBoxes(IMusicTagBytes file)
    {
        long? moovOffset = null;
        var moovLength = 0L;
        var position = 0L;
        for (var count = 0; position < file.Length; count++)
        {
            if (count == MaximumTopLevelBoxes) return Mp4TagRead.Unreadable("too many boxes");
            var header = file.Read(position, 16);
            if (header.Length < 8) return Mp4TagRead.Unreadable("a box header cut short by the end of the file");
            var type = Encoding.Latin1.GetString(header, 4, 4);
            if (count == 0 && type != "ftyp") return Mp4TagRead.Unreadable("it does not start as an MP4 file");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (size == 1)
            {
                if (header.Length < 16) return Mp4TagRead.Unreadable("a box header cut short by the end of the file");
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (large > long.MaxValue) return Mp4TagRead.Unreadable("a box too large to read");
                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = file.Length - position;
            }
            if (size < headerLength) return Mp4TagRead.Unreadable($"a {Printable(type)} box with an invalid size");
            if (size > file.Length - position) return Mp4TagRead.Unreadable($"a {Printable(type)} box cut short by the end of the file");
            if (type == "moov")
            {
                if (moovOffset is not null) return Mp4TagRead.Unreadable("more than one movie box");
                moovOffset = position;
                moovLength = size;
            }
            position += size;
        }

        if (moovOffset is not { } offset) return Mp4TagRead.Unreadable("no movie box");
        if (moovLength > MusicTagReader.MaximumReadBytes) return Mp4TagRead.Unreadable("a movie box too large to read");
        var moov = file.Read(offset, (int)moovLength);
        if (moov.Length != moovLength) return Mp4TagRead.Unreadable("a movie box cut short by the end of the file");
        var (layout, items) = ReadMoov(moov);
        return Mp4TagRead.Read(new Mp4Tags(layout, items, offset, offset + moovLength == file.Length));
    }

    private static (Mp4TagLayout Layout, IReadOnlyList<Mp4TagItem> Items) ReadMoov(byte[] moov)
    {
        var quickTimeKeys = false;
        List<Mp4TagItem>? items = null;
        foreach (var child in Mp4Box.Children(moov, Mp4Box.Whole(moov, "moov")))
        {
            // FFmpeg's use_metadata_tags puts the meta box directly in the movie box; iTunes puts it in udta.
            if (child.Type == "meta") quickTimeKeys |= ReadMeta(moov, child, ref items);
            if (child.Type != "udta") continue;
            foreach (var meta in Mp4Box.Children(moov, child.Body).Where(box => box.Type == "meta"))
                quickTimeKeys |= ReadMeta(moov, meta, ref items);
        }
        if (quickTimeKeys) return (Mp4TagLayout.QuickTimeKeys, []);
        return items is null ? (Mp4TagLayout.None, []) : (Mp4TagLayout.Itunes, items);
    }

    /// <summary>Reads one meta box's iTunes items into <paramref name="items"/>; true when it holds QuickTime keys instead.</summary>
    private static bool ReadMeta(byte[] moov, Mp4Box meta, ref List<Mp4TagItem>? items)
    {
        var children = Mp4Box.Children(moov, Mp4Box.MetaChildren(moov, meta)).ToList();
        var handler = children.Where(box => box.Type == "hdlr").Select(box => Mp4Box.Handler(moov, box)).ToList();
        if (handler is ["mdta"] || children.Any(box => box.Type == "keys")) return true;
        if (handler is not ["mdir"]) throw new Mp4BoxException($"a meta box with a {(handler.Count == 1 ? Printable(handler[0]) : "missing")} handler");
        var lists = children.Where(box => box.Type == "ilst").ToList();
        if (lists.Count > 1 || items is not null && lists.Count == 1) throw new Mp4BoxException("more than one iTunes tag list");
        if (lists.Count == 0) return false;
        items = [.. Mp4Box.Children(moov, lists[0].Body).Select(item => ReadItem(moov, item))];
        return false;
    }

    private static Mp4TagItem ReadItem(byte[] moov, Mp4Box item)
    {
        var parts = Mp4Box.Children(moov, item.Body).ToList();
        var key = item.Type;
        if (item.Type == "----")
        {
            if (parts is not [{ Type: "mean" } mean, { Type: "name" } name, ..])
                throw new Mp4BoxException("a freeform item without its mean and name");
            key = $"----:{FullBoxText(moov, mean)}:{FullBoxText(moov, name)}";
            parts = parts[2..];
        }
        if (parts.Count == 0 || parts.Any(part => part.Type != "data")) throw new Mp4BoxException($"a {Printable(key)} item it cannot read");
        var values = parts.Select(part =>
        {
            var body = moov.AsSpan(part.Body);
            if (body.Length < 8) throw new Mp4BoxException($"a {Printable(key)} item it cannot read");
            var type = BinaryPrimitives.ReadUInt32BigEndian(body) & 0xFFFFFF;
            return string.Create(CultureInfo.InvariantCulture, $"{type}:{MusicTagReader.Sha256(body[8..])}");
        }).ToList();
        return new Mp4TagItem(key, values, moov.AsMemory(item.Start, item.Length));
    }

    private static string FullBoxText(byte[] moov, Mp4Box box)
    {
        var body = moov.AsSpan(box.Body);
        if (body.Length < 4) throw new Mp4BoxException("a freeform item it cannot read");
        var text = MusicTagReader.StrictUtf8.GetString(body[4..]);
        // The key holds the mean and the name apart with the separator, so neither may contain it.
        return text.Contains(':', StringComparison.Ordinal) ? throw new Mp4BoxException("a freeform item it cannot read") : text;
    }

    internal static string Printable(string type) => type.All(c => c is >= ' ' and <= '~' or '©') ? type : "binary";
}

/// <summary>
/// Writes iTunes items, and puts a new item list into an MP4 file's movie box. Only the tag list
/// and the sizes of the boxes holding it change; every other byte stays where it was, so the
/// sample offsets the movie box records still point at the same audio.
/// </summary>
public static class Mp4TagWriter
{
    /// <summary>A text item, such as <c>©nam</c> or <c>soaa</c>, in UTF-8.</summary>
    public static byte[] Text(string atom, string value) => Box(atom, Data(1, Encoding.UTF8.GetBytes(value)));

    /// <summary>A freeform <c>----</c> item named by its mean (who defined it) and name, holding UTF-8 text.</summary>
    public static byte[] Freeform(string mean, string name, string value) =>
        Box("----", Box("mean", [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(mean)]), Box("name", [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(name)]),
            Data(1, Encoding.UTF8.GetBytes(value)));

    /// <summary>A track (<c>trkn</c>) or disc (<c>disk</c>) number with its total, laid out as FFmpeg and iTunes write them.</summary>
    public static byte[] NumberPair(string atom, int number, int total)
    {
        byte[] pair = [0, 0, (byte)(number >> 8), (byte)number, (byte)(total >> 8), (byte)total];
        return Box(atom, Data(0, atom == "trkn" ? [.. pair, 0, 0] : pair));
    }

    /// <summary>
    /// The movie box with its one iTunes tag list holding <paramref name="items"/> in place of what it
    /// held; null, with the reason, when <paramref name="moov"/> is not one whole movie box with
    /// exactly one iTunes tag list.
    /// </summary>
    public static byte[]? ReplaceItems(ReadOnlySpan<byte> moov, IEnumerable<ReadOnlyMemory<byte>> items, out string? problem)
    {
        problem = null;
        var bytes = moov.ToArray();
        try
        {
            if (bytes.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(bytes) != bytes.Length)
                throw new Mp4BoxException("it is not one whole movie box");
            var path = FindItemList(bytes);
            var ilst = path[^1];
            var list = items.SelectMany(item => item.ToArray()).ToArray();
            var growth = list.Length - (ilst.Length - 8);
            var result = new byte[bytes.Length + growth];
            bytes.AsSpan(0, ilst.Start + 8).CopyTo(result);
            list.CopyTo(result, ilst.Start + 8);
            bytes.AsSpan(ilst.Start + ilst.Length).CopyTo(result.AsSpan(ilst.Start + 8 + list.Length));
            foreach (var box in path)
            {
                var size = (long)box.Length + growth;
                if (size > uint.MaxValue) throw new Mp4BoxException("a tag list too large to write");
                BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(box.Start), (uint)size);
            }
            return result;
        }
        catch (Exception error) when (error is Mp4BoxException or ArgumentOutOfRangeException or DecoderFallbackException)
        {
            problem = error is Mp4BoxException ? error.Message : "a box it cannot follow";
            return null;
        }
    }

    /// <summary>The boxes from the movie box down to its one iTunes tag list: moov, udta, meta, ilst.</summary>
    private static List<Mp4Box> FindItemList(byte[] moov)
    {
        var root = new Mp4Box("moov", 0, moov.Length, Mp4Box.Whole(moov, "moov"));
        var found = new List<List<Mp4Box>>();
        foreach (var child in Mp4Box.Children(moov, root.Body))
        {
            if (child.Type == "meta" && Handlers(moov, child).Contains("mdta"))
                throw new Mp4BoxException("its tags are QuickTime keys, not iTunes items");
            if (child.Type != "udta") continue;
            foreach (var meta in Mp4Box.Children(moov, child.Body).Where(box => box.Type == "meta"))
            {
                if (!Handlers(moov, meta).SequenceEqual(["mdir"])) throw new Mp4BoxException("its tags are not iTunes items");
                found.AddRange(Mp4Box.Children(moov, Mp4Box.MetaChildren(moov, meta)).Where(box => box.Type == "ilst")
                    .Select(ilst => new List<Mp4Box> { root, child, meta, ilst }));
            }
        }
        if (found.Count != 1) throw new Mp4BoxException(found.Count == 0 ? "it has no iTunes tag list" : "it has more than one iTunes tag list");
        // A 64-bit size is never needed for tags, and would be in a different place.
        if (found[0].Any(box => box.HeaderLength != 8)) throw new Mp4BoxException("a box around its tags has a 64-bit size");
        return found[0];
    }

    private static List<string> Handlers(byte[] moov, Mp4Box meta) =>
        [.. Mp4Box.Children(moov, Mp4Box.MetaChildren(moov, meta)).Where(box => box.Type == "hdlr").Select(box => Mp4Box.Handler(moov, box))];

    private static byte[] Data(int type, byte[] payload) =>
        Box("data", [0, (byte)(type >> 16), (byte)(type >> 8), (byte)type, 0, 0, 0, 0, .. payload]);

    private static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(child => child).ToArray();
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.Latin1.GetBytes(type).CopyTo(box, 4);
        body.CopyTo(box, 8);
        return box;
    }
}

internal sealed class Mp4BoxException(string message) : Exception(message);

/// <summary>A box inside a movie box held in memory: where it starts, how long it is, and where its contents lie.</summary>
internal readonly record struct Mp4Box(string Type, int Start, int Length, Range Body, int HeaderLength = 8)
{
    /// <summary>The contents of the box that is the whole of <paramref name="bytes"/>.</summary>
    public static Range Whole(byte[] bytes, string type)
    {
        if (bytes.Length < 8 || Encoding.Latin1.GetString(bytes, 4, 4) != type) throw new Mp4BoxException($"no {type} box");
        return 8..bytes.Length;
    }

    public static IEnumerable<Mp4Box> Children(byte[] bytes, Range within)
    {
        var (start, length) = within.GetOffsetAndLength(bytes.Length);
        var end = start + length;
        var position = start;
        var boxes = new List<Mp4Box>();
        while (position < end)
        {
            if (end - position < 8) throw new Mp4BoxException("a box cut short inside the movie box");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position));
            var type = Encoding.Latin1.GetString(bytes, position + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (end - position < 16) throw new Mp4BoxException("a box cut short inside the movie box");
                var large = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(position + 8));
                size = large > int.MaxValue ? -1 : (long)large;
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }
            if (size < header || size > end - position)
                throw new Mp4BoxException($"a {Mp4TagReader.Printable(type)} box that overruns the box holding it");
            boxes.Add(new Mp4Box(type, position, (int)size, (position + header)..(position + (int)size), header));
            position += (int)size;
        }
        return boxes;
    }

    /// <summary>
    /// A meta box's children. ISO meta boxes carry a version and flags first; Apple's QuickTime meta
    /// boxes do not, and start straight with their handler.
    /// </summary>
    public static Range MetaChildren(byte[] bytes, Mp4Box meta)
    {
        var (start, length) = meta.Body.GetOffsetAndLength(bytes.Length);
        if (length >= 8 && bytes.AsSpan(start + 4, 4).SequenceEqual("hdlr"u8)) return meta.Body;
        if (length < 4) throw new Mp4BoxException("a meta box cut short");
        return (start + 4)..(start + length);
    }

    public static string Handler(byte[] bytes, Mp4Box hdlr)
    {
        var (start, length) = hdlr.Body.GetOffsetAndLength(bytes.Length);
        if (length < 12) throw new Mp4BoxException("a handler box cut short");
        return Encoding.Latin1.GetString(bytes, start + 8, 4);
    }
}
