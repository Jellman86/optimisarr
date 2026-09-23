using Optimisarr.Api;
using Optimisarr.Api.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class SettingsRequestParserTests
{
    [Fact]
    public void An_older_client_keeps_saved_workload_lanes()
    {
        var current = new QueueSettings(1, 0, 0, 1, EncoderMode.Auto, true,
            HdrToneMapMode.Software, VerificationPolicy.Default, false, false, 0,
            WorkloadConcurrencyMode: WorkloadConcurrencyMode.Manual, NonVideoSlots: 1,
            EvidenceValidationSlots: 3);
        var request = ValidRequest() with
        {
            WorkloadConcurrencyMode = null,
            NonVideoSlots = null,
            EvidenceValidationSlots = null
        };

        Assert.True(SettingsRequestParser.TryParse(request, true, out var settings, out var error,
            currentSettings: current));
        Assert.Null(error);
        Assert.Equal(WorkloadConcurrencyMode.Manual, settings.WorkloadConcurrencyMode);
        Assert.Equal(1, settings.NonVideoSlots);
        Assert.Equal(3, settings.EvidenceValidationSlots);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(5, 2)]
    [InlineData(0, 0)]
    [InlineData(0, 5)]
    public void Unsafe_manual_lane_limits_are_rejected(int nonVideo, int evidence)
    {
        var request = ValidRequest() with { WorkloadConcurrencyMode = "Manual",
            NonVideoSlots = nonVideo, EvidenceValidationSlots = evidence };
        Assert.False(SettingsRequestParser.TryParse(request, true, out _, out _));
    }
    [Fact]
    public void A_client_from_before_the_tone_map_setting_keeps_the_software_default()
    {
        var request = ValidRequest() with { HdrToneMapMode = null };

        var parsed = SettingsRequestParser.TryParse(request, remoteWorkersAvailable: true, out var settings, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(HdrToneMapMode.Software, settings.HdrToneMapMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_older_client_omitting_worker_verification_keeps_the_saved_choice(bool savedChoice)
    {
        var request = ValidRequest() with { WorkerVerificationRequired = null };

        var parsed = SettingsRequestParser.TryParse(request, remoteWorkersAvailable: true,
            out var settings, out var error, currentWorkerVerificationRequired: savedChoice);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(savedChoice, settings.WorkerVerificationRequired);
    }

    [Theory]
    [InlineData("Magic")]
    [InlineData("999")]
    public void An_unknown_tone_map_mode_is_rejected_before_it_reaches_ffmpeg(string value)
    {
        var request = ValidRequest() with { HdrToneMapMode = value };

        var parsed = SettingsRequestParser.TryParse(request, remoteWorkersAvailable: true, out _, out var error);

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
