using System.Text.Json;
using Optimisarr.Core.Diagnostics;
using Optimisarr.Sidecar.Core.Session;
namespace Optimisarr.Sidecar.Core.Tests;

public sealed class DiagnosticJournalTests
{
    [Fact]
    public void Consent_is_required_and_expiry_scope_and_stage_deduplication_are_enforced()
    {
        var journal = new DiagnosticJournal(); var lease = Guid.NewGuid();
        journal.Assignment(lease, 42); journal.Record(lease, "Worker.LeaseReleased");
        Assert.Null(journal.Pending());
        Assert.Empty(JsonDocument.Parse(journal.Export()).RootElement.GetProperty("entries").EnumerateArray());
        var now = DateTimeOffset.UtcNow;
        journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), 42), now);
        journal.Stage(lease, "Encoding", 1, 200); journal.Stage(lease, "Encoding", 2, 200);
        var batch = Assert.IsType<SidecarDiagnosticBatch>(journal.Pending()); Assert.Single(batch.Events, e => e.ReasonCode == "Worker.StageChanged");
        journal.Acknowledge(batch, batch.Events.Max(e => e.Sequence)); Assert.Null(journal.Pending());
        journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), 43), now);
        journal.Record(lease, "Worker.LeaseReleased"); Assert.Null(journal.Pending());
        journal.Apply(new(Guid.NewGuid(), now, now.AddHours(-1), null), now);
        journal.Record(lease, "Worker.LeaseReleased"); Assert.Null(journal.Pending());
    }
    [Fact]
    public void Pending_records_survive_restart_without_reactivating_capture_and_secrets_are_not_exported()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new DiagnosticJournal(root); var now = DateTimeOffset.UtcNow;
            var consent = new SidecarDiagnosticConsent(Guid.NewGuid(), now, now.AddHours(1), null);
            journal.Apply(consent, now); var lease = Guid.NewGuid(); journal.Assignment(lease, 42);
            journal.Record(lease, "Worker.StageChanged", "Bearer secret-token", double.NaN, ffmpegSha256: "/private/secret");
            var recovered = new DiagnosticJournal(root);
            Assert.Null(recovered.Pending()); recovered.Apply(consent, now);
            Assert.NotNull(recovered.Pending());
            var json = System.Text.Encoding.UTF8.GetString(recovered.Export());
            Assert.DoesNotContain("secret", json); Assert.DoesNotContain("NaN", json);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Local_storage_and_batch_sizes_remain_bounded_under_repeated_failure()
    {
        var journal = new DiagnosticJournal(); var now = DateTimeOffset.UtcNow;
        journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), null), now); var lease = Guid.NewGuid(); journal.Assignment(lease, 42);
        for (var i = 0; i < 3000; i++) journal.Record(lease, "Worker.RequestFailed", httpStatus: 503);
        Assert.Equal(100, journal.Pending()!.Events.Count);
        Assert.True(journal.Pending()!.DroppedEvents > 0);
        Assert.Equal(2048, JsonDocument.Parse(journal.Export()).RootElement.GetProperty("entries").GetArrayLength());
        Assert.True(journal.Export().Length <= DiagnosticJournal.MaximumFileBytes);
    }
    [Fact]
    public void Expired_local_records_are_removed_from_disk_without_starting_another_capture()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow; var journal = new DiagnosticJournal(root);
            journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), null), now);
            journal.Assignment(Guid.NewGuid(), 42);
            Assert.True(File.Exists(Path.Combine(root, DiagnosticJournal.FileName)));
            journal.Apply(null, now.AddDays(8));
            Assert.False(File.Exists(Path.Combine(root, DiagnosticJournal.FileName)));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public void Enabling_capture_mid_job_records_current_stage_and_tool_identity()
    {
        var journal = new DiagnosticJournal(); var lease = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        journal.Assignment(lease, 42); journal.Stage(lease, "Encoding", 1, 200);
        journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), null), now);
        journal.Stage(lease, "Encoding", 2, 200);
        Assert.Contains(journal.Pending()!.Events, e => e.ReasonCode == "Worker.StageChanged");
        Assert.Contains(journal.Pending()!.Events, e => e.ReasonCode == "Worker.ToolsIdentified");
    }
    [Fact]
    public void A_malformed_local_entry_does_not_break_recovery_or_export()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, DiagnosticJournal.FileName), "{\"entries\":[null]}");
            Assert.Empty(JsonDocument.Parse(new DiagnosticJournal(root).Export()).RootElement.GetProperty("entries").EnumerateArray());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Source_completion_and_resumed_upload_offsets_are_recorded_as_observed_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow; var journal = new DiagnosticJournal(); var lease = Guid.NewGuid();
            journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), null), now); journal.Assignment(lease, 42);
            using var server = new FakeWorkerServer([1, 2, 3], new string('a', 64)) { Delivered = [1, 2] };
            using var http = new HttpClient(server);
            var transfer = new JobTransfer(http, journal); var file = Path.Combine(root, "candidate.mkv");
            var pairing = new StoredPairing("http://example.test", "private-credential", 1);
            await transfer.FetchSourceAsync(pairing, lease, file);
            await transfer.DeliverAsync(pairing, lease, file, new string('a', 64));
            var events = journal.Pending()!.Events;
            Assert.Contains(events, e => e.ReasonCode == "Worker.TransferOffset" && e.OffsetBytes == 2);
            Assert.True(events.Count(e => e.ReasonCode == "Worker.TransferAcknowledged" && e.OffsetBytes == 3) >= 2);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Tray_snapshot_export_never_rewrites_newer_service_records()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var now = DateTimeOffset.UtcNow; var writer = new DiagnosticJournal(root);
            writer.Apply(new(Guid.NewGuid(), now, now.AddHours(1), null), now);
            var lease = Guid.NewGuid(); writer.Assignment(lease, 42);
            var reader = new DiagnosticJournal(root, readOnly: true);
            reader.Apply(null, now);
            writer.Record(lease, "Worker.TransferAcknowledged", offset: 123);
            var path = Path.Combine(root, DiagnosticJournal.FileName);
            var latest = File.ReadAllBytes(path);
            reader.Apply(null, now.AddDays(8));
            Assert.Empty(JsonDocument.Parse(reader.Export()).RootElement.GetProperty("entries").EnumerateArray());
            Assert.True(File.Exists(path));
            Assert.Equal(latest, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tray_snapshot_export_does_not_delete_stale_or_oversized_service_files(bool oversized)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, DiagnosticJournal.FileName);
            var original = oversized ? new byte[DiagnosticJournal.MaximumFileBytes + 1] : "{\"entries\":[]}"u8.ToArray();
            File.WriteAllBytes(path, original);
            if (!oversized) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            var reader = new DiagnosticJournal(root, readOnly: true);
            if (oversized) Assert.Throws<IOException>(() => reader.Export());
            else Assert.Empty(JsonDocument.Parse(reader.Export()).RootElement.GetProperty("entries").EnumerateArray());
            Assert.True(File.Exists(path));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void A_read_only_export_reports_a_journal_read_failure_instead_of_claiming_empty_evidence()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, DiagnosticJournal.FileName), "invalid-json");
            var reader = new DiagnosticJournal(root, readOnly: true);
            Assert.Throws<IOException>(() => reader.Export());
            Assert.Throws<IOException>(() => reader.Export());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Corrupt_recovery_is_disclosed_in_the_export_and_every_following_upload()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, DiagnosticJournal.FileName), "{broken");
            var journal = new DiagnosticJournal(root); var now = DateTimeOffset.UtcNow;
            journal.Apply(new(Guid.NewGuid(), now, now.AddHours(1), null), now);
            journal.Assignment(Guid.NewGuid(), 42);
            Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(journal.Pending(), new JsonSerializerOptions(JsonSerializerDefaults.Web))).RootElement.GetProperty("recoveryIncomplete").GetBoolean());
            Assert.True(JsonDocument.Parse(journal.Export()).RootElement.GetProperty("recoveryIncomplete").GetBoolean());
            Assert.True(JsonDocument.Parse(new DiagnosticJournal(root).Export()).RootElement.GetProperty("recoveryIncomplete").GetBoolean());
            var batch = journal.Pending()!; journal.Acknowledge(batch, batch.Events.Max(e => e.Sequence));
            Assert.False(JsonDocument.Parse(new DiagnosticJournal(root).Export()).RootElement.GetProperty("recoveryIncomplete").GetBoolean());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_read_only_export_cannot_mistake_a_journal_directory_for_a_missing_file()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, DiagnosticJournal.FileName));
        try { Assert.Throws<IOException>(() => new DiagnosticJournal(root, readOnly: true).Export()); }
        finally { Directory.Delete(root, true); }
    }

}
