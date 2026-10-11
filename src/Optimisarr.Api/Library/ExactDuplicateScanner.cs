using System.Security.Cryptography;
using Optimisarr.Core.IO;
using Optimisarr.Core.Library;

namespace Optimisarr.Api.Library;

public sealed record ExactDuplicateCheckpoint(ExactDuplicateInput Input, ExactFileFingerprint? Fingerprint,
    int? HardLinkCount, bool Reused, ExactDuplicateProgress Progress);
public sealed record ExactDuplicateInput(int Id, string Path, string RelativePath, long SizeBytes, DateTimeOffset ModifiedAt);
public sealed record ExactDuplicateProgress(int Checked, int Skipped, int Total, long BytesRead, int Reused = 0);
public sealed record ExactDuplicateResult(int Checked, int Skipped, int Total, long BytesRead,
    IReadOnlyList<ExactDuplicateGroup> Groups, bool Truncated, int Reused = 0, int TotalGroups = 0, int Page = 0, bool HasMore = false);

/// <summary>Explicit, read-only scan. One 128 KiB block every 16 ms caps reads near 8 MiB/s.
/// Reports are snapshots. Cached content-read times are preserved; no cleanup is supported.</summary>
public sealed class ExactDuplicateScanner(TimeSpan? pause = null)
{
    private readonly TimeSpan _pause = pause ?? TimeSpan.FromMilliseconds(16);
    public async Task<ExactDuplicateResult> ScanAsync(string root, IReadOnlyList<ExactDuplicateInput> inputs,
        Action<ExactDuplicateProgress> progress, CancellationToken token,
        IReadOnlyDictionary<int, ExactFileFingerprint>? cache = null, bool forceRefresh = false,
        Func<ExactDuplicateCheckpoint, CancellationToken, Task>? checkpoint = null)
    {
        token.ThrowIfCancellationRequested();
        var candidates = inputs.Where(f => f.SizeBytes > 0).DistinctBy(f => f.Path,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .GroupBy(f => f.SizeBytes).Where(g => g.Count() > 1).SelectMany(g => g).ToArray();
        var observed = new List<ExactDuplicateFile>();
        var checkedCount = 0; var skipped = 0; var reused = 0; long bytes = 0;
        progress(new(0, 0, candidates.Length, 0));
        var buffer = new byte[128 * 1024];
        foreach (var input in candidates)
        {
            token.ThrowIfCancellationRequested();
            ExactFileFingerprint? fingerprint = null; int? links = null; var wasReused = false;
            try
            {
                if (!SafePath(root, input.Path) || !Matches(input)) { skipped++; continue; }
                var generation = new ExactFileGeneration(Path.GetFullPath(root), Path.GetFullPath(input.Path),
                    input.SizeBytes, input.ModifiedAt.UtcTicks, ExactFileIdentity.Read(input.Path));
                var previous = cache?.GetValueOrDefault(input.Id);
                if (ExactDuplicateCache.CanReuse(generation, previous, forceRefresh))
                {
                    using var access = File.OpenHandle(input.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (ExactFileIdentity.Read(access) != generation.Identity) { skipped++; continue; }
                    if (!SafePath(root, input.Path) || !Matches(input) || ExactFileIdentity.Read(input.Path) != generation.Identity)
                    { skipped++; continue; }
                    fingerprint = previous;
                    links = HardLinkProbe.CountLinks(input.Path);
                    wasReused = true; reused++; checkedCount++;
                    observed.Add(new(input.Id, input.RelativePath, input.SizeBytes, previous!.Sha256, links, previous.CheckedAt, true));
                    continue;
                }
                await using var stream = new FileStream(input.Path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length != input.SizeBytes || ExactFileIdentity.Read(stream.SafeFileHandle) != generation.Identity) { skipped++; continue; }
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long length = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, token)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, count)); length += count; bytes += count;
                    progress(new(checkedCount, skipped, candidates.Length, bytes, reused));
                    if (_pause > TimeSpan.Zero) await Task.Delay(_pause, token);
                    if (length > input.SizeBytes) break;
                }
                if (length != input.SizeBytes || !SafePath(root, input.Path) || !Matches(input)
                    || ExactFileIdentity.Read(input.Path) != generation.Identity) { skipped++; continue; }
                fingerprint = new(generation, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    DateTimeOffset.UtcNow, ExactDuplicateCache.Contract);
                links = HardLinkProbe.CountLinks(input.Path);
                observed.Add(new(input.Id, input.RelativePath, length, fingerprint.Sha256, links, fingerprint.CheckedAt));
                checkedCount++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
            finally
            {
                var current = new ExactDuplicateProgress(checkedCount, skipped, candidates.Length, bytes, reused);
                progress(current);
                if (checkpoint is not null && !token.IsCancellationRequested)
                    await checkpoint(new(input, fingerprint, links, wasReused, current), token);
            }
        }
        var groups = ExactDuplicateGrouping.Group(observed);
        var display = ExactDuplicateGrouping.Limit(groups);
        return new(checkedCount, skipped, candidates.Length, bytes, display.Groups, display.Truncated, reused, groups.Count);
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
