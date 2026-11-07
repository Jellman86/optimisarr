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

    private Task<DedupeFileObservation> ObserveCopy(Func<Task>? afterRead = null) =>
        DedupeFileObserver.ObserveAsync(Snapshot(2, Copy, Quarantine), default, TimeSpan.Zero, afterRead);

    [PosixFact]
    public async Task Observing_an_untouched_keeper_and_copy_satisfies_the_preflight_rule()
    {
        var keeper = await DedupeFileObserver.ObserveAsync(Snapshot(1, Keeper, null), default, TimeSpan.Zero);
        var copy = await ObserveCopy();
        var plan = new DedupePreparation(Guid.NewGuid(), 7, new string('c', 64), new string('d', 64),
        [
            Snapshot(1, Keeper, null) with { Identity = keeper.Identity! },
            Snapshot(2, Copy, Quarantine) with { Identity = copy.Identity! },
        ]);

        var result = DedupeActionPreflight.Evaluate(plan, [keeper, copy], new(true, new HashSet<int>(), new HashSet<int>(), true, new HashSet<int>()));

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
    public async Task A_file_changed_moments_ago_is_not_read_until_it_has_settled() =>
        Assert.Null((await DedupeFileObserver.ObserveAsync(Snapshot(2, Copy, Quarantine), default)).FreshSha256);

    [PosixFact]
    public async Task A_write_during_the_read_that_restores_size_and_time_gives_no_hash()
    {
        var modified = File.GetLastWriteTimeUtc(Copy);
        var written = false;
        var copy = await ObserveCopy(async () =>
        {
            // Past the clock tick of the stamp taken before the read.
            await Task.Delay(50);
            await File.WriteAllBytesAsync(Copy, [9, 2, 3, 4, 5]);
            File.SetLastWriteTimeUtc(Copy, modified);
            written = true;
        });

        Assert.True(written);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Copy));
        Assert.Null(copy.FreshSha256);
    }

    [PosixFact]
    public async Task A_parent_swapped_for_a_link_during_the_read_is_reported_as_a_link()
    {
        var folder = Path.GetDirectoryName(Copy)!;
        var moved = folder + "-moved";
        var copy = await ObserveCopy(() =>
        {
            Directory.Move(folder, moved);
            Directory.CreateSymbolicLink(folder, moved);
            return Task.CompletedTask;
        });

        Assert.True(copy.IsLink);
        Assert.Null(copy.FreshSha256);
    }

    [PosixFact]
    public async Task A_quarantine_folder_that_is_a_link_is_refused_even_on_the_same_filesystem()
    {
        var elsewhere = Directory.CreateTempSubdirectory("optimisarr-dedupe-elsewhere-").FullName;
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, ".optimisarr-quarantine"), elsewhere);
            Assert.False((await ObserveCopy()).QuarantinePathSafe);
        }
        finally { Directory.Delete(elsewhere, true); }
    }

    [PosixFact]
    public async Task A_dangling_link_or_file_in_the_quarantine_path_is_refused()
    {
        var first = Path.Combine(_root, ".optimisarr-quarantine");
        File.CreateSymbolicLink(first, Path.Combine(_root, "nothing-here"));
        Assert.False((await ObserveCopy()).QuarantinePathSafe);

        File.Delete(first);
        File.WriteAllText(first, "a file where a folder must go");
        Assert.False((await ObserveCopy()).QuarantinePathSafe);
    }

    [PosixFact]
    public void A_library_root_that_is_itself_a_link_is_not_a_safe_path()
    {
        var link = _root + "-link";
        Directory.CreateSymbolicLink(link, _root);
        try
        {
            Assert.False(DedupeFileObserver.WalkFolders(link, Path.Combine(link, "Copy", "Film.mkv")).Safe);
            Assert.False(DedupeFileObserver.WalkFolders(link, Path.Combine(link, "Film.mkv")).Safe);
            Assert.True(DedupeFileObserver.WalkFolders(_root, Path.Combine(_root, "Copy", "Film.mkv")).Safe);
        }
        finally { Directory.Delete(link); }
    }

    [PosixFact]
    public async Task A_quarantine_path_that_does_not_exist_yet_is_safe()
    {
        var copy = await ObserveCopy();
        Assert.True(copy.QuarantinePathSafe);
        Assert.NotNull(copy.QuarantineVolumeId);
    }

    [PosixFact]
    public async Task A_library_rooted_at_a_disc_folder_is_still_a_disc()
    {
        var disc = Path.Combine(_root, "BDMV");
        var stream = Path.Combine(disc, "STREAM", "00001.m2ts");
        Directory.CreateDirectory(Path.GetDirectoryName(stream)!);
        File.WriteAllBytes(stream, Content);

        var observed = await DedupeFileObserver.ObserveAsync(Snapshot(3, stream, null) with { Root = disc }, default, TimeSpan.Zero);

        Assert.False(observed.IsIndependentFile);
    }

    [PosixFact]
    public async Task A_missing_file_is_reported_absent()
    {
        var snapshot = Snapshot(2, Copy, Quarantine);
        File.Delete(Copy);
        Assert.False((await DedupeFileObserver.ObserveAsync(snapshot, default, TimeSpan.Zero)).Exists);
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
