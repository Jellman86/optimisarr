using System.Buffers.Binary;
using Optimisarr.Core.Library;
using static Optimisarr.Tests.Mp4TagFixtures;

namespace Optimisarr.Tests;

public sealed class Mp4TagReaderTests
{
    private static Mp4TagRead Read(byte[] file) => Mp4TagReader.Read(new MusicTagBytes(file));

    private static Mp4Tags Tags(byte[] file) => Read(file).Tags ?? throw new Xunit.Sdk.XunitException(Read(file).Problem);

    [Fact]
    public void Reads_itunes_items_by_atom_and_freeform_items_by_their_mean_and_name()
    {
        var tags = Tags(M4a(Text("©nam", "Title"), Freeform("com.apple.iTunes", "MusicBrainz Album Id", "abc"),
            Box("covr", DataBox(14, Png))));

        Assert.Equal(Mp4TagLayout.Itunes, tags.Layout);
        Assert.Equal(["©nam", "----:com.apple.iTunes:MusicBrainz Album Id", "covr"], tags.Items.Select(item => item.Key));
        Assert.All(tags.Items, item => Assert.Single(item.Values));
        Assert.StartsWith("14:", tags.Items[2].Values[0]);
    }

    [Fact]
    public void Items_are_the_same_only_with_the_same_data_type_and_bytes()
    {
        var text = Tags(M4a(Text("©nam", "Title"))).Items.Single();
        var sameText = Tags(M4a(Text("©nam", "Title"))).Items.Single();
        var otherType = Tags(M4a(Box("©nam", DataBox(0, "Title"u8.ToArray())))).Items.Single();
        var otherText = Tags(M4a(Text("©nam", "Other"))).Items.Single();

        Assert.Equal(text, sameText);
        Assert.NotEqual(text, otherType);
        Assert.NotEqual(text, otherText);
    }

    [Fact]
    public void An_item_keeps_its_whole_atom_so_it_can_be_copied_exactly()
    {
        var atom = Freeform("com.apple.iTunes", "iTunNORM", " 00000100");

        Assert.Equal(atom, Tags(M4a(atom)).Items.Single().Atom.ToArray());
    }

    [Fact]
    public void Quicktime_keys_are_recognised_where_ffmpeg_writes_them_beside_the_track()
    {
        var file = (byte[])[.. Ftyp, .. Mdat, .. Moov(Box("udta"), QuickTimeKeysMeta(("optimisarr", "v1"), ("TITLE", "Title")))];

        var tags = Tags(file);

        Assert.Equal(Mp4TagLayout.QuickTimeKeys, tags.Layout);
    }

    [Fact]
    public void Quicktime_keys_beside_an_itunes_list_still_count_as_quicktime_keys()
    {
        var file = (byte[])[.. Ftyp, .. Mdat, .. Moov(Box("udta", ItunesMeta(Text("©nam", "Title"))), QuickTimeKeysMeta(("TITLE", "Title")))];

        Assert.Equal(Mp4TagLayout.QuickTimeKeys, Tags(file).Layout);
    }

    [Fact]
    public void A_file_without_tags_has_no_layout()
    {
        var tags = Tags([.. Ftyp, .. Mdat, .. Moov()]);

        Assert.Equal(Mp4TagLayout.None, tags.Layout);
        Assert.Empty(tags.Items);
    }

    [Fact]
    public void Says_where_the_movie_box_is_and_whether_anything_follows_it()
    {
        var last = Tags(M4a(Text("©nam", "Title")));
        var first = Tags([.. Ftyp, .. Moov(Box("udta", ItunesMeta(Text("©nam", "Title")))), .. Mdat]);

        Assert.True(last.MoovIsLast);
        Assert.Equal(Ftyp.Length + 8 + Mdat.Length, last.MoovOffset);
        Assert.False(first.MoovIsLast);
        Assert.Equal(Ftyp.Length, first.MoovOffset);
    }

    [Fact]
    public void A_large_sample_box_with_a_64_bit_size_is_stepped_over()
    {
        var mdat = new byte[16 + 32];
        BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        "mdat"u8.CopyTo(mdat.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);

        var tags = Tags([.. Ftyp, .. mdat, .. Moov(Box("udta", ItunesMeta(Text("©nam", "Title"))))]);

        Assert.Equal("©nam", tags.Items.Single().Key);
    }

    [Fact]
    public void A_box_that_runs_past_the_end_of_the_file_is_unreadable()
    {
        var file = M4a(Text("©nam", "Title"));
        var cut = file[..^4];

        Assert.Null(Read(cut).Tags);
        Assert.Contains("end of the file", Read(cut).Problem);
    }

    [Fact]
    public void An_item_whose_size_overruns_its_list_is_unreadable()
    {
        var item = Text("©nam", "Title");
        BinaryPrimitives.WriteUInt32BigEndian(item, (uint)item.Length + 40);

        Assert.Null(Read(M4a(item)).Tags);
    }

    [Fact]
    public void An_item_holding_anything_but_data_is_unreadable()
    {
        Assert.Null(Read(M4a(Box("©nam", Box("itif", [0, 0, 0, 1])))).Tags);
        Assert.Null(Read(M4a(Box("©nam"))).Tags);
    }

    [Fact]
    public void A_freeform_item_without_its_mean_or_name_is_unreadable()
    {
        Assert.Null(Read(M4a(Box("----", FullBox("name", "x"u8.ToArray()), DataBox(1, "v"u8.ToArray())))).Tags);
    }

    [Fact]
    public void Two_movie_boxes_or_none_are_unreadable()
    {
        Assert.Null(Read([.. Ftyp, .. Mdat]).Tags);
        Assert.Null(Read([.. Ftyp, .. Mdat, .. Moov(), .. Moov()]).Tags);
    }

    [Fact]
    public void A_file_that_does_not_start_with_a_file_type_box_is_unreadable()
    {
        Assert.Null(Read([.. Mdat, .. Moov()]).Tags);
        Assert.Null(Read("ID3"u8.ToArray()).Tags);
    }

    [Theory]
    [InlineData("/music/Track.m4a", true)]
    [InlineData("/music/Book.M4B", true)]
    [InlineData("/films/Film.mp4", true)]
    [InlineData("/films/Film.mov", true)]
    [InlineData("/music/Track.flac", false)]
    [InlineData("/films/Film.mkv", false)]
    public void Knows_the_mp4_family_by_extension(string path, bool expected) =>
        Assert.Equal(expected, Mp4TagReader.IsMp4Family(path));
}
