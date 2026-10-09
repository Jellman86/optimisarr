using System.Security.Cryptography;
using System.Text.Json;
using Optimisarr.Core.Diagnostics;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>A private, bounded local mirror. Consent must be renewed by a healthy server check-in.</summary>
public sealed class DiagnosticJournal(string? directory = null, string? ffmpeg = null, string? ffprobe = null, string? measurementFfmpeg = null, bool readOnly = false)
{
    private readonly object gate = new();
    private SidecarDiagnosticConsent? consent;
    private DateTimeOffset deadline;
    private readonly Guid instance = Guid.NewGuid();
    private long sequence;
    private readonly List<JournalEntry> entries = [];
    private readonly Dictionary<Guid, long> droppedBySession = [];
    private readonly Dictionary<Guid, (int JobId, string? Stage)> leases = [];
    private string? ffmpegHash, ffprobeHash, measurementHash;
    private bool toolsRead;
    private readonly HashSet<Guid> toolsRecorded = [];
    private bool loaded;
    public const int MaximumEntries = 2048;
    public const int MaximumFileBytes = 1024 * 1024;
    public const string FileName = "diagnostic-events.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record JournalEntry(Guid SessionId, Guid InstanceId, SidecarDiagnosticEvent Event, bool Acknowledged = false);

