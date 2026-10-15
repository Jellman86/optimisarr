using System.Text.Json;
using System.Text.Json.Nodes;
using Optimisarr.Sidecar.Core.Session;
using Optimisarr.Sidecar.Native;

namespace Optimisarr.Sidecar.Core.Tests;

[Collection("Native scratch ownership")]
public sealed class NativeHostRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-host-recovery-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
    private JsonObject Request() => new() { ["version"] = 1, ["mode"] = "recover",
        ["ffmpegPath"] = Path.GetFullPath("missing-native-media-tool"), ["scratchRoot"] = _root };
    private static NativeHostRequest Parse(JsonObject request) => NativeHostProtocol.Parse(JsonSerializer.SerializeToUtf8Bytes(request));

    [Fact]
    public async Task Recovery_does_not_need_media_tools_network_credentials_or_a_memory_grant()
    {
        var request = Parse(Request());
        var result = await NativeHostExecutor.RunAsync(request, _ => Assert.Fail("Recovery cannot report media work"), default);
        Assert.Equal("recovered", result.Type);
        Assert.Null(result.JobId);
        var json = JsonSerializer.SerializeToElement(result, NativeHostProtocol.Json);
        Assert.Equal(0, json.GetProperty("recovery").GetProperty("removed").GetInt32());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Recovery_removes_stopped_work_but_preserves_live_owners_and_unrecorded_media()
    {
        using var stopped = NativeScratchWorkspace.CreateCapability(_root);
        File.WriteAllText(Path.Combine(stopped.DirectoryPath, "copy.png"), "owned copy");
        stopped.Dispose();
        using var live = NativeScratchWorkspace.CreateJob(_root, 12, Guid.NewGuid());
        var original = Path.Combine(_root, "unrecorded.png"); File.WriteAllText(original, "unrecorded media");
        var result = await NativeHostExecutor.RunAsync(Parse(Request()), _ => Assert.Fail("No media progress"), default);
        var recovery = JsonSerializer.SerializeToElement(result, NativeHostProtocol.Json).GetProperty("recovery");
        Assert.Equal(1, recovery.GetProperty("removed").GetInt32());
        Assert.Equal(1, recovery.GetProperty("busy").GetInt32());
        Assert.False(Directory.Exists(stopped.DirectoryPath));
        Assert.True(Directory.Exists(live.DirectoryPath));
        Assert.Equal("unrecorded media", File.ReadAllText(original));
        Assert.True(live.Cleanup());
    }

    [Theory]
    [InlineData("pairing", "{\"serverAddress\":\"https://example.com\",\"credential\":\"private-token\",\"workerId\":1}")]
    [InlineData("assignment", "{}")]
    [InlineData("grantedMemoryBytes", "1")]
    [InlineData("imageMetric", "\"/missing-image-tool\"")]
    [InlineData("audioMetric", "\"/missing-audio-tool\"")]
    [InlineData("metadataTool", "\"/missing-metadata-tool\"")]
    [InlineData("command", "\"delete-anything\"")]
    public void Recovery_cannot_carry_media_authority_or_an_unrecognised_command(string key, string value)
    {
        var request = Request(); request[key] = JsonNode.Parse(value);
        Assert.Throws<InvalidDataException>(() => Parse(request));
    }

    [Theory]
    [InlineData("../owner-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json")]
    [InlineData("owner-00000000000000000000000000000000.json")]
    [InlineData("owner-not-a-nonce.json")]
    [InlineData("")]
    public void Recovery_cursor_is_an_owner_name_never_a_path(string cursor)
    {
        var request = Request(); request["recoveryAfterOwner"] = cursor;
        Assert.Throws<InvalidDataException>(() => Parse(request));
    }

    [Fact]
    public async Task Recovery_pages_resume_after_the_last_processed_owner()
    {
        for (var index = 0; index < 65; index++) NativeScratchWorkspace.CreateCapability(_root).Dispose();
        var request = Request();
        var first = await NativeHostExecutor.RunAsync(Parse(request), _ => Assert.Fail("No media progress"), default);
        var page = JsonSerializer.SerializeToElement(first, NativeHostProtocol.Json).GetProperty("recovery");
        Assert.Equal(64, page.GetProperty("removed").GetInt32());
        request["recoveryAfterOwner"] = page.GetProperty("nextOwner").GetString();
        var second = await NativeHostExecutor.RunAsync(Parse(request), _ => Assert.Fail("No media progress"), default);
        Assert.Equal(1, JsonSerializer.SerializeToElement(second, NativeHostProtocol.Json).GetProperty("recovery").GetProperty("removed").GetInt32());
    }

    [Fact]
    public async Task Cancelled_recovery_does_not_create_working_directories()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var request = Parse(Request());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeHostExecutor.RunAsync(request, _ => { }, cancelled.Token));
        Assert.False(Directory.Exists(_root));
    }
}
