using System.Net;
using System.Text;
using System.Text.Json;
using Optimisarr.Sidecar.Core.Capabilities;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// The pairing and check-in conversation, driven against a scripted server rather than a live one.
/// What is pinned here is the wire shape and the handling of each refusal, because the other end is
/// versioned separately: a field quietly renamed on this side is a worker the server records
/// nothing about, and a refusal mishandled is either a loop or a machine that gives up for good.
/// </summary>
public sealed class SidecarClientTests
{
    private sealed class ScriptedHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static SidecarCapabilities Capabilities() => new(
        Name: "PICARD",
        OperatingSystem: "windows",
        Architecture: "x64",
        VideoEncoders: ["hevc_nvenc", "libx265"],
        AudioEncoders: ["aac"],
        HardwareDecoders: ["hevc_cuvid"],
        Vmaf: VmafCapability.Cuda,
        FreeScratchBytes: 500L * 1024 * 1024 * 1024,
        MaxConcurrency: 1);

    private static (SidecarClient Client, ScriptedHandler Handler) Client(
        HttpStatusCode status, string json)
    {
        var handler = new ScriptedHandler(status, json);
        return (new SidecarClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task Pairing_sends_what_the_machine_proved_and_which_build_is_running()
    {
        var (client, handler) = Client(
            HttpStatusCode.OK,
            """{"workerId":7,"credential":"secret","protocolVersion":1}""");

        var result = await client.PairAsync("https://optimisarr.example.com", "123 456", Capabilities());

        Assert.Equal(7, result.WorkerId);
        Assert.Equal("secret", result.Credential);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var body = sent.RootElement;
        // Named, not numbered: the two ends are versioned apart, so an ordinal would change meaning
        // the moment the enum gained a member — and this value gates what may be offered.
        Assert.Equal("Cuda", body.GetProperty("vmaf").GetString());
        Assert.Equal("windows", body.GetProperty("operatingSystem").GetString());
        Assert.Equal("hevc_nvenc", body.GetProperty("videoEncoders")[0].GetString());
        Assert.Equal("aac", body.GetProperty("audioEncoders")[0].GetString());
        // The PIN goes as typed, spaces and all: the server tolerates the grouping people read out.
        Assert.Equal("123 456", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("sidecarVersion").GetString()));
    }

    [Fact]
    public async Task A_refused_pairing_code_is_not_worth_retrying_and_says_why()
    {
        var (client, _) = Client(
            HttpStatusCode.Unauthorized,
            """{"code":"worker.pairing.expired","error":"That pairing code has expired."}""");

        var thrown = await Assert.ThrowsAsync<SidecarException>(
            () => client.PairAsync("https://optimisarr.example.com", "000000", Capabilities()));

        // The server's own sentence, not one invented here, so what the operator reads matches what
        // the server would have told them.
        Assert.Equal("That pairing code has expired.", thrown.Message);
        Assert.False(thrown.Recoverable);
    }

    [Fact]
    public async Task Remote_workers_being_switched_off_is_worth_waiting_out()
    {
        var (client, _) = Client(
            HttpStatusCode.Forbidden,
            """{"code":"worker.disabled","error":"Remote workers are turned off."}""");

        var thrown = await Assert.ThrowsAsync<SidecarException>(
            () => client.PairAsync("https://optimisarr.example.com", "123456", Capabilities()));

        // Nothing is wrong with this machine; an operator may turn the feature on at any moment.
        Assert.True(thrown.Recoverable);
    }

    [Fact]
    public async Task A_check_in_carries_the_credential_the_capabilities_and_the_load()
    {
        var (client, handler) = Client(
            HttpStatusCode.OK,
            """{"workerId":7,"protocolVersion":1,"serverTimeUtc":"2026-09-14T20:00:00Z","heartbeatIntervalSeconds":30,"draining":false}""");

        var result = await client.HeartbeatAsync(
            new StoredPairing("https://optimisarr.example.com", "secret", 7),
            Capabilities(),
            new MachineLoad(CpuBusyFraction: 0.42, GpuBusyFraction: 0.87));

        Assert.Equal(TimeSpan.FromSeconds(30), result.Interval);
        Assert.False(result.Draining);
        Assert.Equal("Bearer secret", handler.LastRequest!.Headers.Authorization!.ToString());

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(0.42, sent.RootElement.GetProperty("cpuBusyFraction").GetDouble());
        Assert.Equal(0.87, sent.RootElement.GetProperty("gpuBusyFraction").GetDouble());
    }

    [Fact]
    public async Task Load_that_could_not_be_measured_is_left_out_rather_than_sent_as_zero()
    {
        var (client, handler) = Client(
            HttpStatusCode.OK,
            """{"workerId":7,"protocolVersion":1,"serverTimeUtc":"2026-09-14T20:00:00Z","heartbeatIntervalSeconds":30,"draining":false}""");

        await client.HeartbeatAsync(
            new StoredPairing("https://optimisarr.example.com", "secret", 7),
            Capabilities(),
            new MachineLoad(CpuBusyFraction: 0.5, GpuBusyFraction: null));

        using var sent = JsonDocument.Parse(handler.LastBody!);
        // A zero would be drawn as an idle accelerator; an absent field leaves the server showing
        // the last figure it was actually told.
        Assert.False(sent.RootElement.TryGetProperty("gpuBusyFraction", out _));
        Assert.Equal(0.5, sent.RootElement.GetProperty("cpuBusyFraction").GetDouble());
    }

    [Fact]
    public async Task A_revoked_credential_stops_the_service_rather_than_looping()
    {
        var (client, _) = Client(HttpStatusCode.Unauthorized, """{"error":"Unknown or revoked worker credential."}""");

        var thrown = await Assert.ThrowsAsync<SidecarException>(
            () => client.HeartbeatAsync(
                new StoredPairing("https://optimisarr.example.com", "dead", 7), Capabilities()));

        // Retrying cannot recover a revoked credential; only pairing again can, and that needs a
        // person. Beating on regardless would be an infinite loop of 401s.
        Assert.False(thrown.Recoverable);
    }

    [Theory]
    [InlineData("optimisarr.example.com", "http://optimisarr.example.com/api/workers/pair")]
    [InlineData("https://optimisarr.example.com", "https://optimisarr.example.com/api/workers/pair")]
    [InlineData("https://optimisarr.example.com/", "https://optimisarr.example.com/api/workers/pair")]
    [InlineData("192.168.1.10:8787", "http://192.168.1.10:8787/api/workers/pair")]
    public void An_address_without_a_scheme_is_reached_over_http(string given, string expected)
    {
        // Right for a server on a home network, wrong for one behind a TLS proxy — where it fails
        // with nothing to suggest the scheme is the problem. Callers name the address on failure.
        Assert.Equal(expected, SidecarClient.Endpoint(given, "/api/workers/pair").ToString());
    }

    [Fact]
    public void An_empty_address_is_refused_before_any_request_is_made()
    {
        Assert.Throws<SidecarException>(() => SidecarClient.Endpoint("   ", "/api/workers/pair"));
    }

    [Fact]
    public async Task A_measurement_reports_its_bytes_per_window_and_their_total()
    {
        // The server compares each window with the source's own bytes over the same scenes. The
        // total stays for servers that predate the split; the two must agree or it is refused.
        var (client, handler) = Client(
            HttpStatusCode.OK, "{\"nextStep\":null,\"selectedQuality\":24,\"reason\":\"done\"}");

        await client.ReportAdaptiveProbeAsync(
            new StoredPairing("https://optimisarr.example.com", "secret", 7),
            Guid.NewGuid(), 24, [100, 110, 120], ["{}", "{}", "{}"]);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(330, body.RootElement.GetProperty("encodedBytes").GetInt64());
        Assert.Equal(
            [100L, 110L, 120L],
            body.RootElement.GetProperty("windowEncodedBytes").EnumerateArray().Select(e => e.GetInt64()));
    }
}

/// <summary>
/// The shapes the server actually sends. A model that cannot hold one of them is not a cosmetic
/// problem: the claim throws, the job never starts, and the lease lapses in silence two minutes
/// later — which looks from the server like a worker that went quiet rather than one that refused.
/// </summary>
public sealed class AssignmentShapeTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void An_assignment_carrying_measurement_commands_can_be_read()
    {
        // Each command is a full argument list, so the field is a list of lists. It was a flat list
        // of strings until the quality search needed to run them, and any job whose library had a
        // VMAF gate could not be deserialised at all.
        const string json = """
        {
          "leaseId": "8b1e2c3d-0000-4000-8000-000000000001",
          "jobId": 12,
          "title": "The Dinosaurs",
          "sourceBytes": 4096,
          "videoEncoder": "hevc_nvenc",
          "vmaf": "Cpu",
          "expiresUtc": "2026-09-15T09:00:00Z",
          "renewWithinSeconds": 120,
          "arguments": ["-i", "{{input}}", "{{output}}.mkv"],
          "outputExtension": ".mkv",
          "quality": {
            "measure": true,
            "model": "vmaf_v0.6.1",
            "frameSubsample": 1,
            "clipVmaf": true,
            "minimumHarmonicMean": 93,
            "minimumMinimum": 80,
            "commands": [
              ["-i", "{{distorted}}", "-i", "{{reference}}", "-lavfi", "libvmaf=log_path={{log}}", "-f", "null", "-"],
              ["-i", "{{distorted}}", "-i", "{{reference}}", "-lavfi", "libvmaf=log_path={{log}}", "-f", "null", "-"]
            ]
          }
        }
        """;

