namespace Optimisarr.Core.IO;

/// <summary>
/// Reads a stream only while it remains within a caller-provided byte limit. Oversized content is
/// rejected after a one-byte probe beyond the limit, preventing chunked or dishonest responses from
/// being fully buffered before their size is known.
/// </summary>
public static class BoundedStreamReader
{
    public static async Task<byte[]?> ReadAsync(
        Stream source,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        var bufferLength = maxBytes < 81_920 ? maxBytes + 1 : 81_920;
        var buffer = new byte[bufferLength];
        using var output = new MemoryStream(Math.Min(maxBytes, 81_920));
        long total = 0;

        while (true)
        {
            var probeRemaining = (long)maxBytes - total + 1;
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, probeRemaining)),
                cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
}
