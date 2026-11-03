using System.Buffers.Binary;
using Optimisarr.Core.Queue;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// Splits FFmpeg's <c>image2pipe</c> PNG output into whole pictures. Reading one picture at a time
/// is what bounds super-resolution scratch space: while a batch waits for the engine, nothing is
/// read and the decoder simply blocks on its full pipe. A stream that ends mid-picture or is not
/// PNG is refused, so a partial frame can never reach the engine or the encoder.
/// </summary>
public static class PngStreamReader
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Larger than any chunk FFmpeg writes for a picture we accept (an 8K RGB frame is
    /// ~100 MB raw); a bigger length means the stream is not what we asked for.</summary>
    private const int MaximumChunkBytes = 256 * 1024 * 1024;

    /// <summary>The next complete picture, or null when the stream ended cleanly between pictures.</summary>
    public static async Task<byte[]?> ReadNextAsync(Stream stream, CancellationToken cancellationToken)
    {
        var signature = new byte[Signature.Length];
        var read = await ReadAtMostAsync(stream, signature, cancellationToken);
        if (read == 0)
        {
            return null;
        }
        if (read < signature.Length || !signature.AsSpan().SequenceEqual(Signature))
        {
            throw new InvalidDataException("The decoder's picture stream is not PNG.");
        }

        using var picture = new MemoryStream();
        picture.Write(signature);
        var header = new byte[8];
        while (true)
        {
            await ReadExactlyAsync(stream, header, cancellationToken);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length < 0 || length > MaximumChunkBytes)
            {
                throw new InvalidDataException("The decoder's picture stream has an implausible chunk length.");
            }
            var body = new byte[length + 4];
            await ReadExactlyAsync(stream, body, cancellationToken);
            picture.Write(header);
            picture.Write(body);
            if (header.AsSpan(4, 4).SequenceEqual("IEND"u8))
            {
                return picture.ToArray();
            }
        }
    }

    /// <summary>The picture size from a PNG's IHDR chunk.</summary>
    public static PictureSize Dimensions(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new InvalidDataException("The picture has no PNG header.");
        }
        return new PictureSize(BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4)));
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        if (await ReadAtMostAsync(stream, buffer, cancellationToken) < buffer.Length)
        {
            throw new InvalidDataException("The decoder's picture stream ended in the middle of a picture.");
        }
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
