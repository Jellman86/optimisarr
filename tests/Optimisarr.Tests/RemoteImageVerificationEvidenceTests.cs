using Optimisarr.Core.Domain;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

public sealed class RemoteImageVerificationEvidenceTests
{
    private static readonly string Source = new('a', 64), Candidate = new('b', 64), Tool = new('c', 64);
    private const string Probe = """{"streams":[{"codec_type":"video","codec_name":"png","width":32,"height":32,"pix_fmt":"rgb24"}],"format":{}}""";
    private static readonly RemoteVerificationContract Contract = new(4, Guid.NewGuid(), false, Image: new(true, true, true));
    private static RemoteImagePerceptualEvidence Metric(string candidate, double score = 90) => RemoteImagePerceptualEvidence.From(
        new(new(32, 32, false, 0, score, [score]), null, Source, candidate, Tool, 1, MeasurementLocation: "Worker"));
    private static RemoteVerificationEvidence Valid() => new(Contract.Id, Source, Candidate, Probe, Probe, DecodeHealthResult.Ok,
        Image: new(Metric(Source, 100), Metric(Candidate), ImageQualityResult.Ok(0.95),
            ImageMetadataResult.Ok(new(true, true)), ImageMetadataResult.Ok(new(true, true)), Tool, Tool, Tool));

    [Fact]
    public void Still_image_contract_uses_bounded_native_source_and_image_measurements_without_packet_timelines()
    {
        Assert.Equal(MediaKind.Image, Contract.Kind);
        Assert.Empty(RemoteVerificationEvidenceValidator.Validate(Contract, Valid(), Source, Candidate));
    }

    [Fact]
    public void Required_image_measurements_and_native_identities_cannot_be_omitted_or_substituted()
    {
        var evidence = Valid();
        foreach (var image in new[] { evidence.Image! with { Source = null }, evidence.Image! with { Perceptual = null },
            evidence.Image! with { Ssim = null }, evidence.Image! with { Ssim = ImageQualityResult.Ok(double.NaN) },
            evidence.Image! with { SourceMetadata = null }, evidence.Image! with { CandidateMetadata = ImageMetadataResult.Failed("missing exiftool") },
            evidence.Image! with { EncoderSha256 = "unknown" }, evidence.Image! with { MetadataToolSha256 = null },
            evidence.Image! with { Perceptual = Metric(Candidate) with { Revision = "older-build" } },
            evidence.Image! with { Perceptual = Metric(Source) },
            evidence.Image! with { Source = Metric(Candidate, 100) } })
            Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence with { Image = image }, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence with { Image = null }, Source, Candidate));
    }

    [Fact]
    public void Negative_quality_and_metadata_measurements_are_retained_for_the_servers_final_verdict()
    {
        var evidence = Valid();
        Assert.Empty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence with { Image = evidence.Image! with
        { Perceptual = Metric(Candidate, 10), Ssim = ImageQualityResult.Ok(0.2), CandidateMetadata = ImageMetadataResult.Ok(new(false, false)) } }, Source, Candidate));
    }

    [Fact]
    public void A_video_contract_cannot_relabel_image_evidence_and_a_still_contract_cannot_request_audio_or_picture_counts()
    {
        foreach (var contract in new[] { Contract with { Version = 1 }, Contract with { Image = null },
            Contract with { MeasureAudio = true }, Contract with { CountVideoFrames = true }, Contract with { MeasureAudioQuality = true } })
            Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(contract, Valid(), Source, Candidate));
    }

    [Fact]
    public void Source_dimensions_native_tool_and_profile_are_bound_to_the_original_probe()
    {
        var evidence = Valid();
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract,
            evidence with { SourceProbe = Probe.Replace("32", "64") }, Source, Candidate));
        Assert.NotEmpty(RemoteVerificationEvidenceValidator.Validate(Contract, evidence with { Image = evidence.Image! with
            { Perceptual = Metric(Candidate) with { Assessment = Metric(Candidate).Assessment with { MetricSha256 = Candidate } } } }, Source, Candidate));
    }
}
