using System.Diagnostics;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Queue;

namespace Optimisarr.Tests;

/// <summary>
/// The bounded super-resolution stream against fake tools: a decoder that writes PNG pictures to
/// stdout, an engine that upscales a directory, and an encoder that counts what arrives on stdin.
/// Scratch must stay within one batch, every process must be gone before scratch is removed, and
/// a stall, a crash, a wrong picture or a full disk must fail the attempt with its reason.
/// </summary>
public sealed class SuperResolutionPipelineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-sr-pipeline-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private const string PngHelpers = """
        import os, struct, sys, time
        SIG = b'\x89PNG\r\n\x1a\n'
        def chunk(kind, data):
            return struct.pack('>I', len(data)) + kind + data + b'\0\0\0\0'
        def png(w, h):
            return SIG + chunk(b'IHDR', struct.pack('>II', w, h) + bytes([8, 2, 0, 0, 0])) + chunk(b'IDAT', b'\0' * 16) + chunk(b'IEND', b'')
        def read_png(stream):
            sig = stream.read(8)
            if not sig:
                return None
            out = sig
            while True:
                head = stream.read(8)
                n = struct.unpack('>I', head[:4])[0]
                body = stream.read(n + 4)
                out += head + body
                if head[4:8] == b'IEND':
                    return out
        """;

    /// <summary>One fake ffmpeg: decoder when it writes to "-", encoder when it reads "-".</summary>
    private string FakeFfmpeg(int frames, string? decoderMode = null)
    {
        var script = TestPython.Shebang + PngHelpers + $$"""

        args = sys.argv[1:]
        if args[-1] == '-':
            mode = {{(decoderMode is null ? "None" : "'" + decoderMode + "'")}}
            for i in range({{frames}}):
                if mode == 'crash' and i == 3:
                    sys.stderr.write('decoder crashed\n'); sys.exit(7)
                sys.stdout.buffer.write(png(64, 36)); sys.stdout.buffer.flush()
            sys.exit(0)
        out = args[-1]
        count = 0
        while read_png(sys.stdin.buffer) is not None:
            count += 1
        with open(out, 'w') as f:
            f.write(str(count))
        """;
        return Write("ffmpeg", script);
    }

    /// <summary>A fake engine: upscales each picture's header and records the scratch high-water mark.</summary>
    private string FakeEngine(string mode = "ok")
    {
        var script = TestPython.Shebang + PngHelpers + $$"""

        args = sys.argv[1:]
        src = args[args.index('-i') + 1]; dst = args[args.index('-o') + 1]; scale = int(args[args.index('-s') + 1])
        mode = '{{mode}}'
        if mode == 'stall':
            time.sleep(600)
        if mode == 'fail':
            sys.stderr.write('vkCreateInstance failed\n'); sys.exit(3)
        workspace = os.path.dirname(src)
        files = sum(len(fs) for _, _, fs in os.walk(workspace))
        with open(os.path.join(os.path.dirname(workspace), 'highwater'), 'a') as f:
            f.write(str(files) + '\n')
        for name in sorted(os.listdir(src)):
            w, h = (64, 36) if mode == 'wrong-size' else (64 * scale, 36 * scale)
            with open(os.path.join(dst, name), 'wb') as f:
                f.write(png(w, h))
        """;
        return Write("engine", script);
    }

    private string Write(string name, string script)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private (SuperResolutionRunRequest Request, string Output) Request(string ffmpeg, string engine, int batch = 8, TimeSpan? stall = null)
    {
        var output = Path.Combine(_root, "candidate.mkv");
        var workspace = Path.Combine(_root, "work", "super-resolution", "1-a");
        return (new SuperResolutionRunRequest(
            ffmpeg, engine,
            DecoderArguments: ["-i", "source.mkv", "-f", "image2pipe", "-"],
            EncoderArguments: ["-f", "image2pipe", "-i", "-", output],
            EngineArguments: (input, outputDir) => ["-i", input, "-o", outputDir, "-s", "2"],
            workspace,
            ExpectedUpscaled: new PictureSize(128, 72),
            ExpectedFrames: 50,
            FramesPerBatch: batch,
            WorstCaseBatchBytes: 1024,
            ReserveBytes: 0,
            StallLimit: stall ?? TimeSpan.FromMinutes(5)), output);
    }

    private static readonly List<int> Started = [];

    private static SuperResolutionRunHooks Hooks(Func<long?>? free = null, Func<bool>? paused = null) => new(
        free ?? (() => null),
        paused ?? (() => false),
        pid => { lock (Started) Started.Add(pid); return null; },
        _ => Task.CompletedTask);

    private static void AssertAllStopped(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.True(process.HasExited, $"process {pid} was left running");
            }
            catch (ArgumentException)
            {
                // Gone, as required.
            }
        }
    }

    [PosixFact]
    public async Task Every_picture_reaches_the_encoder_while_scratch_stays_within_one_batch()
    {
        var (request, output) = Request(FakeFfmpeg(50), FakeEngine(), batch: 8);

        var run = await SuperResolutionPipeline.RunAsync(request, Hooks(), CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("50", await File.ReadAllTextAsync(output));
        var highWater = (await File.ReadAllLinesAsync(Path.Combine(_root, "work", "super-resolution", "highwater")))
            .Select(int.Parse).Max();
        Assert.True(highWater <= 8, $"scratch held {highWater} pictures for a batch of 8");
        Assert.False(Directory.Exists(request.Workspace));
    }

    [PosixFact]
    public async Task A_stalled_engine_is_stopped_and_its_scratch_removed()
    {
        var (request, _) = Request(FakeFfmpeg(20), FakeEngine("stall"), stall: TimeSpan.FromSeconds(3));
        int[] before;
        lock (Started) before = [.. Started];

        var run = await SuperResolutionPipeline.RunAsync(request, Hooks(), CancellationToken.None);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("no progress", run.Error);
        lock (Started) AssertAllStopped(Started.Except(before));
        Assert.False(Directory.Exists(request.Workspace));
    }

    [PosixFact]
    public async Task A_paused_queue_is_not_mistaken_for_a_stall()
    {
        var (request, output) = Request(FakeFfmpeg(16), FakeEngine(), stall: TimeSpan.FromSeconds(2));
        var pausedUntil = Stopwatch.StartNew();

        var run = await SuperResolutionPipeline.RunAsync(request,
            Hooks(paused: () => pausedUntil.Elapsed < TimeSpan.FromSeconds(4)), CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("16", await File.ReadAllTextAsync(output));
    }

    [PosixFact]
    public async Task Cancellation_stops_every_process_before_scratch_is_removed()
    {
        var (request, _) = Request(FakeFfmpeg(20), FakeEngine("stall"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        int[] before;
        lock (Started) before = [.. Started];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SuperResolutionPipeline.RunAsync(request, Hooks(), cancel.Token));

        lock (Started) AssertAllStopped(Started.Except(before));
        Assert.False(Directory.Exists(request.Workspace));
    }

    [PosixFact]
    public async Task An_engine_failure_is_reported_with_its_exit_code_and_log()
    {
        var (request, _) = Request(FakeFfmpeg(10), FakeEngine("fail"));
        var run = await SuperResolutionPipeline.RunAsync(request, Hooks(), CancellationToken.None);
        Assert.Equal(3, run.ExitCode);
        Assert.Contains("vkCreateInstance", run.Log);
    }

    [PosixFact]
    public async Task A_picture_of_the_wrong_size_fails_the_attempt()
    {
        var (request, _) = Request(FakeFfmpeg(10), FakeEngine("wrong-size"));
        var run = await SuperResolutionPipeline.RunAsync(request, Hooks(), CancellationToken.None);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("64x36", run.Error);
    }

    [PosixFact]
    public async Task A_crashed_decoder_fails_the_attempt()
    {
        var (request, _) = Request(FakeFfmpeg(10, decoderMode: "crash"), FakeEngine());
        var run = await SuperResolutionPipeline.RunAsync(request, Hooks(), CancellationToken.None);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("decoder crashed", run.Log);
    }

    [PosixFact]
    public async Task A_batch_that_would_eat_into_the_free_space_reserve_is_refused()
    {
        var (request, _) = Request(FakeFfmpeg(10), FakeEngine());
        request = request with { WorstCaseBatchBytes = 5_000, ReserveBytes = 10_000 };
        var run = await SuperResolutionPipeline.RunAsync(request, Hooks(free: () => 12_000), CancellationToken.None);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("reserve", run.Error);
        Assert.False(File.Exists(Path.Combine(_root, "work", "super-resolution", "highwater")));
    }
}
