using Optimisarr.Api;
using Optimisarr.Api.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SettingsRequestParserTests
{
    [Fact]
    public void A_client_from_before_the_tone_map_setting_keeps_the_software_default()
    {
        var request = ValidRequest() with { HdrToneMapMode = null };

        var parsed = SettingsRequestParser.TryParse(request, out var settings, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(HdrToneMapMode.Software, settings.HdrToneMapMode);
    }

    [Theory]
    [InlineData("Magic")]
    [InlineData("999")]
    public void An_unknown_tone_map_mode_is_rejected_before_it_reaches_ffmpeg(string value)
    {
        var request = ValidRequest() with { HdrToneMapMode = value };

        var parsed = SettingsRequestParser.TryParse(request, out _, out var error);

        Assert.False(parsed);
        Assert.Equal("settings.hdrToneMapMode.invalid", error?.Code);
    }

    private static SettingsDto ValidRequest() => SettingsDto.From(new QueueSettings(
        MaxConcurrentJobs: 1,
        MinFreeDiskBytes: 0,
        CpuThreadLimit: 0,
        LibraryScanIntervalHours: 1,
        EncoderMode: EncoderMode.Auto,
        HardwareDecode: true,
        HdrToneMapMode: HdrToneMapMode.Software,
        VerificationPolicy: VerificationPolicy.Default,
        ReplacementAllowCrossFilesystem: false,
        DryRunMode: false,
        ReplacementQuarantineRetentionDays: 0));
}
