using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;

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

    /// <summary>An MP4-family file's tag atoms, read natively; unreadable rather than an exception on any failure.</summary>
    public virtual async Task<Mp4TagRead> ReadMp4Async(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return Mp4TagReader.Read(new FileBytes(handle, cancellationToken));
            }, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Mp4TagRead.Unreadable($"the file could not be read: {error.Message}");
        }
    }

    /// <summary>
    /// Writes Optimisarr's marker, and the source's tags FFmpeg had no iTunes atom for, into the tag
    /// list of an M4A FFmpeg has just written (see <see cref="Mp4TagCarryOver"/>). Only the new file
    /// changes. Null when done, or why not; verification refuses the file if a tag is then missing.
    /// </summary>
    /// <param name="sourceTags">The source's tags as ffprobe reports them.</param>
    /// <param name="outputTags">The new file's tags as ffprobe reports them, before this adds any.</param>
    public virtual async Task<string?> CarryOverMp4TagsAsync(
        string sourcePath,
        string outputPath,
        IReadOnlyDictionary<string, string>? sourceTags,
        IReadOnlyDictionary<string, string>? outputTags,
        string marker,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var handle = File.OpenHandle(outputPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var file = new FileBytes(handle, cancellationToken);
                var read = Mp4TagReader.Read(file);
                if (read.Tags is not { } output) return $"its tags could not be read: {read.Problem}";
                // The sample offsets in the movie box point before it, so it can grow in place only at the end.
                if (!output.MoovIsLast) return "its movie box is not at the end of the file";
                Mp4Tags? source = null;
                if (Mp4TagReader.IsMp4Family(sourcePath))
                {
                    using var sourceHandle = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    source = Mp4TagReader.Read(new FileBytes(sourceHandle, cancellationToken)).Tags;
                }

                var items = Mp4TagCarryOver.Plan(output, source, sourceTags, outputTags, marker);
                var moov = file.Read(output.MoovOffset, (int)(file.Length - output.MoovOffset));
                var rewritten = Mp4TagWriter.ReplaceItems(moov, items, out var problem);
                if (rewritten is null) return problem;
                RandomAccess.Write(handle, rewritten, output.MoovOffset);
                RandomAccess.SetLength(handle, output.MoovOffset + rewritten.Length);
                return null;
            }, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"the file could not be written: {error.Message}";
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
