using System.Security.Cryptography;
using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;
using Optimisarr.Sidecar.Core.Session;
using WorkerProtocol = Optimisarr.Core.Workers.WorkerProtocol;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class PerceptualSearchSessionTests
{
    private static readonly string Source = new('a', 64), Tool = new('c', 64);
    private static readonly TranscodeSpec Spec = new(WorkerProtocol.InputPlaceholder, WorkerProtocol.OutputPlaceholder + ".webp",
        null, null, null, false, Kind: MediaKind.Image, ImageEncoder: "libwebp", ImageQuality: 80);
    private readonly RemotePerceptualSearchContract _contract = new(Guid.NewGuid(), MediaKind.Image, "libwebp",
        PerceptualQualityPlan.Image("libwebp", 80), VerificationPolicy.Default with { ImagePerceptualGateEnabled = true,
            MinimumImagePerceptualScore = 85, ImageMetadataGateEnabled = false, ImageQualityGateEnabled = false }, [], SourceImage: new(32, 32, false));
    private readonly IReadOnlyList<string> _arguments = FfmpegCommandBuilder.Build(Spec, optimisedMarker: "claim-release");
    private RemotePerceptualSearchState? _server;
    private int _measurements, _reports;
    private RemotePerceptualSearchStep First => RemotePerceptualSearchPlanner.Plan(_contract, 80, 0, Spec);

    private Task<RemotePerceptualProbeReport> Measure(RemotePerceptualSearchStep step, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _measurements++;
        var candidate = step.Value.ToString("x64");
        RemoteImagePerceptualEvidence Image(string hash, double score) => RemoteImagePerceptualEvidence.From(
            new(new(32, 32, false, 0, score, [score]), null, Source, hash, Tool, 1));
        return Task.FromResult(new RemotePerceptualProbeReport(_contract.Id, "libwebp", step.Value, Source,
            new(Image: Image(Source, 100)), [new(0, 1000 + step.Value * 10, Source, candidate,
                Image: Image(candidate, step.Value >= 60 ? 90 : 70))], EncoderSha256: Tool));
    }
    private Task<PerceptualSearchDirection> Report(RemotePerceptualProbeReport report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); _reports++;
        _server ??= RemotePerceptualSearchState.Start(_contract);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(report, SidecarClient.Json)));
        var advanced = RemotePerceptualSearchCoordinator.Advance(_server, Source, report, digest);
        Assert.True(advanced.Accepted, advanced.Error); _server = advanced.State!;
        return Task.FromResult(_server.SourceRejected ? new PerceptualSearchDirection(null, null, null, true, _server.SourceError)
            : _server.AskedValue is { } asked ? new(RemotePerceptualSearchPlanner.Plan(_contract, asked, _server.Probes.Count, Spec), null, null, false, null)
            : new(null, _server.Decision!.SelectedValue, FrozenPerceptualEncoding.SelectArguments(_contract, _arguments, _server.Decision.SelectedValue), false, "settled"));
    }

    [Fact]
    public async Task Four_native_measurements_return_only_the_settled_command_and_preserve_claim_intent()
    {
        var result = await PerceptualSearchSession.RunAsync(First, _arguments, Source, Measure, Report, default);
        Assert.Equal(4, _measurements); Assert.Equal(4, _reports); Assert.False(result.SourceBlocked);
        Assert.Equal(62, result.SelectedValue);
        Assert.Equal(FrozenPerceptualEncoding.SelectArguments(_contract, _arguments, 62), result.Arguments);
    }

    [Fact]
    public async Task A_committed_report_with_a_lost_response_is_replayed_without_measuring_again()
    {
        var retries = 0;
        var result = await PerceptualSearchSession.RunAsync(First, _arguments, Source, Measure, async (report, token) =>
        {
            var response = await Report(report, token);
            if (_reports == 1) throw new SidecarException("Response lost after commit", true);
            return response;
        }, default, (_, _) => { retries++; return Task.CompletedTask; });
        Assert.False(result.SourceBlocked); Assert.Equal(4, _measurements); Assert.Equal(5, _reports); Assert.Equal(1, retries);
        Assert.Equal(4, _server!.Probes.Count);
    }

    [Theory]
    [InlineData("premature")]
    [InlineData("contract")]
    [InlineData("repeated-step")]
    [InlineData("command")]
    [InlineData("sample-command")]
    public async Task Changed_or_premature_directions_cannot_authorise_an_encode(string change)
    {
        await Assert.ThrowsAsync<SidecarException>(() => PerceptualSearchSession.RunAsync(First, _arguments, Source, Measure, async (report, token) =>
        {
            var direction = await Report(report, token);
            if (change == "premature") return new(null, 80, _arguments, false, "encode now");
            if (direction.NextStep is { } step)
            {
                if (change == "contract") return direction with { NextStep = step with { Contract = step.Contract with
                    { Policy = step.Contract.Policy with { MinimumImagePerceptualScore = 10 } } } };
                if (change == "repeated-step") return direction with { NextStep = First };
                if (change == "sample-command") return direction with { NextStep = step with
                    { SampleCommands = [["-i", "/another/private/source", "{{output}}.webp"]] } };
            }
            if (change == "command" && direction.Arguments is { } args)
                return direction with { Arguments = args.Select(arg => arg == "claim-release" ? "changed-release" : arg == "libwebp" ? "mjpeg" : arg).ToArray() };
            return direction;
        }, default));
        Assert.True(_measurements <= 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_refusal_is_reported_but_never_overridden_by_an_encode_directive(bool overrideRefusal)
    {
        async Task<RemotePerceptualProbeReport> Refused(RemotePerceptualSearchStep step, CancellationToken token) =>
            (await Measure(step, token)) with { Source = new(Error: "unsupported original"), Samples = [] };
        Task<PerceptualSearchDirection> Answer(RemotePerceptualProbeReport report, CancellationToken token) =>
            overrideRefusal ? Task.FromResult(new PerceptualSearchDirection(null, 80, _arguments, false, "encode anyway")) : Report(report, token);
        if (overrideRefusal)
            await Assert.ThrowsAsync<SidecarException>(() => PerceptualSearchSession.RunAsync(First, _arguments, Source, Refused, Answer, default));
        else
        {
            var result = await PerceptualSearchSession.RunAsync(First, _arguments, Source, Refused, Answer, default);
            Assert.True(result.SourceBlocked); Assert.Null(result.Arguments); Assert.Null(result.SelectedValue); Assert.Equal(1, _reports);
        }
        Assert.Equal(1, _measurements);
    }

    [Fact]
    public async Task A_persistent_outage_has_bounded_replays_and_does_not_encode_at_an_unconfirmed_setting()
    {
        var error = await Assert.ThrowsAsync<SidecarException>(() => PerceptualSearchSession.RunAsync(First, _arguments, Source, Measure,
            (_, _) => { _reports++; throw new SidecarException("Restarting", true); }, default, (_, _) => Task.CompletedTask));
        Assert.True(error.Recoverable); Assert.Equal(5, _reports); Assert.Equal(1, _measurements);
    }

    [Fact]
    public async Task Cancellation_during_report_replay_propagates_without_another_measurement_or_fallback_encode()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PerceptualSearchSession.RunAsync(First, _arguments, Source, Measure,
            (_, _) => { _reports++; throw new SidecarException("Restarting", true); }, cancellation.Token,
            (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }));
        Assert.Equal(1, _reports); Assert.Equal(1, _measurements);
    }

    [Theory]
    [InlineData("source-hash")]
    [InlineData("encoder")]
    [InlineData("missing-contract")]
    public async Task Invalid_claim_intent_is_refused_before_any_native_measurement(string invalid)
    {
        var first = invalid == "missing-contract" ? First with { Contract = null! } : First;
        var arguments = invalid == "encoder" ? _arguments.Select(arg => arg == "libwebp" ? "mjpeg" : arg).ToArray() : _arguments;
        await Assert.ThrowsAsync<SidecarException>(() => PerceptualSearchSession.RunAsync(first, arguments,
            invalid == "source-hash" ? "unknown" : Source, Measure, Report, default));
        Assert.Equal(0, _measurements); Assert.Equal(0, _reports);
    }
}
