using System.Security.Cryptography;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class PerceptualCapabilityProberTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "native-capability-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _encoders = [];
    private static readonly string Tool = new('c', 64);
    public PerceptualCapabilityProberTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private PerceptualCapabilityOperations Operations() => new(
        (encoder, _, _, _) => { _encoders.Add(encoder); return Task.FromResult(encoder == "mjpeg"); },
        (_, _, _) => Task.FromResult(true),
        (path, _) => Task.FromResult<RemoteImagePerceptualEvidence?>(RemoteImagePerceptualEvidence.From(
            new(new(32, 32, false, 0, 100, [100]), null, Hash(path), Hash(path), Tool, 1))),
        (path, _) => Task.FromResult<RemoteAudioQualityEvidence?>(RemoteAudioQualityEvidence.From(new(true, null,
            new(1, 2, 48000, "stereo"), new(1, 2, 48000, "stereo"), Hash(path), Hash(path), Tool, Tool, Tool,
            [new(new(0, 1), new(48000, [0, 0]))], 1))));

    private PerceptualCapabilityProber Prober(PerceptualCapabilityOperations? operations = null) =>
        new(new("fixture-encoder", "fixture-probe", "fixture-audio", "fixture-image", "fixture-metadata"), operations ?? Operations());

    [Fact]
    public async Task Capability_qualification_owns_its_workspace_and_recovers_old_stopped_probes()
    {
        var abandoned = NativeScratchWorkspace.CreateCapability(_root);
        var old = abandoned.DirectoryPath; abandoned.Dispose();
        var ops = Operations(); var encoder = ops.Encoder;
        ops = ops with { Encoder = (name, source, candidate, token) =>
        {
            Assert.False(Directory.Exists(old));
            Assert.Equal(1, NativeScratchWorkspace.Recover(_root).Busy);
            return encoder(name, source, candidate, token);
        } };
        var proof = await Prober(ops).ProbeAsync(_root, default);
        Assert.NotNull(proof.ImageMetricRevision);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName), "owner-*.json"));
    }

    [Fact]
    public async Task Proof_requires_real_successful_encoder_round_trips_and_both_pinned_self_assessments()
    {
        var proof = await Prober().ProbeAsync(_root, default);
        Assert.Equal(["mjpeg", "libwebp"], _encoders);
        Assert.Equal(["mjpeg"], proof.ImageEncoders);
        Assert.True(proof.ImageMetadataSupported);
        Assert.Equal(ImagePerceptualResultParser.Revision, proof.ImageMetricRevision);
        Assert.Equal(AudioQualityResultParser.Revision, proof.AudioMetricRevision);
        Assert.Equal([NativeScratchWorkspace.ControlDirectoryName], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.Equal(["root.lock"], Directory.GetFileSystemEntries(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName)).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("image-version")]
    [InlineData("image-identity")]
    [InlineData("audio-version")]
    [InlineData("audio-coverage")]
    [InlineData("metadata")]
    public async Task Incomplete_mismatched_or_missing_tool_evidence_is_not_advertised(string missing)
    {
        var ops = Operations();
        if (missing.StartsWith("image"))
        {
            var original = ops.Image;
            ops = ops with { Image = async (path, token) =>
            {
                var evidence = (await original(path, token))!;
                return missing == "image-version" ? evidence with { Revision = "old" }
                    : evidence with { Assessment = evidence.Assessment with { CandidateSha256 = Tool } };
            } };
        }
        if (missing.StartsWith("audio"))
        {
            var original = ops.Audio;
            ops = ops with { Audio = async (path, token) =>
            {
                var evidence = (await original(path, token))!;
                return missing == "audio-version" ? evidence with { Revision = "old" }
                    : evidence with { Assessment = evidence.Assessment with { Windows = [new(new(0, 1), new(47999, [0, 0]))] } };
            } };
        }
        if (missing == "metadata") ops = ops with { Metadata = (_, _, _) => Task.FromResult(false) };
        var proof = await Prober(ops).ProbeAsync(_root, default);
        if (missing.StartsWith("image")) { Assert.Null(proof.ImageMetricRevision); Assert.NotNull(proof.AudioMetricRevision); }
        if (missing.StartsWith("audio")) { Assert.Null(proof.AudioMetricRevision); Assert.NotNull(proof.ImageMetricRevision); }
        if (missing == "metadata") Assert.False(proof.ImageMetadataSupported);
        Assert.Equal([NativeScratchWorkspace.ControlDirectoryName], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.Equal(["root.lock"], Directory.GetFileSystemEntries(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task One_unavailable_module_does_not_inherit_old_proof_or_disable_other_qualified_tools()
    {
        var proof = await Prober(Operations() with { Image = (_, _) => throw new IOException("missing module") }).ProbeAsync(_root, default);
        Assert.Null(proof.ImageMetricRevision); Assert.NotNull(proof.AudioMetricRevision);
        Assert.True(proof.ImageMetadataSupported);
        Assert.Equal([NativeScratchWorkspace.ControlDirectoryName], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.Equal(["root.lock"], Directory.GetFileSystemEntries(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cancellation_finishes_the_current_probe_before_removing_its_owned_files()
    {
        using var cancellation = new CancellationTokenSource();
        var ops = Operations() with { Image = async (path, token) =>
        {
            cancellation.Cancel();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Assert.True(File.Exists(path)); }
            return null;
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prober(ops).ProbeAsync(_root, cancellation.Token));
        Assert.Equal([NativeScratchWorkspace.ControlDirectoryName], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
        Assert.Equal(["root.lock"], Directory.GetFileSystemEntries(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName)).Select(Path.GetFileName));
    }
}
