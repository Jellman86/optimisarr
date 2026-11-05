using Optimisarr.Api.Replacement;

namespace Optimisarr.Tests;

public sealed class FileMoverTests : IDisposable
{
    private readonly string _dir;

    public FileMoverTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "optimisarr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Move_relocates_the_file_and_creates_missing_destination_directories()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "nested", "dir", "dest.bin");
        File.WriteAllText(source, "payload");

        var result = FileMover.Move(source, destination);

        Assert.False(result.CrossFilesystem);
        Assert.False(File.Exists(source));
        Assert.Equal("payload", File.ReadAllText(destination));
    }

    [Fact]
    public void Move_refuses_to_overwrite_an_existing_destination()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "existing");

        Assert.Throws<IOException>(() => FileMover.Move(source, destination));

        // Neither file is touched, so no data is lost.
        Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal("existing", File.ReadAllText(destination));
    }

    [PosixFact]
    public void Move_on_one_filesystem_is_a_rename_that_keeps_the_same_file()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllText(source, "payload");
        var identity = Optimisarr.Api.Library.ExactFileIdentity.Read(source);

        var result = FileMover.Move(source, destination);

        Assert.False(result.CrossFilesystem);
        Assert.NotNull(identity);
        Assert.Equal(identity, Optimisarr.Api.Library.ExactFileIdentity.Read(destination));
    }

    [Fact]
    public void Rename_never_replaces_a_destination_that_appeared_after_the_check()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "existing");

        Assert.Equal(RenameOutcome.DestinationExists, RenameOnly.Rename(source, destination));

        Assert.Equal("new", File.ReadAllText(source));
        Assert.Equal("existing", File.ReadAllText(destination));
    }

    [PosixFact]
    public void Move_across_filesystems_reports_it_and_keeps_only_a_verified_copy()
    {
        // tmpfs is a separate filesystem from the temp folder wherever both exist; a machine
        // that has no such pair has no cross-device rename to exercise here.
        const string memory = "/dev/shm";
        if (!Directory.Exists(memory) || DeviceOf(memory) == DeviceOf(_dir)) return;
        var folder = Path.Combine(memory, "optimisarr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(_dir, "source.bin");
            var destination = Path.Combine(folder, "dest.bin");
            File.WriteAllText(source, "payload");

            Assert.Equal(RenameOutcome.CrossDevice, RenameOnly.Rename(source, destination));
            Assert.True(File.Exists(source));
            Assert.False(File.Exists(destination));

            var result = FileMover.Move(source, destination);

            Assert.True(result.CrossFilesystem);
            Assert.False(File.Exists(source));
            Assert.Equal("payload", File.ReadAllText(destination));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void Rename_moves_a_file_on_one_volume_and_leaves_nothing_at_the_source()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllText(source, "payload");

        Assert.Equal(RenameOutcome.Renamed, RenameOnly.Rename(source, destination));

        Assert.False(File.Exists(source));
        Assert.Equal("payload", File.ReadAllText(destination));
    }

    [Fact]
    public void A_copy_that_fails_verification_keeps_the_source_and_removes_the_copy()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllText(source, "payload");

        Assert.Throws<IOException>(() => FileMover.CopyVerifyDelete(source, destination,
            (_, _) => throw new IOException("Copy verification failed.")));

        Assert.Equal("payload", File.ReadAllText(source));
        Assert.False(File.Exists(destination));
    }

    private static string? DeviceOf(string path) =>
        Optimisarr.Api.Library.ExactFileIdentity.Read(path)?.Split(':') is ["linux", var major, var minor, _] ? $"{major}:{minor}" : null;

    [Fact]
    public void Move_throws_when_the_source_is_missing()
    {
        Assert.Throws<FileNotFoundException>(
            () => FileMover.Move(Path.Combine(_dir, "absent.bin"), Path.Combine(_dir, "dest.bin")));
    }

    [Fact]
    public void VerifyCopiedContent_accepts_an_identical_copy()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
        File.Copy(source, destination);

        FileMover.VerifyCopiedContent(source, destination);   // does not throw
    }

    [Fact]
    public void VerifyCopiedContent_rejects_a_truncated_copy()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllBytes(destination, new byte[] { 1, 2, 3 });

        var ex = Assert.Throws<IOException>(() => FileMover.VerifyCopiedContent(source, destination));
        Assert.Contains("size", ex.Message);
    }

    [Fact]
    public void VerifyCopiedContent_rejects_same_length_corruption_a_length_check_would_miss()
    {
        var source = Path.Combine(_dir, "source.bin");
        var destination = Path.Combine(_dir, "dest.bin");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4, 5 });
        File.WriteAllBytes(destination, new byte[] { 1, 2, 9, 4, 5 });   // same length, one byte flipped

        var ex = Assert.Throws<IOException>(() => FileMover.VerifyCopiedContent(source, destination));
        Assert.Contains("content", ex.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
