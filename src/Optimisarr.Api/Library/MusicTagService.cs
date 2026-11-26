using Optimisarr.Core.Library;

namespace Optimisarr.Api.Library;

/// <summary>
/// Reads a music file's tags from its bytes for <see cref="MusicTagReader"/>, which decides what they
/// say. Every failure to read is an unreadable result, never an exception, so a caller that needs
/// the tags proved refuses rather than proceeds.
/// </summary>
public class MusicTagService
{
    public virtual async Task<MusicTagRead> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // The reads are a few small, positioned ones, so they run off the caller's thread.
            return await Task.Run(() =>
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return MusicTagReader.Read(Path.GetExtension(path), new FileBytes(handle, cancellationToken));
            }, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return MusicTagRead.Unreadable($"the file could not be read: {error.Message}");
        }
    }

    private sealed class FileBytes(Microsoft.Win32.SafeHandles.SafeFileHandle handle, CancellationToken cancellationToken) : IMusicTagBytes
    {
        public long Length { get; } = RandomAccess.GetLength(handle);

        public byte[] Read(long offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < 0 || offset >= Length || count <= 0) return [];
            var buffer = new byte[(int)Math.Min(count, Length - offset)];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(filled), offset + filled);
                if (read == 0) break;
                filled += read;
            }
            return filled == buffer.Length ? buffer : buffer[..filled];
        }
    }
}
