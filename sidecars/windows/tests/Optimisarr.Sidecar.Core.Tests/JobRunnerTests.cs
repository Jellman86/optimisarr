using System.Net;
using System.Text;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Core.Tests;

/// <summary>
/// One job from claim to delivery, with a fake server and a fake encoder. What is pinned here is
/// what happens when something goes wrong part-way, because those paths are the ones that decide
/// whether a bad night costs one job or fills a disk and delivers rubbish.
/// </summary>
public sealed class JobRunnerTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "optimisarr-jobtests", Guid.NewGuid().ToString("N"));

    private sealed class FakeServer(byte[] source, string sourceHash) : HttpMessageHandler
    {
        public readonly List<string> Calls = [];
        public byte[] Delivered = [];
        public bool Completed;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add($"{request.Method} {path}");

            if (path.EndsWith("/source", StringComparison.Ordinal))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(source),
                };
                response.Headers.Add("X-Optimisarr-Source-Sha256", sourceHash);
                return Task.FromResult(response);
            }

            if (path.EndsWith("/result/offset", StringComparison.Ordinal))
            {
                return Json($$"""{"bytes":{{Delivered.Length}}}""");
            }

            if (path.EndsWith("/result/complete", StringComparison.Ordinal))
            {
                Completed = true;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            if (path.EndsWith("/result", StringComparison.Ordinal))
            {
                var body = request.Content!.ReadAsByteArrayAsync(cancellationToken).Result;
                Delivered = [.. Delivered, .. body];
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeTranscoder(int exitCode, string? writeCandidate, string errorTail = "") : ITranscoder
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<TranscodeResult> RunAsync(
            string ffmpeg, IReadOnlyList<string> arguments,
            IProgress<double>? encodedSeconds, CancellationToken cancellationToken)
        {
            Arguments = arguments;
            encodedSeconds?.Report(12.5);
            if (writeCandidate is not null)
            {
                File.WriteAllText(writeCandidate, "encoded-bytes");
            }
            return Task.FromResult(new TranscodeResult(exitCode, errorTail));
        }
    }

    private static Assignment Assignment() => new(
        LeaseId: Guid.NewGuid(),
        JobId: 5888,
        Title: "The Dinosaurs - S01E01",
        SourceBytes: 13,
        VideoEncoder: "hevc_nvenc",
        Vmaf: "Cpu",
        ExpiresUtc: DateTimeOffset.UtcNow.AddMinutes(5),
        RenewWithinSeconds: 120,
        Arguments: ["-i", "{{input}}", "-c:v", "hevc_nvenc", "{{output}}.mkv"],
        OutputExtension: ".mkv",
        Quality: new QualityRequirement(false, "", 1, false, 0, 0, []));

    private static StoredPairing Pairing() => new("https://server.example.com", "secret", 7);

    [Fact]
    public async Task A_job_is_fetched_encoded_and_delivered()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var hash = Hash(source);
        var server = new FakeServer(source, hash);
        var http = new HttpClient(server);
        var assignment = Assignment();
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeTranscoder(0, candidate), "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.True(server.Completed);
        Assert.Equal("encoded-bytes", Encoding.UTF8.GetString(server.Delivered));
        // Scratch is never left behind: a worker keeping every source it was sent fills a disk.
        Assert.False(Directory.Exists(Path.Combine(_scratch, $"job-{assignment.JobId}")));
    }

    [Fact]
    public async Task A_source_that_did_not_arrive_intact_is_never_encoded()
    {
        // The server declares a hash that will not match what it actually sent. Encoding anyway
        // would spend an hour producing a candidate the server refuses for the wrong source.
        var server = new FakeServer(Encoding.UTF8.GetBytes("truncated"), Hash(Encoding.UTF8.GetBytes("whole")));
        var http = new HttpClient(server);
        var transcoder = new FakeTranscoder(0, null);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("hash mismatch", outcome.Detail);
        Assert.Null(transcoder.Arguments);   // never ran
        Assert.Contains(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_encode_gives_the_job_back_with_the_reason()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeServer(source, Hash(source));
        var http = new HttpClient(server);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeTranscoder(1, null, "Error while opening encoder for output stream"),
            "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("Error while opening encoder", outcome.Detail);
        // Handed back at once rather than left to lapse, so the queue can try elsewhere.
        Assert.Contains(server.Calls, call => call.EndsWith("/release", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_encode_that_claims_success_but_produces_nothing_is_not_delivered()
    {
        // Exit code zero is not proof of a file. Delivering nothing would leave the server waiting
        // on an upload that never comes.
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeServer(source, Hash(source));
        var http = new HttpClient(server);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            new FakeTranscoder(0, null), "ffmpeg.exe", _scratch, () => null);

        var outcome = await runner.RunAsync(Pairing(), Assignment(), CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Contains("no candidate file", outcome.Detail);
        Assert.False(server.Completed);
    }

    [Fact]
    public async Task The_server_chose_the_encode_and_only_the_paths_are_this_machines()
    {
        var source = Encoding.UTF8.GetBytes("source-bytes");
        var server = new FakeServer(source, Hash(source));
        var http = new HttpClient(server);
        var assignment = Assignment();
        var candidate = Path.Combine(_scratch, $"job-{assignment.JobId}", "candidate.mkv");
        var transcoder = new FakeTranscoder(0, candidate);

        var runner = new JobRunner(
            new SidecarClient(http), new JobTransfer(http),
            transcoder, "ffmpeg.exe", _scratch, () => null);

        await runner.RunAsync(Pairing(), assignment, CancellationToken.None);

        // The encoder and its flags are untouched; only the two paths were filled in.
        Assert.Contains("-c:v", transcoder.Arguments!);
        Assert.Contains("hevc_nvenc", transcoder.Arguments!);
        Assert.DoesNotContain(transcoder.Arguments!, argument => argument.Contains("{{"));
    }

    private static string Hash(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }
}
