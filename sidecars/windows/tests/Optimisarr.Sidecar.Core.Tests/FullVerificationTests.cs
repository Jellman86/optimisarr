using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;
using System.Text.Json;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class FullVerificationTests
{
    [Fact]
    public async Task A_missing_media_tool_returns_bound_failure_evidence_without_claiming_a_pass()
    {
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), true, CountVideoFrames: true);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ffmpeg");
        var evidence = await FullVerification.MeasureAsync(missing, "source", "candidate", contract,
            new string('a', 64), new string('b', 64), CancellationToken.None);
        Assert.Equal(contract.Id, evidence.ContractId);
        Assert.NotNull(evidence.Error);
        Assert.Null(evidence.Decode);
        Assert.Null(evidence.SourceDecodedFrameCount);
        Assert.Null(evidence.CandidateDecodedFrameCount);
    }

    [Fact]
    public void The_wire_contract_keeps_strict_verification_optional_for_older_servers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), true);
        var copy = JsonSerializer.Deserialize<RemoteVerificationContract>(JsonSerializer.Serialize(contract, options), options);
        Assert.Equal(contract, copy);
        Assert.Equal(1, Optimisarr.Sidecar.Core.Session.WorkerProtocol.Minimum);
        Assert.Equal(Optimisarr.Core.Workers.WorkerProtocol.Current,
            Optimisarr.Sidecar.Core.Session.WorkerProtocol.Maximum);
    }
    [Theory]
    [InlineData(false, false, 2, null)]
    [InlineData(false, true, 2, "Audio jobs cannot request video quality measurements or searches.")]
    [InlineData(true, false, 2, "Audio jobs cannot request video quality measurements or searches.")]
    [InlineData(false, false, 1, "The verification contract does not match the media kind.")]
    public void Audio_assignment_rejects_video_verification_before_reading_media(bool vmaf, bool search, int version, string? reason)
    {
        var quality = new QualityRequirement(vmaf, "vmaf_v0.6.1", 1, false, 0, 0, []);
        var assignment = new Assignment(Guid.NewGuid(), 1, "Audio", 4096, null, "None", DateTimeOffset.UtcNow,
            30, [], "opus", quality, Search: search ? new(24, [], quality) : null, FullVerification: new(version, Guid.NewGuid(), true),
            Kind: Optimisarr.Core.Domain.MediaKind.Audio, AudioEncoder: "libopus");
        Assert.Equal(reason, assignment.RefuseMediaContract());
    }

}
