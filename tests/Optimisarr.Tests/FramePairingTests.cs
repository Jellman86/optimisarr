using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public class FramePairingTests
{
    [Fact]
    public void Frames_are_paired_by_number_only_when_both_files_hold_the_same_number()
    {
        Assert.True(FramePairing.Applies(34_046, 34_046));
        // A frame lost in the encode moves every later frame number, so the timestamps stay the
        // better guide.
        Assert.False(FramePairing.Applies(34_046, 34_045));
        Assert.False(FramePairing.Applies(null, 34_046));
        Assert.False(FramePairing.Applies(34_046, null));
        Assert.False(FramePairing.Applies(0, 0));
    }

    [Fact]
    public void A_count_reads_the_moving_picture_stream_without_decoding_it()
    {
        Assert.Equal(
            ["-v", "error", "-select_streams", "V:0", "-count_packets",
             "-show_entries", "stream=nb_read_packets", "-of", "csv=p=0", "/media/a.mkv"],
            FramePairing.CountArguments("/media/a.mkv"));
    }

    [Theory]
    [InlineData("34046\n", 34046)]
    [InlineData("34046,\n", 34046)]
    [InlineData("", null)]
    [InlineData("N/A", null)]
    [InlineData("-3", null)]
    public void A_count_is_read_from_ffprobe_output(string output, int? expected)
    {
        Assert.Equal(expected, FramePairing.ParseCount(output));
    }
}
