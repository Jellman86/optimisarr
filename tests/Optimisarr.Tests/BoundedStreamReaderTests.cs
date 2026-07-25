using Optimisarr.Core.IO;

namespace Optimisarr.Tests;

public sealed class BoundedStreamReaderTests
{
    [Fact]
    public async Task Returns_content_that_fits_the_limit()
    {
        await using var stream = new MemoryStream([1, 2, 3, 4]);

        var bytes = await BoundedStreamReader.ReadAsync(stream, 4, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], bytes);
    }

    [Fact]
    public async Task Rejects_oversized_chunked_content_without_reading_beyond_the_limit_probe()
    {
        await using var stream = new CountingChunkedStream(length: 1_000, chunkSize: 3);

        var bytes = await BoundedStreamReader.ReadAsync(stream, 10, CancellationToken.None);

        Assert.Null(bytes);
        Assert.Equal(11, stream.BytesRead);
    }

    private sealed class CountingChunkedStream(int length, int chunkSize) : Stream
    {
        private int _position;
        public int BytesRead => _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(buffer.Length, chunkSize), length - _position);
            if (count <= 0)
            {
                return ValueTask.FromResult(0);
            }

            buffer.Span[..count].Fill(0x2A);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
