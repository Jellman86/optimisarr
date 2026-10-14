using System.Text.Json;
using Optimisarr.Core.Domain;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Native;

internal sealed record NativeHostRequest(int Version, string Mode, string FfmpegPath, string ScratchRoot,
    string? ImageMetric = null, string? AudioMetric = null, string? MetadataTool = null,
    StoredPairing? Pairing = null, JsonElement? Assignment = null, long? GrantedMemoryBytes = null);

/// <summary>One bounded private request; closing its pipe ends the parent's authority to run.</summary>
internal static class NativeHostProtocol
{
    public const int MaximumRequestBytes = 256 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };

    public static async Task<byte[]> ReadRequestAsync(Stream input, CancellationToken token)
    {
        using var frame = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer, token).AsTask().WaitAsync(token);
            if (count == 0) throw new EndOfStreamException("The native host request is incomplete.");
            var newline = Array.IndexOf(buffer, (byte)'\n', 0, count);
            var length = newline < 0 ? count : newline;
            if (frame.Length + length > MaximumRequestBytes)
                throw new InvalidDataException("The native host request exceeds its byte budget.");
            frame.Write(buffer, 0, length);
            if (newline < 0) continue;
            if (newline != count - 1 || frame.Length == 0)
                throw new InvalidDataException("The native host accepts one request frame.");
            var bytes = frame.ToArray();
            return bytes[^1] == '\r' ? bytes[..^1] : bytes;
        }
    }

    public static NativeHostRequest Parse(ReadOnlyMemory<byte> bytes)
    {
        const string invalid = "The native host request is invalid or unsupported.";
        try
        {
            if (bytes.Length is <= 0 or > MaximumRequestBytes) throw new InvalidDataException(invalid);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (!UniqueProperties(document.RootElement)) throw new InvalidDataException(invalid);
            var request = document.RootElement.Deserialize<NativeHostRequest>(Json);
            if (request is null || request.Version != 1 || request.Mode is not ("capabilities" or "run")
                || !Absolute(request.FfmpegPath) || !Absolute(request.ScratchRoot)
                || request.ImageMetric is not null && !Absolute(request.ImageMetric)
                || request.AudioMetric is not null && !Absolute(request.AudioMetric)
                || request.MetadataTool is not null && !Absolute(request.MetadataTool)) throw new InvalidDataException(invalid);
            if (request.Mode == "capabilities")
            {
                if (request.Pairing is not null || request.Assignment is not null || request.GrantedMemoryBytes is not null)
                    throw new InvalidDataException(invalid);
            }
            else
            {
                if (request.Pairing is not { WorkerId: > 0 } pairing || pairing.Credential is not { Length: >= 32 and <= 512 }
                    || pairing.Credential.Any(character => character <= 32 || character >= 127)
                    || !Uri.TryCreate(pairing.ServerAddress, UriKind.Absolute, out var server)
                    || server.Scheme is not ("http" or "https") || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0
                    || request.GrantedMemoryBytes is not > 0 || request.Assignment is not { ValueKind: JsonValueKind.Object } raw)
                    throw new InvalidDataException(invalid);
                var assignment = raw.Deserialize<Assignment>(Json);
                if (assignment is null || assignment.JobId <= 0 || assignment.LeaseId == Guid.Empty || assignment.SourceBytes <= 0
                    || assignment.Kind is not (MediaKind.Image or MediaKind.Audio) || assignment.PerceptualSearch is null
                    || assignment.RefuseMediaContract() is not null) throw new InvalidDataException(invalid);
            }
            return request;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            // Parser diagnostics may contain the credential or an untrusted control frame.
            throw new InvalidDataException(invalid);
        }
    }

    public static async Task WatchParentAsync(Stream input, CancellationTokenSource work, CancellationToken done)
    {
        try
        {
            // Console pipes on macOS can ignore cancellation while blocked in the OS.
            // The single-use host must be able to exit without waiting for another byte.
            await input.ReadExactlyAsync(new byte[1], done).AsTask().WaitAsync(done);
            CancelOwnedWork(work);
        }
        catch (OperationCanceledException) when (done.IsCancellationRequested) { }
        catch (IOException) { CancelOwnedWork(work); }
    }

    internal static void CancelOwnedWork(CancellationTokenSource work)
    {
        try { work.Cancel(); }
        // Cancel invokes every callback before aggregating failures. Those diagnostics must
        // not escape through an OS signal handler or disclose private control-pipe data.
        catch (AggregateException) { }
    }

    private static bool Absolute(string? path) => path is { Length: > 0 and <= 4096 }
        && !path.Contains('\0') && Path.IsPathFullyQualified(path);
    private static bool UniqueProperties(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).All(group => group.Count() == 1)
            && value.EnumerateObject().All(property => UniqueProperties(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueProperties),
        _ => true
    };
}
