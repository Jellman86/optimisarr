using System.Text;
using Optimisarr.Core.Library;
using static Optimisarr.Tests.Mp4TagFixtures;

namespace Optimisarr.Tests;

public sealed class Mp4TagWriterTests
{
    private static Mp4Tags Tags(byte[] file) =>
        Mp4TagReader.Read(new MusicTagBytes(file)).Tags ?? throw new Xunit.Sdk.XunitException(Mp4TagReader.Read(new MusicTagBytes(file)).Problem);

    private static byte[] Rewrite(byte[] file, params byte[][] items)
    {
        var tags = Tags(file);
        var moov = Mp4TagWriter.ReplaceItems(file.AsSpan((int)tags.MoovOffset), items.Select(item => (ReadOnlyMemory<byte>)item), out var problem);
        Assert.True(moov is not null, problem);
        return [.. file.AsSpan(0, (int)tags.MoovOffset), .. moov];
    }

    [Fact]
    public void Replacing_the_items_changes_only_the_tag_list_and_the_sizes_around_it()
    {
        var file = M4a(Text("©nam", "Title"), Text("©too", "Lavf61.7.103"));

        var rewritten = Rewrite(file, Text("©nam", "Title"), Freeform("com.optimisarr", "optimisarr", "1.2.3"));

        var before = Tags(file);
        var after = Tags(rewritten);
        Assert.Equal(file.AsSpan(0, (int)before.MoovOffset).ToArray(), rewritten.AsSpan(0, (int)after.MoovOffset).ToArray());
        Assert.Equal(["©nam", "----:com.optimisarr:optimisarr"], after.Items.Select(item => item.Key));
        Assert.Equal(Mp4TagLayout.Itunes, after.Layout);
        // The header, the track and the rest of the movie box are the same bytes in the same place.
        var unchanged = Moov().Length - 8;
        Assert.Equal(file.AsSpan((int)before.MoovOffset + 8, unchanged).ToArray(), rewritten.AsSpan((int)after.MoovOffset + 8, unchanged).ToArray());
    }

    [Fact]
    public void Written_items_read_back_as_the_items_ffmpeg_and_taggers_write()
    {
        var file = M4a(Text("©too", "Lavf61.7.103"));

        var rewritten = Rewrite(file, Mp4TagWriter.Text("soaa", "Sort"), Mp4TagWriter.Freeform("com.apple.iTunes", "MusicBrainz Album Id", "abc"),
            Mp4TagWriter.NumberPair("trkn", 3, 12), Mp4TagWriter.NumberPair("disk", 1, 2));

        Assert.Equal([
            Tags(M4a(Text("soaa", "Sort"))).Items[0], Tags(M4a(Freeform("com.apple.iTunes", "MusicBrainz Album Id", "abc"))).Items[0],
            Tags(M4a(Box("trkn", DataBox(0, [0, 0, 0, 3, 0, 12, 0, 0])))).Items[0], Tags(M4a(Box("disk", DataBox(0, [0, 0, 0, 1, 0, 2])))).Items[0],
        ], Tags(rewritten).Items);
    }

    [Fact]
    public void Refuses_a_file_whose_tags_are_quicktime_keys()
    {
        var file = (byte[])[.. Ftyp, .. Mdat, .. Moov(QuickTimeKeysMeta(("TITLE", "Title")))];

        Assert.Null(Mp4TagWriter.ReplaceItems(file.AsSpan((int)Tags(file).MoovOffset), [], out var problem));
        Assert.Contains("iTunes", problem);
    }

    [Fact]
    public void Refuses_a_movie_box_with_more_than_one_tag_list()
    {
        var moov = Moov(Box("udta", ItunesMeta(Text("©nam", "A"))), Box("udta", ItunesMeta(Text("©nam", "B"))));

        Assert.Null(Mp4TagWriter.ReplaceItems(moov, [], out _));
    }

    [Fact]
    public void Refuses_bytes_that_are_not_one_whole_movie_box()
    {
        Assert.Null(Mp4TagWriter.ReplaceItems(Mdat, [], out _));
        Assert.Null(Mp4TagWriter.ReplaceItems([.. Moov(Box("udta", ItunesMeta())), 0], [], out _));
    }

    [Fact]
    public void Text_is_written_as_utf8()
    {
        var item = Mp4TagWriter.Freeform("com.apple.iTunes", "ARTISTS", "Björk");

        Assert.Contains("Björk", Encoding.UTF8.GetString(item));
    }
}
