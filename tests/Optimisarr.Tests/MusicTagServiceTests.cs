using Optimisarr.Api.Library;
using Optimisarr.Core.Library;
using static Optimisarr.Tests.MusicTagFixtures;

namespace Optimisarr.Tests;

public sealed class MusicTagServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-tags-").FullName;

    [Fact]
    public async Task A_file_is_read_where_the_reader_asks_including_its_end()
    {
        var path = Path.Combine(_root, "Track.mp3");
        await File.WriteAllBytesAsync(path, [.. Id3v2(3, Text(3, "TIT2", "Title")), .. new byte[200_000], .. Id3v1("Title", "Artist", 4, 17)]);

        var read = await new MusicTagService().ReadAsync(path, default);

        Assert.NotNull(read.Tags);
        Assert.Equal(3, read.Tags.Id3Version);
        Assert.Contains(new MusicTag("TIT2", ["Title"]), read.Tags.Tags);
        Assert.Contains(new MusicTag("ID3V1:ARTIST", ["Artist"]), read.Tags.Tags);
    }

    [Fact]
    public async Task A_file_that_cannot_be_opened_is_unreadable_rather_than_an_error()
    {
        var read = await new MusicTagService().ReadAsync(Path.Combine(_root, "missing.flac"), default);

        Assert.Null(read.Tags);
        Assert.StartsWith("the file could not be read", read.Problem);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
