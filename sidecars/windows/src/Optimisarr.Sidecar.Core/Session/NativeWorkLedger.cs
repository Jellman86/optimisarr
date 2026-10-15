using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisarr.Sidecar.Core.Session;

public enum NativeWorkPurpose { Job, Capability }
public enum NativeWorkPhase { Intent, Authorized, Retired, Released }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeWorkObligation(
    [property: JsonPropertyOrder(0)] Guid Owner,
    [property: JsonPropertyOrder(1)] NativeWorkPurpose Purpose,
    [property: JsonPropertyOrder(2)] int? JobId,
    [property: JsonPropertyOrder(3)] Guid? LeaseId,
    [property: JsonPropertyOrder(4)] string Root,
    [property: JsonPropertyOrder(5)] NativeScratchScope Scope,
    [property: JsonPropertyOrder(6)] string VolumeIdentity,
    [property: JsonPropertyOrder(7)] long NativeMemoryBytes,
    [property: JsonPropertyOrder(8)] long ScratchBytes,
    [property: JsonPropertyOrder(9)] bool ScratchUsesMemory,
    [property: JsonPropertyOrder(10)] NativeWorkPhase Phase)
{
    [JsonIgnore] public bool HoldsResources => Phase != NativeWorkPhase.Released;
    [JsonIgnore] public long MemoryCharge => checked(NativeMemoryBytes + (ScratchUsesMemory ? ScratchBytes : 0));
    [JsonIgnore] public long DiskCharge => ScratchBytes;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeWorkSnapshot(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] Guid StoreId,
    [property: JsonPropertyOrder(2)] long Revision,
    [property: JsonPropertyOrder(3)] ImmutableArray<NativeWorkObligation> Obligations);

/// <summary>Pure, bounded accounting format. Persistence and physical retirement supply authority.</summary>
public static class NativeWorkLedger
{
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumObligations = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() {
        MaxDepth = 12,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter<NativeWorkPurpose>(allowIntegerValues: false),
            new JsonStringEnumConverter<NativeWorkPhase>(allowIntegerValues: false) }
    };

    public static byte[] Encode(NativeWorkSnapshot snapshot)
    {
        Validate(snapshot);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        return bytes;
    }

    public static NativeWorkSnapshot Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw Invalid();
        try
        {
            var snapshot = JsonSerializer.Deserialize<NativeWorkSnapshot>(bytes, Json) ?? throw Invalid();
            // Canonical bytes make duplicate, omitted and silently reinterpreted fields invalid.
            // Any future encoding change requires a new schema and an explicit migration.
            if (!bytes.SequenceEqual(Encode(snapshot))) throw Invalid();
            return snapshot;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw Invalid(error); }
    }

    public static void ValidateTransition(NativeWorkSnapshot previous, NativeWorkSnapshot next)
    {
        Validate(previous); Validate(next);
        if (previous.StoreId != next.StoreId || previous.Revision == long.MaxValue || next.Revision != previous.Revision + 1)
            throw Invalid();
        var before = previous.Obligations.ToDictionary(record => record.Owner);
        var after = next.Obligations.ToDictionary(record => record.Owner);
        foreach (var record in previous.Obligations)
        {
            if (!after.TryGetValue(record.Owner, out var replacement))
            {
                if (record.Phase != NativeWorkPhase.Released) throw Invalid();
                continue;
            }
            if (record with { Phase = replacement.Phase } != replacement || !Forward(record.Phase, replacement.Phase))
                throw Invalid();
        }
        if (next.Obligations.Any(record => !before.ContainsKey(record.Owner) && record.Phase != NativeWorkPhase.Intent))
            throw Invalid();
    }

    private static bool Forward(NativeWorkPhase before, NativeWorkPhase after) => before == after || (before, after) is
        (NativeWorkPhase.Intent, NativeWorkPhase.Authorized or NativeWorkPhase.Retired)
        or (NativeWorkPhase.Authorized, NativeWorkPhase.Retired)
        or (NativeWorkPhase.Retired, NativeWorkPhase.Released);

    private static void Validate(NativeWorkSnapshot snapshot)
    {
        if (snapshot is null || snapshot.SchemaVersion != 1 || snapshot.StoreId == Guid.Empty || snapshot.Revision < 0
            || snapshot.Obligations.IsDefault || snapshot.Obligations.Length > MaximumObligations) throw Invalid();
        try
        {
            var owners = new HashSet<Guid>();
            var disk = new Dictionary<string, long>(StringComparer.Ordinal);
            long memory = 0;
            foreach (var record in snapshot.Obligations)
            {
                if (record is null || record.Owner == Guid.Empty || !owners.Add(record.Owner)
                    || !Enum.IsDefined(record.Purpose) || !Enum.IsDefined(record.Phase)
                    || !(record.Purpose == NativeWorkPurpose.Job && record.JobId is > 0 && record.LeaseId is { } lease && lease != Guid.Empty
                        || record.Purpose == NativeWorkPurpose.Capability && record.JobId is null && record.LeaseId is null)
                    || !Text(record.Root, 4096) || !Path.IsPathFullyQualified(record.Root)
                    || record.Root != Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.Root))
                    || record.Scope is null || !Text(record.Scope.Root, 256) || !Text(record.Scope.Control, 256) || !Text(record.Scope.Gate, 256)
                    || !Text(record.VolumeIdentity, 512) || record.NativeMemoryBytes <= 0 || record.ScratchBytes <= 0) throw Invalid();
                var charge = record.MemoryCharge;
                if (!record.HoldsResources) continue;
                memory = checked(memory + charge);
                disk[record.VolumeIdentity] = checked(disk.GetValueOrDefault(record.VolumeIdentity) + record.DiskCharge);
            }
        }
        catch (Exception error) when (error is OverflowException or ArgumentException) { throw Invalid(error); }
    }

    private static bool Text(string? value, int maximumBytes) => !string.IsNullOrWhiteSpace(value)
        && !value.Contains('\0') && StrictUtf8.GetByteCount(value) <= maximumBytes;
    private static InvalidDataException Invalid(Exception? inner = null) => new("Native work accounting is invalid or unsupported.", inner);
}
