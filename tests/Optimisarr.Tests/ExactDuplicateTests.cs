using Optimisarr.Core.Library;
using Optimisarr.Api.Library;
using System.Runtime.InteropServices;

namespace Optimisarr.Tests;

public sealed class ExactDuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-duplicates-" + Guid.NewGuid());
    public ExactDuplicateTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Groups_require_complete_hashes_and_equal_size_and_distinct_paths()
    {
        var hash = new string('a', 64);
        var items = new[] { Entry(1, "a", 3, hash, 1), Entry(2, "b", 3, hash, 1),
            Entry(3, "c", 4, hash, 1), Entry(4, "a", 3, hash, 1), Entry(5, "d", 3, "broken", 1) };
        var group = Assert.Single(ExactDuplicateGrouping.Group(items));
        Assert.Equal(2, group.Copies.Count);
        Assert.Equal(3, group.ExtraCopyBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public void Linked_or_unknown_storage_never_claims_reclaimable_bytes(int? links)
    {
        var group = Assert.Single(ExactDuplicateGrouping.Group([
            Entry(1, "a", 3, new string('a', 64), 1), Entry(2, "b", 3, new string('a', 64), links)]));
        Assert.Null(group.ExtraCopyBytes);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(500)]
    public void Large_reports_bound_copy_count_and_text_and_hide_shortened_totals(int pathLength)
    {
        var entries = Enumerable.Range(0, 5_000).Select(i => Entry(i, i.ToString() + new string('x', pathLength), (i / 50) + 1, new string('a', 64), 1));
        var groups = ExactDuplicateGrouping.Limit(ExactDuplicateGrouping.Group(entries));
        Assert.True(groups.Truncated);
        Assert.All(groups.Groups.Where(g => g.Copies.Count < g.TotalCopies), g => Assert.Null(g.ExtraCopyBytes));
        Assert.InRange(groups.Groups.Sum(g => g.Copies.Count), 2, 2_000);
        Assert.InRange(groups.Groups.Sum(g => g.Copies.Sum(f => f.RelativePath.Length)), 1, 500_000);
        var largeGroup = ExactDuplicateGrouping.Group(entries.Select(f => f with { SizeBytes = 3 }));
        Assert.Null(Assert.Single(ExactDuplicateGrouping.Limit(largeGroup).Groups).ExtraCopyBytes);
    }

    [Fact]
    public async Task Scanner_reads_same_size_audio_images_video_and_keeps_originals_unchanged()
    {
        var inputs = new[] { await File(1, "song.wav", [1,2,3]), await File(2, "film.mkv", [1,2,3]),
            await File(3, "image.jpg", [1,2,3]), await File(4, "other.jpg", [3,2,1]),
            await File(5, "unique.wav", [1,2,3,4]) };
        var result = await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root, inputs, _ => { }, default);
        var group = Assert.Single(result.Groups);
        Assert.Equal(3, group.Copies.Count);
        Assert.Equal(4, result.Checked);
        Assert.Equal(new byte[] { 1, 2, 3 }, await System.IO.File.ReadAllBytesAsync(inputs[0].Path));
    }

    [Fact]
    public async Task Changed_missing_outside_and_linked_paths_are_skipped()
    {
        var a = await File(1, "a.jpg", [1, 2, 3]); var b = await File(2, "b.jpg", [1, 2, 3]);
        var c = await File(3, "gone.jpg", [1, 2, 3]); System.IO.File.Delete(c.Path);
        var changed = b with { ModifiedAt = b.ModifiedAt.AddSeconds(-1) };
        var outside = a with { Id = 4, Path = Path.Combine(_root, "..", "outside.jpg") };
        var result = await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root, [a, changed, c, outside], _ => { }, default);
        Assert.Empty(result.Groups);
        Assert.Equal(3, result.Skipped);
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(_root, "link.jpg"); System.IO.File.CreateSymbolicLink(link, a.Path);
            result = await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root, [a, a with { Id = 5, Path = link }], _ => { }, default);
            Assert.Empty(result.Groups); Assert.Equal(1, result.Skipped);
        }
    }

    [Fact]
    public async Task Cancellation_leaves_media_untouched_and_never_publishes_a_finished_report()
    {
        var a = await File(1, "a.wav", [1, 2, 3]); var b = await File(2, "b.wav", [1, 2, 3]);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExactDuplicateScanner(TimeSpan.Zero)
            .ScanAsync(_root, [a, b], _ => { }, cancel.Token));
        Assert.Equal(3, new FileInfo(a.Path).Length);
    }

    [Fact]
    public async Task A_file_changed_during_hashing_cannot_join_a_group()
    {
        var a = await File(1, "a.wav", new byte[300_000]); var b = await File(2, "b.wav", new byte[300_000]);
        var changed = false;
        var result = await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root, [a, b], p =>
        {
            if (!changed && p.BytesRead > 0) { changed = true; System.IO.File.SetLastWriteTimeUtc(a.Path, DateTime.UtcNow.AddHours(1)); }
        }, default);
        Assert.Empty(result.Groups); Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task A_directory_link_inside_the_library_cannot_escape_the_root()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = await File(1, "a.jpg", [1, 2, 3]);
        Directory.CreateSymbolicLink(Path.Combine(_root, "alias"), _root);
        try
        {
            var result = await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root,
                [a, a with { Id = 2, Path = Path.Combine(_root, "alias", "a.jpg"), RelativePath = "alias/a.jpg" }], _ => { }, default);
            Assert.Empty(result.Groups); Assert.Equal(1, result.Skipped);
        }
        finally { Directory.Delete(Path.Combine(_root, "alias")); }
    }

    [Fact]
    public async Task Real_hardlinks_never_claim_extra_disk_space()
    {
        var a = await File(1, "a.wav", [1, 2, 3]); var path = Path.Combine(_root, "b.wav");
        Assert.True(OperatingSystem.IsWindows() ? CreateHardLink(path, a.Path, IntPtr.Zero) : Link(a.Path, path) == 0);
        var b = a with { Id = 2, Path = path, RelativePath = "b.wav" };
        var result = await new ExactDuplicateScanner(TimeSpan.Zero).ScanAsync(_root, [a, b], _ => { }, default);
        Assert.Null(Assert.Single(result.Groups).ExtraCopyBytes);
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string source, string target);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string target, string source, IntPtr reserved);

    private static ExactDuplicateFile Entry(int id, string path, long size, string hash, int? links) =>
        new(id, path, size, hash, links, DateTimeOffset.UtcNow);
    private async Task<ExactDuplicateInput> File(int id, string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name); await System.IO.File.WriteAllBytesAsync(path, bytes);
        return new(id, path, name, bytes.Length, new FileInfo(path).LastWriteTimeUtc);
    }
}
