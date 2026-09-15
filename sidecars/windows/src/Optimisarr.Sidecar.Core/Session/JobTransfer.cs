using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// Moving a source down and a candidate back up.
///
/// <para>Both directions are resumable, because these are whole video files over a home network and
/// a transfer that has to restart from zero after a dropped connection is one that may never finish
/// at all on a large title.</para>
///
/// <para>Both directions are hashed. The server refuses a candidate that does not match its
/// declared hash, and refuses one encoded from the wrong source — so verifying here turns a
/// corrupted transfer into a clear local failure rather than a delivery the server discards after
/// the machine has already spent an hour on the encode.</para>
/// </summary>
public sealed class JobTransfer(HttpClient http)
{
    private const string SourceHashHeader = "X-Optimisarr-Source-Sha256";
    private const string CandidateHashHeader = "X-Optimisarr-Candidate-Sha256";
    private const string OffsetHeader = "X-Optimisarr-Offset";

    /// <summary>How much is sent per request. Small enough that a dropped connection costs little.</summary>
    private const int ChunkBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Fetches the source for a held lease, resuming from whatever is already on disk, and returns
    /// the hash the server declared for it.
    /// </summary>
    public async Task<string?> FetchSourceAsync(
        StoredPairing pairing,
        Guid leaseId,
        string destination,
        IProgress<(long Received, long Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var already = File.Exists(destination) ? new FileInfo(destination).Length : 0;

        using var request = new HttpRequestMessage(
            HttpMethod.Get, SidecarClient.Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/source"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
        if (already > 0)
        {
            // Ask only for what is missing. The server supports ranges precisely so an interrupted
            // fetch of a 40 GB source does not start again from nothing.
            request.Headers.Range = new RangeHeaderValue(already, null);
        }

        using var response = await http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Already have the whole thing: the server has nothing beyond what is on disk.
            return DeclaredHash(response);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new SidecarException(
                $"Fetching the source failed (HTTP {(int)response.StatusCode}).",
                // A lease that has lapsed or been taken away is not worth retrying; anything else
                // may simply be a server that blinked.
                recoverable: response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.Forbidden));
        }

        // A server that ignored the range header sends the whole file: start again rather than
        // append its first bytes onto what is already here and produce a corrupt source.
        var appending = already > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        var total = (response.Content.Headers.ContentLength ?? 0) + (appending ? already : 0);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        await using (var file = new FileStream(
            destination,
            appending ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[ChunkBytes];
            var received = appending ? already : 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report((received, total));
            }
        }

        return DeclaredHash(response);
    }

    /// <summary>
    /// Sends a candidate back, resuming from whatever the server already holds for this lease.
    ///
    /// <para>The server is the authority on how much arrived, so the offset is asked for rather
    /// than remembered: a worker that trusted its own count after a crash would append a chunk in
    /// the wrong place and produce a file that fails its hash after the whole upload.</para>
    /// </summary>
    public async Task DeliverAsync(
        StoredPairing pairing,
        Guid leaseId,
        string candidatePath,
        string sourceSha256,
        IProgress<(long Sent, long Total)>? progress = null,
        CancellationToken cancellationToken = default,
        // Already known when the candidate was measured. Hashing a forty-gigabyte file twice for
        // one delivery is a whole second read of the disk for a number the caller is holding.
        string? candidateSha256 = null)
    {
        candidateSha256 ??= await HashAsync(candidatePath, cancellationToken);
        var total = new FileInfo(candidatePath).Length;
        var offset = await HeldBytesAsync(pairing, leaseId, cancellationToken);

        await using (var file = File.OpenRead(candidatePath))
        {
            file.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[ChunkBytes];

            while (offset < total)
            {
                var read = await file.ReadAsync(buffer, cancellationToken);
                if (read <= 0)
                {
                    break;
                }

                using var chunk = new HttpRequestMessage(
                    HttpMethod.Patch,
                    SidecarClient.Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/result"))
                {
                    Content = new ByteArrayContent(buffer, 0, read),
                };
                chunk.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
                chunk.Headers.Add(OffsetHeader, offset.ToString());

                using var response = await http.SendAsync(chunk, cancellationToken);
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    // The server and this machine disagree about how much arrived, and the server
                    // wins. It says what it actually holds, so seek there and carry on rather than
                    // failing the whole delivery over a resumable disagreement.
                    offset = await HeldBytesAsync(pairing, leaseId, cancellationToken);
                    file.Seek(offset, SeekOrigin.Begin);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new SidecarException(
                        $"Delivering the candidate failed (HTTP {(int)response.StatusCode}).",
                        recoverable: response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.Forbidden));
                }

                offset += read;
                progress?.Report((offset, total));
            }
        }

        using var complete = new HttpRequestMessage(
            HttpMethod.Post,
            SidecarClient.Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/result/complete"));
        complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);
        complete.Headers.Add(SourceHashHeader, sourceSha256);
        complete.Headers.Add(CandidateHashHeader, candidateSha256);

        using var finished = await http.SendAsync(complete, cancellationToken);
        if (!finished.IsSuccessStatusCode)
        {
            throw new SidecarException(
                $"The server refused the delivered candidate (HTTP {(int)finished.StatusCode}).",
                recoverable: false);
        }
    }

    private async Task<long> HeldBytesAsync(
        StoredPairing pairing, Guid leaseId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            SidecarClient.Endpoint(pairing.ServerAddress, $"/api/workers/leases/{leaseId}/result/offset"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Credential);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("bytes", out var bytes) ? bytes.GetInt64() : 0;
    }

    private static string? DeclaredHash(HttpResponseMessage response) =>
        response.Headers.TryGetValues(SourceHashHeader, out var values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>SHA-256 of a file, streamed rather than loaded — these are whole videos.</summary>
    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