        var assignment = JsonSerializer.Deserialize<Assignment>(json, Web);

        Assert.NotNull(assignment);
        Assert.Equal(2, assignment!.Quality.Commands.Count);
        Assert.Contains("{{distorted}}", assignment.Quality.Commands[0]);
    }

    [Fact]
    public void An_assignment_carrying_a_quality_search_can_be_read()
    {
        const string json = """
        {
          "leaseId": "8b1e2c3d-0000-4000-8000-000000000001",
          "jobId": 12,
          "title": "The Dinosaurs",
          "sourceBytes": 4096,
          "videoEncoder": "hevc_nvenc",
          "vmaf": "Cpu",
          "expiresUtc": "2026-09-15T09:00:00Z",
          "renewWithinSeconds": 120,
          "arguments": ["-i", "{{input}}", "{{output}}.mkv"],
          "outputExtension": ".mkv",
          "quality": {
            "measure": false, "model": "", "frameSubsample": 1, "clipVmaf": false,
            "minimumHarmonicMean": 0, "minimumMinimum": 0, "commands": []
          },
          "search": {
            "quality": 24,
            "sampleCommands": [["-i", "{{input}}", "{{output}}.mkv"]],
            "measurement": {
              "measure": true, "model": "vmaf_v0.6.1", "frameSubsample": 1, "clipVmaf": true,
              "minimumHarmonicMean": 93, "minimumMinimum": 80,
              "commands": [["-i", "{{distorted}}", "-f", "null", "-"]]
            }
          }
        }
        """;

        var assignment = JsonSerializer.Deserialize<Assignment>(json, Web);

        Assert.Equal(24, assignment!.Search!.Quality);
        Assert.Single(assignment.Search.SampleCommands);
        Assert.Single(assignment.Search.Measurement.Commands);
    }

    [Fact]
    public void An_assignment_without_a_search_still_reads_as_one_to_encode_straight_away()
    {
        // What every settled job sends, and what a server predating the search sends.
        const string json = """
        {
          "leaseId": "8b1e2c3d-0000-4000-8000-000000000001", "jobId": 12, "title": "x",
          "sourceBytes": 4096, "videoEncoder": "hevc_nvenc", "vmaf": "None",
          "expiresUtc": "2026-09-15T09:00:00Z", "renewWithinSeconds": 120,
          "arguments": ["-i", "{{input}}", "{{output}}.mkv"], "outputExtension": ".mkv",
          "quality": {
            "measure": false, "model": "", "frameSubsample": 1, "clipVmaf": false,
            "minimumHarmonicMean": 0, "minimumMinimum": 0, "commands": []
          }
        }
        """;

        Assert.Null(JsonSerializer.Deserialize<Assignment>(json, Web)!.Search);
    }
}
