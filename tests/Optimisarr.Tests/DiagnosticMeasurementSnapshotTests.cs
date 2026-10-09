using System.Text.Json;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Core.Workers;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

public sealed class DiagnosticMeasurementSnapshotTests
{
    [Fact]
    public void Process_summary_copies_only_canonical_messages_and_bounds_input()
    {
        var summary = DiagnosticProcessSummary.Read(new string('x', 70000) + "\n/private/token-file: Non-monotonous DTS; Bearer private-token\nConversion failed!")!;
        Assert.True(summary.Truncated); Assert.Equal(65536, summary.CharactersInspected);
        Assert.Contains("Non-monotonous DTS", summary.KnownMessages);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(summary));
        var poisoned = summary with { KnownMessages = ["Bearer private-token", "Conversion failed!"] };
        Assert.Equal(["Conversion failed!"], DiagnosticProcessSummary.Sanitize(poisoned).KnownMessages);
    }
    [Fact]
    public void Frozen_probe_and_packet_measurements_omit_paths_tags_and_process_errors()
    {
        const string probe = """{"streams":[{"codec_type":"video","codec_name":"h264","width":1280,"height":720,"tags":{"title":"private-token"}}],"format":{"filename":"/private-token/media.mkv","duration":"16"}}""";
        var contract = new RemoteVerificationContract(1, Guid.NewGuid(), false);
        var evidence = new RemoteVerificationEvidence(contract.Id, new string('a',64), new string('b',64), probe, probe,
            Decode: new(true, null, 0), SourceVideo: new(true, 0, null, 16, 400), CandidateVideo: new(true, 0, null, 16, 400),
            Error: "Bearer private-token", SourceDecodedFrameCount: 400, CandidateDecodedFrameCount: 400);
        var snapshot = DiagnosticExecutionEvidence.Read(JsonSerializer.Serialize(contract), JsonSerializer.Serialize(evidence))!;
        Assert.True(snapshot.MatchesContract); Assert.Equal(1280, snapshot.SourceProbe!.Width); Assert.Equal(400, snapshot.SourceVideo!.PacketCount);
        Assert.Equal(400, snapshot.CandidateDecodedFrameCount); Assert.True(snapshot.ErrorPresent);
        Assert.DoesNotContain("private-token", JsonSerializer.Serialize(snapshot));
        Assert.Null(DiagnosticExecutionEvidence.Read("{}", "not json"));
        Assert.Null(DiagnosticExecutionEvidence.Read(null, new string('x', 1_000_001)));
    }
    [Theory]
    [InlineData(false, true, true, 1, 0, 0, 0, DiagnosticSchedulingReason.ManualPause)]
    [InlineData(false, false, true, 1, 0, 0, 0, DiagnosticSchedulingReason.MediaActivity)]
    [InlineData(false, false, false, 1, 0, 0, 0, DiagnosticSchedulingReason.LowDiskSpace)]
    [InlineData(true, false, false, 0, 0, 0, 0, DiagnosticSchedulingReason.Idle)]
    [InlineData(true, false, false, 1, 0, 0, 0, DiagnosticSchedulingReason.LibraryWindow)]
    [InlineData(true, false, false, 1, 1, 0, 0, DiagnosticSchedulingReason.WorkerPlacement)]
    [InlineData(true, false, false, 1, 1, 1, 0, DiagnosticSchedulingReason.ConcurrencyOrLane)]
    [InlineData(true, false, false, 1, 1, 1, 1, DiagnosticSchedulingReason.Dispatching)]
    public void Scheduling_reason_identifies_the_gate_without_human_text(bool canStart, bool paused, bool activity,
        int queued, int window, int runnable, int selected, DiagnosticSchedulingReason reason) =>
        Assert.Equal(reason, DiagnosticSchedulingSnapshot.Create(canStart, paused, activity, queued, window, runnable, selected, true, true).Reason);
}
