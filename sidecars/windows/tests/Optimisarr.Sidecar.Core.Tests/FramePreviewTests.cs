using Optimisarr.Sidecar.Core.Session;
using System.Diagnostics;

namespace Optimisarr.Sidecar.Core.Tests;

public sealed class FramePreviewTests
{
    private sealed class FakeExtractor(Func<Task<byte[]?>> sample) : IFramePreviewExtractor
    {
        public int Calls { get; private set; }
        public async Task<byte[]?> ExtractAsync(string ffmpeg, string source, double seconds, CancellationToken token)
        {
            Calls++;
            return await sample();
        }
    }

    [Fact]
    public async Task Only_a_viewer_can_trigger_a_bounded_sample_and_failures_never_escape()
    {
        var now = DateTimeOffset.UtcNow;
        var wanted = false;
        var delivered = new List<byte[]>();
        var extractor = new FakeExtractor(() => Task.FromResult<byte[]?>([0xff, 0xd8, 1, 0xff, 0xd9]));
        var sampler = new JobPreviewSampler("ffmpeg", extractor, () => wanted, delivered.Add, () => now);
        await sampler.TrySampleAsync("source", 1, CancellationToken.None);
        Assert.Equal(0, extractor.Calls);
        wanted = true;
        await sampler.TrySampleAsync("source", 1, CancellationToken.None);
        await sampler.TrySampleAsync("source", 2, CancellationToken.None);
        Assert.Single(delivered);
        Assert.Equal(1, extractor.Calls);
        now += TimeSpan.FromSeconds(1.5);
        await sampler.TrySampleAsync("source", 2, CancellationToken.None);
        Assert.Equal(2, extractor.Calls);
        Assert.Equal(2, delivered.Count);

        var failing = new JobPreviewSampler("ffmpeg", new FakeExtractor(() => throw new IOException("unsupported source")),
            () => true, _ => throw new Exception("should not publish"));
        await failing.TrySampleAsync("source", 3, CancellationToken.None);
    }

    [Fact]
    public async Task In_flight_sample_does_not_stack_processes_and_cancellation_drops_its_frame()
    {
        var release = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var extractor = new FakeExtractor(() => release.Task);
        var published = false;
        using var cancellation = new CancellationTokenSource();
        var sampler = new JobPreviewSampler("ffmpeg", extractor, () => true, _ => published = true);
        var first = sampler.TrySampleAsync("source", 1, cancellation.Token);
        await sampler.TrySampleAsync("source", 2, cancellation.Token);
        Assert.Equal(1, extractor.Calls);
        cancellation.Cancel();
        release.SetResult([0xff, 0xd8, 1, 0xff, 0xd9]);
        await first;
        Assert.False(published);
    }

    [Fact]
    public void Extractor_uses_a_seeking_one_frame_video_only_low_cost_command()
    {
        var args = FfmpegFramePreviewExtractor.Arguments("C:\\scratch\\source", 27.125).ToArray();
        Assert.Contains("0:V:0", args);
        Assert.Contains("-frames:v", args);
        Assert.Contains("scale=160:-2", args);
        Assert.Equal("27.13", args[Array.IndexOf(args, "-ss") + 1]);
        Assert.True(Array.IndexOf(args, "-ss") < Array.IndexOf(args, "-i"));
    }

    [Fact]
    public async Task Native_ffmpeg_extracts_a_small_frame_and_falls_back_for_audio_only()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("OPTIMISARR_PREVIEW_FFMPEG");
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(ffmpeg)) return;
        var root = Path.Combine(Path.GetTempPath(), "optimisarr-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Path.Combine(root, "video.mkv");
            await CreateAsync(ffmpeg, ["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=1",
                "-t", "3", "-c:v", "mpeg4", "-q:v", "4", video]);
            var frame = await new FfmpegFramePreviewExtractor().ExtractAsync(ffmpeg, video, 1, CancellationToken.None);
            Assert.NotNull(frame);
            Assert.InRange(frame.Length, 4, MonitorProtocol.MaximumPreviewBytes);
            Assert.Equal((byte)0xff, frame[0]);
            Assert.Equal((byte)0xd8, frame[1]);
            Assert.Equal((byte)0xd9, frame[^1]);

            var audio = Path.Combine(root, "audio.wav");
            await CreateAsync(ffmpeg, ["-f", "lavfi", "-i", "sine=frequency=440:duration=2",
                "-c:a", "pcm_s16le", audio]);
            Assert.Null(await new FfmpegFramePreviewExtractor().ExtractAsync(ffmpeg, audio, 1, CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CreateAsync(string ffmpeg, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-loglevel");
        start.ArgumentList.Add("error");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }
}
