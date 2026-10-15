using System.Net;
using System.Net.Sockets;
using System.Text;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_coordination_cannot_hide_a_live_owner(bool missingControl)
    {
        using var live = NativeScratchWorkspace.CreateJob(_root, 12, Guid.NewGuid());
        if (missingControl && ControlMoveIsVetoedForLiveOwner(live)) return;
        var sentinel = Path.Combine(live.DirectoryPath, "copy.flac");
        File.WriteAllText(sentinel, "owned live copy");
        var gate = Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName);
        if (!missingControl) gate = Path.Combine(gate, "root.lock");
        var retainedGate = gate + ".retained";
        if (missingControl) Directory.Move(gate, retainedGate); else File.Move(gate, retainedGate);
        try
        {
            var result = await NativeHostExecutor.RunAsync(Parse(Request()), _ => Assert.Fail("Recovery cannot run media"), default);
            Assert.Equal(new NativeScratchRecovery(Deferred: 1), result.Recovery);
            Assert.False(Path.Exists(gate));
            Assert.True(File.Exists(missingControl ? Path.Combine(retainedGate, Path.GetFileName(live.RecordPath)) : live.RecordPath));
            Assert.Equal("owned live copy", File.ReadAllText(sentinel));
        }
        finally { if (missingControl) Directory.Move(retainedGate, gate); else File.Move(retainedGate, gate); }
        Assert.True(live.Cleanup());
    }

    [Fact]
    public void Recreated_empty_coordination_cannot_hide_the_old_coordinators_live_workspace()
    {
        using var live = NativeScratchWorkspace.CreateJob(_root, 12, Guid.NewGuid());
        if (ControlMoveIsVetoedForLiveOwner(live)) return;
        var control = Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName);
        var retainedControl = control + ".retained";
        Directory.Move(control, retainedControl);
        try
        {
            var replacementScope = NativeScratchWorkspace.PrepareAccountingScope(_root);
            Assert.Equal(new NativeScratchRecovery(Deferred: 1), NativeScratchWorkspace.Recover(_root));
            Assert.Equal(new NativeScratchRecovery(Deferred: 1), NativeScratchWorkspace.Recover(_root, expectedScope: replacementScope));
            Assert.True(Directory.Exists(live.DirectoryPath));
        }
        finally
        {
            File.Delete(Path.Combine(control, "root.lock"));
            Directory.Delete(control);
            Directory.Move(retainedControl, control);
        }
        Assert.True(live.Cleanup());
    }

    private bool ControlMoveIsVetoedForLiveOwner(NativeScratchWorkspace live)
    {
        if (!OperatingSystem.IsWindows()) return false;
        // Windows denies relocating a directory containing the exclusively held owner file.
        // Prove that kernel veto and the retained busy result rather than fabricating a move.
        var control = Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName);
        Assert.Throws<IOException>(() => Directory.Move(control, control + ".retained"));
        Assert.True(Directory.Exists(control));
        Assert.Equal(new NativeScratchRecovery(Busy: 1), NativeScratchWorkspace.Recover(_root));
        Assert.True(Directory.Exists(live.DirectoryPath));
        Assert.True(live.Cleanup());
        return true;
    }

    [Fact]
    public void Missing_control_without_native_work_is_clean_but_an_incomplete_root_scan_is_not()
    {
        Directory.CreateDirectory(_root);
        var sentinel = Path.Combine(_root, "original.flac");
        File.WriteAllText(sentinel, "unrecorded original");
        Assert.Equal(new NativeScratchRecovery(), NativeScratchWorkspace.Recover(_root));
        for (var index = 0; index < 4096; index++) File.WriteAllText(Path.Combine(_root, $"entry-{index}"), "");
        Assert.True(NativeScratchWorkspace.Recover(_root).ScanLimited);
        Assert.False(Directory.Exists(Path.Combine(_root, NativeScratchWorkspace.ControlDirectoryName)));
        Assert.Equal("unrecorded original", File.ReadAllText(sentinel));
    }

    [Fact]
    public void A_full_recovery_page_preserves_a_separate_root_level_orphan_hold()
    {
        for (var index = 0; index < 64; index++) NativeScratchWorkspace.CreateCapability(_root).Dispose();
        var orphan = Path.Combine(_root, "native-capability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);
        var sentinel = Path.Combine(orphan, "copy.png");
        File.WriteAllText(sentinel, "unaccounted copy");
        Assert.Equal(new NativeScratchRecovery(Removed: 64, Deferred: 1), NativeScratchWorkspace.Recover(_root));
        Assert.Equal(new NativeScratchRecovery(Deferred: 1), NativeScratchWorkspace.Recover(_root));
        Assert.Equal("unaccounted copy", File.ReadAllText(sentinel));
    }

    [Fact]
    public async Task Version_two_recovery_requires_the_parent_scope_and_cannot_borrow_another_roots_clean_result()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var request = Request(); request["version"] = 2;
        Assert.Throws<InvalidDataException>(() => Parse(request));
        request["accountingScope"] = JsonSerializer.SerializeToNode(scope, NativeHostProtocol.Json);
        var parsed = Parse(request);
        var result = await NativeHostExecutor.RunAsync(parsed, _ => Assert.Fail("Recovery cannot run media"), default);
        Assert.Equal(2, result.Version);
        Assert.Equal(new NativeScratchRecovery(), result.Recovery);
        request["accountedOwner"] = JsonSerializer.SerializeToNode(new { owner = Guid.NewGuid(), jobId = 12, leaseId = Guid.NewGuid() });
        Assert.Throws<InvalidDataException>(() => Parse(request));
    }

    [Fact]
    public void Scope_bound_recovery_preserves_stopped_work_in_a_replacement_root_and_rejects_missing_scope_files()
    {
        var scope = NativeScratchWorkspace.PrepareAccountingScope(_root);
        var replacement = Path.Combine(_root, "replacement");
        using var stopped = NativeScratchWorkspace.CreateCapability(replacement);
        stopped.Dispose();
        var before = File.ReadAllBytes(stopped.RecordPath);
        var result = NativeScratchWorkspace.Recover(replacement, expectedScope: scope);
        Assert.Equal(1, result.Deferred);
        Assert.Equal(0, result.Removed);
        Assert.Equal(before, File.ReadAllBytes(stopped.RecordPath));
        Assert.True(Directory.Exists(stopped.DirectoryPath));
        result = NativeScratchWorkspace.Recover(Path.Combine(_root, "missing"), expectedScope: scope);
        Assert.Equal(1, result.Deferred);
        Assert.False(Directory.Exists(Path.Combine(_root, "missing")));
        var replacementScope = NativeScratchWorkspace.PrepareAccountingScope(replacement);
        Assert.Equal(1, NativeScratchWorkspace.Recover(replacement, expectedScope: replacementScope).Removed);
    }

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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_parsed_run_cancelled_before_execution_still_returns_its_known_lease_without_media_work(int version)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        string? action = null;
        var respond = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            action = await reader.ReadLineAsync(deadline.Token);
            for (var headers = 0; headers < 64; headers++)
            {
                var line = await reader.ReadLineAsync(deadline.Token);
                if (line == "") break;
                Assert.NotNull(line);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        }, deadline.Token);
        var assignment = PerceptualJobFixture.Assignment();
        var request = new NativeHostRequest(version, "run", Path.GetFullPath("missing-media-tool"), _root,
            Pairing: new(address, new('a', 43), 1), GrantedMemoryBytes: 8L * 1024 * 1024 * 1024,
            Assignment: JsonSerializer.SerializeToElement(assignment, NativeHostProtocol.Json),
            AccountedOwner: version == 2 ? new(Guid.NewGuid(), assignment.JobId, assignment.LeaseId) : null,
            AccountingScope: version == 2 ? NativeScratchWorkspace.PrepareAccountingScope(_root) : null);
        request = NativeHostProtocol.Parse(JsonSerializer.SerializeToUtf8Bytes(request, NativeHostProtocol.Json));
        try
        {
            var result = await NativeHostExecutor.RunAsync(request, _ => Assert.Fail("No media work may start"), cancelled.Token).WaitAsync(deadline.Token);
            await respond.WaitAsync(deadline.Token);
            Assert.Equal("completed", result.Type);
            Assert.False(result.Outcome!.Delivered);
            Assert.False(result.Outcome.CapacityBlocked);
            Assert.Equal(version, result.Version);
            Assert.Equal(version == 2 ? true : null, result.NativeWorkspaceCreated);
            Assert.Equal(version == 2 ? true : null, result.NativeResourcesRetired);
            Assert.EndsWith("/interrupted HTTP/1.1", action);
            if (version == 1) Assert.False(Directory.Exists(_root));
            else
            {
                Assert.True(NativeScratchWorkspace.RetireAccountedJob(_root, assignment.JobId, assignment.LeaseId, request.AccountedOwner!.Owner).Retired);
                Assert.True(NativeScratchWorkspace.ForgetRetiredJob(_root, assignment.JobId, assignment.LeaseId, request.AccountedOwner.Owner).Consumed);
            }
        }
        finally
        {
            deadline.Cancel(); listener.Stop();
            try { await respond; } catch (OperationCanceledException) { } catch (SocketException) { }
        }
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
