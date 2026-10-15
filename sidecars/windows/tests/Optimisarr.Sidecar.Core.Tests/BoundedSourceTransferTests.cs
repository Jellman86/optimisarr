using System.Net;
using System.Net.Http.Headers;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class BoundedSourceTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bounded-source-" + Guid.NewGuid().ToString("N"));
    public BoundedSourceTransferTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class UnspecifiedLength(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    private sealed class Handler(HttpStatusCode status, byte[] bytes, bool chunked = false, long? from = null, long? total = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = new HttpResponseMessage(status) { Content = chunked ? new StreamContent(new UnspecifiedLength(bytes)) : new ByteArrayContent(bytes) };
            if (from is { } offset) response.Content.Headers.ContentRange = new(offset, offset + bytes.Length - 1, total!.Value);
            response.Headers.Add("X-Optimisarr-Source-Sha256", new string('a', 64));
            return Task.FromResult(response);
        }
    }
    [Theory]
    [InlineData(8, false, true)]
    [InlineData(8, true, true)]
    [InlineData(9, false, false)]
    [InlineData(9, true, false)]
    [InlineData(7, true, false)]
    public async Task Native_fetches_require_the_assigned_byte_count_even_without_a_content_length(int bytes, bool chunked, bool accepted)
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, new byte[bytes], chunked));
        var transfer = new JobTransfer(http); var path = Path.Combine(_root, "source");
        Task<string?> Fetch() => transfer.FetchSourceAsync(new("https://server.example", "secret", 1), Guid.NewGuid(), path, cancellationToken: default, expectedBytes: 8);
        if (accepted) { Assert.Equal(new string('a', 64), await Fetch()); Assert.Equal(8, new FileInfo(path).Length); }
        else
        {
            var error = await Assert.ThrowsAsync<SidecarException>(Fetch); Assert.False(error.Recoverable);
            Assert.True(!File.Exists(path) || new FileInfo(path).Length <= 8);
        }
    }
    [Theory]
    [InlineData(4L, 8L, true)]
    [InlineData(3L, 8L, false)]
    [InlineData(4L, 9L, false)]
    public async Task Native_resumption_requires_the_matching_range_and_complete_source_size(long from, long total, bool accepted)
    {
        var path = Path.Combine(_root, "source"); await File.WriteAllBytesAsync(path, new byte[4]);
        using var http = new HttpClient(new Handler(HttpStatusCode.PartialContent, new byte[4], from: from, total: total));
        var transfer = new JobTransfer(http);
        Task<string?> Fetch() => transfer.FetchSourceAsync(new("https://server.example", "secret", 1), Guid.NewGuid(), path, expectedBytes: 8);
        if (accepted) { await Fetch(); Assert.Equal(8, new FileInfo(path).Length); }
        else { await Assert.ThrowsAsync<SidecarException>(Fetch); Assert.Equal(4, new FileInfo(path).Length); }
    }
}
