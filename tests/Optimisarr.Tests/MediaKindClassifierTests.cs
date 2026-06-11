using Optimisarr.Core.Domain;

namespace Optimisarr.Tests;

public sealed class MediaKindClassifierTests
{
    [Theory]
    [InlineData(".mkv")]
    [InlineData(".mp4")]
    [InlineData(".avi")]
    public void A_real_video_stream_makes_it_video(string extension)
    {
        Assert.Equal(MediaKind.Video, MediaKindClassifier.Classify(extension, hasNonCoverVideoStream: true, hasAudioStream: true));
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".m4a")]
    [InlineData(".opus")]
    public void Audio_with_no_real_video_stream_is_audio(string extension)
    {
        // An audio file's embedded cover art is an attached-picture stream, not a real
        // video stream, so it must not be mistaken for a movie.
        Assert.Equal(MediaKind.Audio, MediaKindClassifier.Classify(extension, hasNonCoverVideoStream: false, hasAudioStream: true));
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".webp")]
    [InlineData(".avif")]
    public void A_known_image_extension_wins_over_the_still_image_video_stream(string extension)
    {
        // A still image reports a video stream (mjpeg/png/…); the extension is what tells us
        // it is a picture rather than a one-frame video.
        Assert.Equal(MediaKind.Image, MediaKindClassifier.Classify(extension, hasNonCoverVideoStream: true, hasAudioStream: false));
    }

    [Fact]
    public void Image_extensions_are_matched_case_insensitively_and_without_a_dot()
    {
        Assert.Equal(MediaKind.Image, MediaKindClassifier.Classify(".PNG", true, false));
        Assert.Equal(MediaKind.Image, MediaKindClassifier.Classify("jpg", true, false));
    }

    [Fact]
    public void Nothing_recognisable_is_unknown()
    {
        Assert.Equal(MediaKind.Unknown, MediaKindClassifier.Classify(".srt", hasNonCoverVideoStream: false, hasAudioStream: false));
        Assert.Equal(MediaKind.Unknown, MediaKindClassifier.Classify(null, false, false));
    }
}
