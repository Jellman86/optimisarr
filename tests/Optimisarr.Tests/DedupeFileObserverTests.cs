using System.Security.Cryptography;
using Optimisarr.Api.Deduplication;
using Optimisarr.Core.Deduplication;

namespace Optimisarr.Tests;

public sealed class DedupeFileObserverTests : IDisposable
{
    private static readonly byte[] Content = [1, 2, 3, 4, 5];
    private readonly string _root = Path.GetFullPath(Directory.CreateTempSubdirectory("optimisarr-dedupe-observer-").FullName);
    private string Keeper => Path.Combine(_root, "Film.mkv");
    private string Copy => Path.Combine(_root, "Copy", "Film.mkv");
    private string Quarantine => Path.Combine(_root, ".optimisarr-quarantine", "action", "Film.mkv");

    public DedupeFileObserverTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Copy)!);
        File.WriteAllBytes(Keeper, Content);
        File.WriteAllBytes(Copy, Content);
    }

    private static string Hash => Convert.ToHexStringLower(SHA256.HashData(Content));

    private DedupeFileSnapshot Snapshot(int id, string path, string? quarantine)
    {
        var info = new FileInfo(path);
        return new(id, 7, _root, path, quarantine, $"identity-{id}", info.Exists && info.LinkTarget is null ? info.Length : 5,
            info.Exists ? info.LastWriteTimeUtc.Ticks : 1, Hash, quarantine is null);
    }

    private Task<DedupeFileObservation> ObserveCopy() => DedupeFileObserver.ObserveAsync(Snapshot(2, Copy, Quarantine), default);

    [PosixFact]
    public async Task Observing_an_untouched_keeper_and_copy_satisfies_the_preflight_rule()
    {
        var keeper = await DedupeFileObserver.ObserveAsync(Snapshot(1, Keeper, null), default);
        var copy = await ObserveCopy();
        var plan = new DedupePreparation(Guid.NewGuid(), 7, new string('c', 64), new string('d', 64),
        [
            Snapshot(1, Keeper, null) with { Identity = keeper.Identity! },
            Snapshot(2, Copy, Quarantine) with { Identity = copy.Identity! },
        ]);

        var result = DedupeActionPreflight.Evaluate(plan, [keeper, copy], new(false, true, new HashSet<int>()));

        Assert.Empty(result.Issues);
        Assert.Equal(Hash, copy.FreshSha256);
        Assert.Equal(copy.VolumeId, copy.QuarantineVolumeId);
    }

    [PosixFact]
    public async Task Observing_changes_nothing_on_disk()
    {
        var before = Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order().ToArray();
        var modified = File.GetLastWriteTimeUtc(Copy);

        await ObserveCopy();

        Assert.Equal(before, Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories).Order());
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Copy));
        Assert.Equal(Content, await File.ReadAllBytesAsync(Copy));
    }

    [PosixFact]
    public async Task The_hash_comes_from_the_bytes_on_disk_even_when_size_and_time_are_unchanged()
    {
        var modified = File.GetLastWriteTimeUtc(Copy);
        await File.WriteAllBytesAsync(Copy, [9, 9, 9, 9, 9]);
        File.SetLastWriteTimeUtc(Copy, modified);

        var copy = await ObserveCopy();

        Assert.NotEqual(Hash, copy.FreshSha256);
        Assert.Equal(new FileInfo(Copy).Length, copy.SizeBytes);
    }

    [PosixFact]
    public async Task A_missing_file_is_reported_absent()
    {
        var snapshot = Snapshot(2, Copy, Quarantine);
        File.Delete(Copy);
        Assert.False((await DedupeFileObserver.ObserveAsync(snapshot, default)).Exists);
    }

    [PosixFact]
    public async Task A_linked_file_is_reported_and_not_read()
    {
        File.Delete(Copy);
        File.CreateSymbolicLink(Copy, Keeper);

        var copy = await ObserveCopy();

        Assert.True(copy.IsLink);
        Assert.Null(copy.FreshSha256);
    }

    [PosixFact]
    public async Task A_linked_folder_inside_the_library_is_reported_as_a_link()
    {
        var elsewhere = Directory.CreateTempSubdirectory("optimisarr-dedupe-elsewhere-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(elsewhere, "Film.mkv"), Content);
            Directory.Delete(Path.GetDirectoryName(Copy)!, true);
            Directory.CreateSymbolicLink(Path.GetDirectoryName(Copy)!, elsewhere);

            Assert.True((await ObserveCopy()).IsLink);
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    [PosixFact]
    public async Task A_hard_linked_file_reports_its_real_link_count()
    {
        var extra = Path.Combine(_root, "extra.mkv");
        Assert.True(HardLinkFixture.TryCreate(Copy, extra));

        Assert.Equal(2, (await ObserveCopy()).HardLinkCount);
    }

    [PosixFact]
    public async Task An_occupied_destination_is_reported_even_when_it_is_a_dangling_link()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Quarantine)!);
        File.CreateSymbolicLink(Quarantine, Path.Combine(_root, "nothing-here"));

        Assert.True((await ObserveCopy()).QuarantineDestinationExists);
    }

    [PosixFact]
    public async Task A_read_only_folder_is_reported_as_not_writable()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        var folder = Path.GetDirectoryName(Copy)!;
        var mode = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { Assert.False((await ObserveCopy()).ParentWritable); }
        finally { File.SetUnixFileMode(folder, mode); }
    }

    [Theory]
    [InlineData("linux:8:1:1234", "linux:8:1")]
    [InlineData("darwin:16777220:1234", "darwin:16777220")]
    [InlineData("windows:123:0:1234", null)]
    [InlineData("linux:8:1", null)]
    [InlineData(null, null)]
    public void Only_identities_that_name_a_filesystem_give_a_volume(string? identity, string? volume) =>
        Assert.Equal(volume, DedupeFileObserver.VolumeOf(identity));

    [Theory]
    [InlineData("Film.mkv", true)]
    [InlineData("Season 1/Episode.mkv", true)]
    [InlineData("Film/VIDEO_TS/VTS_01_1.VOB", false)]
    [InlineData("Film/bdmv/STREAM/00001.m2ts", false)]
    [InlineData("Film/VTS_01_0.ifo", false)]
    [InlineData("", false)]
    public void Disc_structures_are_never_independent_files(string path, bool independent) =>
        Assert.Equal(independent, DedupeFileEligibility.IsIndependentFile(path));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static class HardLinkFixture
    {
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern int Link(string existing, string created);

        public static bool TryCreate(string existing, string created) => Link(existing, created) == 0;
    }
}
