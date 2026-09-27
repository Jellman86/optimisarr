using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class TimestampProbeArgumentsTests
{
    [Fact]
    public void Only_source_video_reconstructs_missing_presentation_times()
    {
        const string path = "/source with spaces and 'quotes.mkv";
        var source = TimestampIntegrityCheck.Arguments(path, "V:0", generateMissingPts: true);
        Assert.Equal(path, source[^1]);
        Assert.Contains("+genpts", source);
        Assert.DoesNotContain("+genpts", TimestampIntegrityCheck.Arguments(path, "V:0", generateMissingPts: false));
        Assert.DoesNotContain("+genpts", TimestampIntegrityCheck.Arguments(path, "a:0", generateMissingPts: false));
    }
}
