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
    public void A_count_decodes_the_moving_picture_stream_instead_of_counting_packets()
    {
        Assert.Equal(
            ["-v", "error", "-select_streams", "V:0", "-count_frames",
             "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", "/media/a.mkv"],
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

    [Theory]
    [InlineData("[0:v]trim=start_frame=33358:end_frame=34379,settb=AVTB[d]", 37635, true)]
    [InlineData("[0:v]trim=start_frame=37635:end_frame=37635,settb=AVTB[d]", 37635, true)]
    [InlineData("[0:v]trim=start_frame=39171:end_frame=40370,settb=AVTB[d]", 37635, false)]
    [InlineData("[0:v]settb=AVTB,setpts=N*41708[d]", 10, true)]
    public void A_frame_window_fits_only_when_it_ends_within_the_counted_frames(string filter, int frames, bool fits)
    {
        Assert.Equal(fits, FramePairing.FitsWithin(["-nostdin", "-lavfi", filter, "-f", "null", "-"], frames));
    }
}
