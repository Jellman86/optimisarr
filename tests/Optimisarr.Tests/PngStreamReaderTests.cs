using System.Buffers.Binary;
using System.Text;
using Optimisarr.Core.Queue;
using Optimisarr.Core.SuperResolution;

namespace Optimisarr.Tests;

/// <summary>
/// The super-resolution decoder writes frames as one continuous PNG stream. Splitting it must
/// return each picture whole, report its size, stop cleanly at the end, and refuse a stream that
/// is cut off or is not PNG rather than hand a partial picture to the engine.
/// </summary>
public sealed class PngStreamReaderTests
{
    internal static byte[] Png(int width, int height, int payloadBytes = 16)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        Chunk(stream, "IHDR", header);
        Chunk(stream, "IDAT", new byte[payloadBytes]);
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(new byte[4]);
    }

    [Fact]
    public async Task Each_picture_in_a_continuous_stream_is_returned_whole_and_in_order()
    {
        var first = Png(64, 36);
        var second = Png(64, 36, payloadBytes: 40);
        using var stream = new MemoryStream([.. first, .. second]);

        Assert.Equal(first, await PngStreamReader.ReadNextAsync(stream, CancellationToken.None));
        Assert.Equal(second, await PngStreamReader.ReadNextAsync(stream, CancellationToken.None));
        Assert.Null(await PngStreamReader.ReadNextAsync(stream, CancellationToken.None));
    }

    [Fact]
    public void The_picture_size_comes_from_its_header()
    {
        Assert.Equal(new PictureSize(1920, 1080), PngStreamReader.Dimensions(Png(1920, 1080)));
    }

    [Fact]
    public async Task A_stream_cut_off_mid_picture_is_refused()
    {
        var picture = Png(8, 8);
        using var stream = new MemoryStream(picture[..^6]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PngStreamReader.ReadNextAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task Bytes_that_are_not_png_are_refused()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("not a png at all"));
        await Assert.ThrowsAsync<InvalidDataException>(() => PngStreamReader.ReadNextAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task An_implausibly_large_chunk_is_refused_before_it_is_buffered()
    {
        var picture = Png(8, 8);
        BinaryPrimitives.WriteInt32BigEndian(picture.AsSpan(8 + 4 + 4 + 13 + 4), int.MaxValue);
        using var stream = new MemoryStream(picture);
        await Assert.ThrowsAsync<InvalidDataException>(() => PngStreamReader.ReadNextAsync(stream, CancellationToken.None));
    }
}
