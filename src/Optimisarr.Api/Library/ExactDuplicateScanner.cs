using System.Security.Cryptography;
using Optimisarr.Core.IO;
using Optimisarr.Core.Library;

namespace Optimisarr.Api.Library;

public sealed record ExactDuplicateInput(int Id, string Path, string RelativePath, long SizeBytes, DateTimeOffset ModifiedAt);
public sealed record ExactDuplicateProgress(int Checked, int Skipped, int Total, long BytesRead);
public sealed record ExactDuplicateResult(int Checked, int Skipped, int Total, long BytesRead,
    IReadOnlyList<ExactDuplicateGroup> Groups, bool Truncated);

/// <summary>Explicit, read-only scan. One 128 KiB block every 16 ms caps reads near 8 MiB/s.
/// Reports are snapshots. No replacement, deletion, cache reuse or hash-only cleanup is supported.</summary>
public sealed class ExactDuplicateScanner(TimeSpan? pause = null)
{
    private readonly TimeSpan _pause = pause ?? TimeSpan.FromMilliseconds(16);
    public async Task<ExactDuplicateResult> ScanAsync(string root, IReadOnlyList<ExactDuplicateInput> inputs,
        Action<ExactDuplicateProgress> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var candidates = inputs.Where(f => f.SizeBytes > 0).DistinctBy(f => f.Path,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .GroupBy(f => f.SizeBytes).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
        var observed = new List<ExactDuplicateFile>();
        var checkedCount = 0; var skipped = 0; long bytes = 0;
        var buffer = new byte[128 * 1024];
        foreach (var input in candidates)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!SafePath(root, input.Path) || !Matches(input)) { skipped++; continue; }
                await using var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != input.SizeBytes) { skipped++; continue; }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, count)); length += count; bytes += count;
                    progress(new(checkedCount, skipped, candidates.Length, bytes));
                    if (_pause > TimeSpan.Zero) await Task.Delay(_pause, token);
                    if (length > input.SizeBytes) break;
                }
                if (length != input.SizeBytes || !SafePath(root, input.Path) || !Matches(input)) { skipped++; continue; }
                observed.Add(new(input.Id, input.RelativePath, length,
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    HardLinkProbe.CountLinks(input.Path), DateTimeOffset.UtcNow));
                checkedCount++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            finally { progress(new(checkedCount, skipped, candidates.Length, bytes)); }
        }
        var display = ExactDuplicateGrouping.Limit(ExactDuplicateGrouping.Group(observed));
        return new(checkedCount, skipped, candidates.Length, bytes, display.Groups, display.Truncated);
    }

    private static bool Matches(ExactDuplicateInput input)
    {
        var info = new FileInfo(input.Path);
        return info.Exists && info.Length == input.SizeBytes && info.LastWriteTimeUtc == input.ModifiedAt.UtcDateTime;
    }
    internal static bool SafePath(string root, string path)
    {
        root = Path.GetFullPath(root); path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return false;
        // Reject redirects within the configured root. Its parents are explicitly selected by the
        // operator and may be system aliases such as macOS /var -> /private/var.
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) return false;
            if (string.Equals(part, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        return true;
    }
}
