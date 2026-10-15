using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimisarr.Sidecar.Core.Session;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeWorkBinding(
    [property: JsonPropertyOrder(0)] string Path,
    [property: JsonPropertyOrder(1)] string DirectoryIdentity,
    [property: JsonPropertyOrder(2)] string CoordinatorIdentity);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NativeWorkCheckpoint(
    [property: JsonPropertyOrder(0)] int SchemaVersion,
    [property: JsonPropertyOrder(1)] Guid StoreId,
    [property: JsonPropertyOrder(2)] NativeWorkBinding Binding,
    [property: JsonPropertyOrder(3)] string Committed,
    [property: JsonPropertyOrder(4)] string? Pending);

/// <summary>Pure checkpoint transitions; callers must durably write and read back before effects.</summary>
public static class NativeWorkCheckpointProtocol
{
    public const int MaximumBytes = 8192;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = JavaScriptEncoder.Default,
        WriteIndented = false
    };

    public static NativeWorkCheckpoint Initialize(NativeWorkBinding binding, ReadOnlySpan<byte> initial)
    {
        var snapshot = NativeWorkLedger.Decode(initial);
        if (snapshot.Revision != 0 || snapshot.Obligations.Length != 0) throw Invalid();
        var checkpoint = new NativeWorkCheckpoint(1, snapshot.StoreId, binding, Digest(initial), null);
        return Encodable(checkpoint);
    }

    public static NativeWorkCheckpoint Begin(NativeWorkCheckpoint current, ReadOnlySpan<byte> previous, ReadOnlySpan<byte> next)
    {
        Validate(current);
        if (current.Pending is not null) throw Invalid();
        var oldSnapshot = NativeWorkLedger.Decode(previous);
        if (current.Committed != Digest(previous)) throw Invalid();
        var newSnapshot = NativeWorkLedger.Decode(next);
        if (oldSnapshot.StoreId != current.StoreId) throw Invalid();
        NativeWorkLedger.ValidateTransition(oldSnapshot, newSnapshot);
        return Encodable(current with { Pending = Digest(next) });
    }

    public static NativeWorkCheckpoint Resolve(NativeWorkCheckpoint? checkpoint, NativeWorkBinding binding, ReadOnlySpan<byte> visible)
    {
        Validate(checkpoint);
        var snapshot = NativeWorkLedger.Decode(visible);
        var digest = Digest(visible);
        if (checkpoint!.Binding != binding || checkpoint.StoreId != snapshot.StoreId
            || checkpoint.Committed != digest && checkpoint.Pending != digest) throw Invalid();
        return Encodable(checkpoint with { Committed = digest, Pending = null });
    }

    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] Encode(NativeWorkCheckpoint checkpoint)
    {
        Validate(checkpoint);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, Json);
        if (bytes.Length > MaximumBytes) throw Invalid();
        return bytes;
    }

    public static NativeWorkCheckpoint Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes) throw Invalid();
        try
        {
            var checkpoint = JsonSerializer.Deserialize<NativeWorkCheckpoint>(bytes, Json) ?? throw Invalid();
            if (!bytes.SequenceEqual(Encode(checkpoint))) throw Invalid();
            return checkpoint;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { throw Invalid(error); }
    }

    private static NativeWorkCheckpoint Encodable(NativeWorkCheckpoint checkpoint)
    {
        _ = Encode(checkpoint);
        return checkpoint;
    }

    private static void Validate(NativeWorkCheckpoint? checkpoint)
    {
        try
        {
            if (checkpoint is null || checkpoint.SchemaVersion != 1 || checkpoint.StoreId == Guid.Empty || checkpoint.Binding is null
                || !Text(checkpoint.Binding.Path, 4096) || !Path.IsPathFullyQualified(checkpoint.Binding.Path)
                || checkpoint.Binding.Path != Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkpoint.Binding.Path))
                || !Text(checkpoint.Binding.DirectoryIdentity, 256) || !Text(checkpoint.Binding.CoordinatorIdentity, 256)
                || !DigestValid(checkpoint.Committed)
                || checkpoint.Pending is not null && (!DigestValid(checkpoint.Pending) || checkpoint.Pending == checkpoint.Committed)) throw Invalid();
        }
        catch (ArgumentException error) { throw Invalid(error); }
    }

    private static bool Text(string? value, int maximumBytes) => !string.IsNullOrWhiteSpace(value)
        && !value.Contains('\0') && StrictUtf8.GetByteCount(value) <= maximumBytes;
    private static bool DigestValid(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static InvalidDataException Invalid(Exception? inner = null) => new("Native work checkpoint authority is unavailable or changed.", inner);
}
