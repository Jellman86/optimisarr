using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimisarr.Sidecar.Native;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class NativeHostProtocolTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static NativeHostRequest Capability() => new(1, "capabilities", Path.GetFullPath("ffmpeg"), Path.GetFullPath("scratch"));
    private static byte[] Serialize(NativeHostRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public async Task A_request_frame_is_complete_before_any_native_work_can_start()
    {
        var json = Serialize(Capability());
        using var stream = new MemoryStream(json.Concat(new byte[] { 10 }).ToArray());
        Assert.Equal(json, await NativeHostProtocol.ReadRequestAsync(stream, default));
        Assert.Equal(Capability(), NativeHostProtocol.Parse(json));
    }

    [Fact]
    public async Task Unterminated_or_oversized_control_frames_are_refused()
    {
        using var incomplete = new MemoryStream(Bytes("{}"));
        await Assert.ThrowsAsync<EndOfStreamException>(() => NativeHostProtocol.ReadRequestAsync(incomplete, default));
        using var oversized = new MemoryStream(new byte[NativeHostProtocol.MaximumRequestBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeHostProtocol.ReadRequestAsync(oversized, default));
    }

    [Fact]
    public async Task Extra_bytes_cannot_be_smuggled_into_a_second_control_request()
    {
        using var stream = new MemoryStream(Bytes("{}\n{}\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeHostProtocol.ReadRequestAsync(stream, default));
    }

    [Theory]
    [InlineData("{\"version\":1,\"version\":1}")]
    [InlineData("{\"pairing\":{\"credential\":\"one\",\"credential\":\"two\"}}")]
    [InlineData("{\"assignment\":{\"arguments\":[{\"key\":1,\"key\":2}]}}")]
    public void Ambiguous_fields_are_refused_at_every_depth(string text) =>
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Bytes(text)));

    [Theory]
    [InlineData(0, "capabilities")]
    [InlineData(2, "capabilities")]
    [InlineData(1, "shell")]
    public void Unknown_versions_and_operations_never_start_a_tool(int version, string mode) =>
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { Version = version, Mode = mode })));

    [Fact]
    public void Tool_and_working_paths_must_be_explicit_absolute_paths()
    {
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { FfmpegPath = "ffmpeg" })));
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { ScratchRoot = "work" })));
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { MetadataTool = "exiftool" })));
    }

    [Fact]
    public async Task Parent_EOF_cancels_the_owned_native_work()
    {
        using var work = new CancellationTokenSource();
        using var input = new MemoryStream();
        await NativeHostProtocol.WatchParentAsync(input, work, default);
        Assert.True(work.IsCancellationRequested);
    }

    [Fact]
    public async Task Unexpected_parent_data_after_the_initial_request_also_cancels_work()
    {
        using var work = new CancellationTokenSource();
        using var input = new MemoryStream(Bytes("not another command"));
        await NativeHostProtocol.WatchParentAsync(input, work, default);
        Assert.True(work.IsCancellationRequested);
    }

    [Fact]
    public async Task Cancellation_during_frame_reading_is_not_relabelled_as_a_bad_request()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var input = new MemoryStream(Bytes("{}\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeHostProtocol.ReadRequestAsync(input, cancellation.Token));
    }

    private static NativeHostRequest Run(bool image = true) => Capability() with
    {
        Mode = "run", GrantedMemoryBytes = 8L * 1024 * 1024 * 1024,
        Pairing = new("http://127.0.0.1:9999/optimisarr", new('a', 43), 1),
        Assignment = JsonSerializer.SerializeToElement(PerceptualJobFixture.Assignment(image), NativeHostProtocol.Json)
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Native_memory_requirements_are_resolved_without_credentials_network_or_media_tools(bool image)
    {
        var request = Run(image) with { Mode = "requirements", Pairing = null, GrantedMemoryBytes = null,
            FfmpegPath = Path.GetFullPath("missing-native-tool"), ScratchRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
        var parsed = NativeHostProtocol.Parse(Serialize(request));
        var first = await NativeHostExecutor.RunAsync(parsed, _ => Assert.Fail("No progress or media work is allowed"), default);
        var again = await NativeHostExecutor.RunAsync(parsed, _ => Assert.Fail("No progress or media work is allowed"), default);
        Assert.Equal(first, again);
        Assert.Equal("requirements", first.Type);
        Assert.Equal(request.Assignment!.Value.GetProperty("jobId").GetInt32(), first.JobId);
        Assert.Equal(image ? PerceptualJobRuntime.ImageMemoryBytes : PerceptualJobRuntime.AudioMemoryBytes, first.NativeMemoryBytes);
        Assert.Equal(request.Assignment.Value.GetProperty("requiredScratchBytes").GetInt64(), first.RequiredScratchBytes);
        Assert.False(Directory.Exists(request.ScratchRoot));
    }

    [Fact]
    public void Requirements_resolution_cannot_accept_a_pairing_secret_or_an_existing_grant()
    {
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Run() with { Mode = "requirements", GrantedMemoryBytes = null })));
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Run() with { Mode = "requirements", Pairing = null })));
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { Mode = "requirements" })));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_shared_native_audio_and_image_assignments_round_trip_without_a_second_contract_model(bool image)
    {
        var request = Run(image);
        var parsed = NativeHostProtocol.Parse(Serialize(request));
        Assert.Equal(request.Pairing, parsed.Pairing);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(request.Assignment!.Value.GetRawText()), JsonNode.Parse(parsed.Assignment!.Value.GetRawText())));
    }

    [Theory]
    [InlineData("file:///tmp/other")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com/?credential=secret")]
    [InlineData("https://example.com/#fragment")]
    public void Non_HTTP_or_credential_bearing_server_locations_are_refused(string address) =>
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Run() with { Pairing = Run().Pairing! with { ServerAddress = address } })));

    [Fact]
    public void Discovery_cannot_carry_a_pairing_secret_or_a_job()
    {
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { Pairing = Run().Pairing })));
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Capability() with { Assignment = Run().Assignment })));
    }

    [Fact]
    public void A_missing_allocation_or_native_search_is_refused_before_network_or_tool_work()
    {
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Run() with { GrantedMemoryBytes = null })));
        var assignment = PerceptualJobFixture.Assignment(true) with { PerceptualSearch = null };
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Serialize(Run() with
            { Assignment = JsonSerializer.SerializeToElement(assignment, NativeHostProtocol.Json) })));
    }

    [Fact]
    public async Task Completing_owned_work_stops_parent_monitoring_without_reporting_parent_death()
    {
        using var work = new CancellationTokenSource();
        using var done = new CancellationTokenSource(); done.Cancel();
        using var input = new MemoryStream();
        await NativeHostProtocol.WatchParentAsync(input, work, done.Token);
        Assert.False(work.IsCancellationRequested);
    }


    [Fact]
    public void Case_aliases_of_a_valid_property_cannot_bypass_duplicate_field_detection()
    {
        var request = Encoding.UTF8.GetString(Serialize(Capability())).Insert(1, "\"Version\":1,");
        Assert.Throws<InvalidDataException>(() => NativeHostProtocol.Parse(Bytes(request)));
    }


    [Fact]
    public async Task Parent_death_requests_all_cancellation_callbacks_without_echoing_their_errors()
    {
        using var work = new CancellationTokenSource();
        var stopped = false;
        using var failure = work.Token.Register(() => throw new InvalidOperationException("private-pairing-token"));
        using var stop = work.Token.Register(() => stopped = true);
        using var input = new MemoryStream();
        await NativeHostProtocol.WatchParentAsync(input, work, default);
        Assert.True(work.IsCancellationRequested);
        Assert.True(stopped);
    }

}