    public long DroppedEvents { get { lock (gate) { return consent is null ? 0 : droppedBySession.GetValueOrDefault(consent.SessionId); } } }
    public void Apply(SidecarDiagnosticConsent? value, DateTimeOffset now)
    {
        lock (gate)
        {
            Load();
            if (value is { Recording: true } && (consent?.SessionId != value.SessionId || consent?.Recording != true))
            {
                foreach (var leaseId in leases.Keys.ToArray()) leases[leaseId] = (leases[leaseId].JobId, null);
                toolsRecorded.Clear(); toolsRead = false;
            }
            consent = value;
            deadline = value is null ? now : now.AddSeconds(Math.Min(90, value.ExpiresAt is { } expiry
                ? Math.Max(0, (expiry - value.ServerTimeUtc).TotalSeconds) : 90));
            var count = entries.Count; Prune(now);
            if (entries.Count != count) Persist();
        }
    }
    public void Assignment(Guid leaseId, int jobId)
    {
        lock (gate)
        {
            if (leases.Count >= MaximumEntries) { var oldest = leases.Keys.First(); leases.Remove(oldest); toolsRecorded.Remove(oldest); }
            leases[leaseId] = (jobId, null);
            Record(leaseId, "Worker.AssignmentReceived");
            RecordTools(leaseId, jobId);
        }
    }
    public void Stage(Guid leaseId, string? stage, double? seconds, int status)
    {
        lock (gate)
        {
            if (!leases.TryGetValue(leaseId, out var lease)) return;
            RecordTools(leaseId, lease.JobId);
            if (lease.Stage == stage && status is >= 200 and < 300) return;
            leases[leaseId] = (lease.JobId, stage);
            Record(leaseId, status is >= 200 and < 300 ? "Worker.StageChanged" : "Worker.RequestFailed", stage, seconds, httpStatus: status);
        }
    }
    private void RecordTools(Guid leaseId, int jobId)
    {
        if (consent is not { Recording: true } || DateTimeOffset.UtcNow >= deadline
            || consent.ScopedJobId is { } scope && scope != jobId || !toolsRecorded.Add(leaseId)) return;
        if (!toolsRead)
        {
            ffmpegHash = ToolHash(ffmpeg); ffprobeHash = ToolHash(ffprobe);
            measurementHash = measurementFfmpeg is null || measurementFfmpeg == ffmpeg ? ffmpegHash : ToolHash(measurementFfmpeg);
            toolsRead = true;
        }
        Record(leaseId, "Worker.ToolsIdentified", ffmpegSha256: ffmpegHash, ffprobeSha256: ffprobeHash, measurementFfmpegSha256: measurementHash);
    }
    public void Record(Guid leaseId, string reason, string? stage = null, double? seconds = null,
        long? offset = null, int? httpStatus = null, string? ffmpegSha256 = null, string? ffprobeSha256 = null, string? measurementFfmpegSha256 = null)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (consent is null || !consent.Recording || now >= deadline || !leases.TryGetValue(leaseId, out var lease)
                || consent.ScopedJobId is { } scoped && scoped != lease.JobId || !DiagnosticTelemetry.Reasons.Contains(reason)) return;
            entries.Add(new(consent.SessionId, instance, DiagnosticTelemetry.Sanitize(new(++sequence,
                now, leaseId, lease.JobId, reason, stage, seconds, offset, httpStatus, ffmpegSha256, ffprobeSha256, measurementFfmpegSha256))));
            Prune(now); Persist();
        }
    }
    public SidecarDiagnosticBatch? Pending()
    {
        lock (gate)
        {
            if (consent is null || consent.Recording && DateTimeOffset.UtcNow >= deadline) return null;
            var pending = entries.Where(e => e.SessionId == consent.SessionId && !e.Acknowledged).Take(100).ToArray();
            if (pending.Length == 0) return null;
            var batchInstance = pending[0].InstanceId;
            return new(1, consent.SessionId, batchInstance, pending.Where(e => e.InstanceId == batchInstance).Select(e => e.Event).ToArray(), DroppedEvents: DroppedEvents);
        }
    }
    public void Acknowledge(SidecarDiagnosticBatch batch, long throughSequence)
    {
        lock (gate)
        {
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].SessionId == batch.SessionId && entries[i].InstanceId == batch.InstanceId && entries[i].Event.Sequence <= throughSequence)
                    entries[i] = entries[i] with { Acknowledged = true };
            Persist();
        }
    }
    private void Load()
    {
        if (loaded) return; loaded = true;
        if (directory is null) return;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > MaximumFileBytes || File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-7)) { if (!readOnly) File.Delete(path); return; }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var saved = doc.RootElement.GetProperty("entries").Deserialize<List<JournalEntry>>(Json) ?? [];
            entries.AddRange(saved.Where(e => e is not null && e.Event is not null && e.Event.Sequence > 0 && e.Event.JobId > 0 && DiagnosticTelemetry.Reasons.Contains(e.Event.ReasonCode)).TakeLast(MaximumEntries)
                .Select(e => e with { Event = DiagnosticTelemetry.Sanitize(e.Event) }));
            if (doc.RootElement.TryGetProperty("droppedBySession", out var dropped))
                foreach (var pair in (dropped.Deserialize<Dictionary<Guid, long>>(Json) ?? []).Where(p => p.Value is >= 0 and <= 1_000_000_000).Take(20))
                    droppedBySession[pair.Key] = pair.Value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
    }
    // Keep acknowledged records locally too, so the operator can export them if the server loses its database.
    public byte[] Export()
    {
        lock (gate)
        {
            Load(); var count = entries.Count; Prune(DateTimeOffset.UtcNow);
            if (entries.Count != count) Persist();
            return Serialize();
        }
    }
    private byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, collection = "LocalSidecar", maximumEntries = MaximumEntries, droppedBySession, entries }, Json);
    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in entries.Where(e => e.Event.OccurredAt < now.AddDays(-7))) CountDropped(entry);
        entries.RemoveAll(e => e.Event.OccurredAt < now.AddDays(-7));
        while (entries.Count > MaximumEntries) { CountDropped(entries[0]); entries.RemoveAt(0); }
    }
    private void CountDropped(JournalEntry entry)
    {
        if (entry.Acknowledged) return;
        if (droppedBySession.Count >= 20 && !droppedBySession.ContainsKey(entry.SessionId)) droppedBySession.Remove(droppedBySession.Keys.First());
        droppedBySession[entry.SessionId] = Math.Min(1_000_000_000, droppedBySession.GetValueOrDefault(entry.SessionId) + 1);
    }
    private void Persist()
    {
        if (readOnly || directory is null) return;
        try
        {
            var path = Path.Combine(directory, FileName);
            if (entries.Count == 0) { File.Delete(path); return; }
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            var bytes = Serialize();
            while (bytes.Length > MaximumFileBytes && entries.Count > 0) { CountDropped(entries[0]); entries.RemoveAt(0); bytes = Serialize(); }
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Telemetry must not fail a media job. */ }
    }
    private static string? ToolHash(string? path)
    {
        try { if (path is null || !File.Exists(path)) return null; using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
