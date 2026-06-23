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
