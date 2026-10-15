using System.Diagnostics;
using System.Text.Json;
using Optimisarr.Core.Verification;

namespace Optimisarr.Tests;

// The executable fixture uses a POSIX shebang; pure counter/command contracts run on Windows too.
// Native FFmpeg acceptance separately checks the actual pipes on available physical hosts.
public sealed class VmafWatchdogProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimisarr-vmaf-watchdog-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(_root, "source");
    private string Candidate => Path.Combine(_root, "candidate");
    private string Pid => Path.Combine(_root, "pid");
    private string Log => Path.Combine(_root, "log-path");
    private static QualityMeasurementContext Context => new(320, 180, false, false,
        ReferenceStartSeconds: 20, DistortedStartSeconds: 20, MeasureDurationSeconds: 2,
        ReferenceFrameRate: 50, PairFramesByNumber: true, ModelVersion: "vmaf_v0.6.1");

    private QualityScoreService Service(string mode, TimeSpan? progressTimeout = null)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Source, "source"); File.WriteAllText(Candidate, "candidate");
        var tool = Path.Combine(_root, "fake-ffmpeg");
        File.WriteAllText(tool, "#!/usr/bin/python3\n" + """
            import os,sys,time,json,re,pathlib
            root=pathlib.Path(__ROOT__)
            graph=sys.argv[sys.argv.index('-lavfi')+1]
            log=re.search(r'log_path=([^:]+)',graph).group(1).strip("'")
            (root/'log-path').write_text(log)
            (root/'pid.tmp').write_text(str(os.getpid())); (root/'pid.tmp').replace(root/'pid')
            mode=__MODE__
            if mode=='fallback' and '-hwaccel' in sys.argv:
                pathlib.Path(log).write_text('{"pooled_metrics":{"vmaf":{"mean":100}}}')
                sys.stderr.write('hardware unavailable\n'); sys.exit(1)
            if mode=='flood':
                sys.stdout.write('discarded output'*100000+'\n'); sys.stdout.flush()
                sys.stderr.write('decoder error: '+'x'*200000+'\n'); sys.stderr.flush(); sys.exit(1)
            def tick(n):
                for name,stream in [('reference',sys.stdout),('candidate',sys.stderr)]:
                    stream.write('frame:'+str(n)+' pts:0 pts_time:0\n'+'lavfi.optimisarr.decode.'+name+'=1\n'); stream.flush()
            for i in range(15):
                tick(i*50 if mode in ['advance','no-log','cancel','scoring-stall','fallback'] else 0)
                time.sleep(0.2)
            if mode=='scoring-stall':
                sys.stderr.write('frame=25 time=00:00:00.50\n'); sys.stderr.flush()
            if mode in ['stale','scoring-stall','cancel']:
                while True:
                    tick(1000 if mode=='scoring-stall' else 0)
                    sys.stderr.write('frame=25 time=00:00:00.50\n' if mode=='scoring-stall' else 'frame=0 time=00:00:00.00\n'); sys.stderr.flush()
                    time.sleep(0.1)
            if mode!='no-log':
                pathlib.Path(log).write_text(json.dumps({'frames':[{'frameNum':0,'metrics':{'vmaf':96}},{'frameNum':1,'metrics':{'vmaf':96}}],'pooled_metrics':{'vmaf':{'mean':96,'harmonic_mean':96,'min':96}}}))
            """.Replace("__ROOT__", JsonSerializer.Serialize(_root), StringComparison.Ordinal)
            .Replace("__MODE__", JsonSerializer.Serialize(mode), StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new QualityScoreService(tool, progressTimeout ?? TimeSpan.FromSeconds(2));
    }

    [PosixFact]
    public async Task Advancing_decode_counters_survive_a_startup_longer_than_the_idle_deadline()
    {
        var progress = new RecordingProgress();
        var result = await Service("advance").MeasureAsync(Source, Candidate, Context, CancellationToken.None, progress);
        Assert.True(result.Measured, result.Error);
        Assert.Equal(96, result.Scores!.VmafHarmonicMean);
        Assert.Empty(progress.Values);
        AssertReaped(); AssertOwnedLogRemoved();
    }

    [PosixFact]
    public async Task Software_fallback_tracks_decoding_and_discards_failed_hardware_evidence()
    {
        var result = await Service("fallback").MeasureAsync(Source, Candidate,
            Context with { Acceleration = VmafAcceleration.Qsv }, CancellationToken.None);
        Assert.True(result.Measured, result.Error);
        Assert.Equal(VmafAcceleration.None, result.Acceleration);
        Assert.Equal(96, result.Scores!.VmafHarmonicMean);
        AssertReaped(); AssertOwnedLogRemoved();
    }

    [PosixFact]
    public async Task Noisy_decoder_output_is_drained_and_error_capture_stays_bounded()
    {
        // This fixture deliberately emits no progress while filling both pipes. Give the drain
        // its own overall bound so CPU contention cannot turn it into an idle-watchdog test.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await Service("flood", TimeSpan.FromSeconds(30)).MeasureAsync(Source, Candidate, Context, deadline.Token);
        Assert.False(result.Measured);
        Assert.StartsWith("decoder error:", result.Error);
        Assert.InRange(result.Error!.Length, 1, 65_536);
        AssertReaped(); AssertOwnedLogRemoved();
    }

    [PosixFact]
    public async Task Repeated_counters_and_zero_output_time_do_not_keep_a_stalled_process_alive()
    {
        var result = await Service("stale").MeasureAsync(Source, Candidate, Context, CancellationToken.None);
        Assert.False(result.Measured);
        Assert.Contains("no decoding or VMAF progress", result.Error);
        Assert.Contains("window at 20s", result.Error);
        AssertReaped(); AssertOwnedLogRemoved();
    }

    [PosixFact]
    public async Task Out_of_window_decode_markers_do_not_disable_the_scoring_watchdog()
    {
        var result = await Service("scoring-stall").MeasureAsync(Source, Candidate, Context, CancellationToken.None);
        Assert.False(result.Measured);
        Assert.Contains("no decoding or VMAF progress", result.Error);
        AssertReaped(); AssertOwnedLogRemoved();
    }

    [PosixFact]
    public async Task Heartbeats_cannot_replace_missing_quality_evidence()
    {
        // This asserts evidence refusal after a successful tool exit, not startup latency.
        // Keep it independent of the short idle deadline exercised by the stalled fixtures.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await Service("no-log", TimeSpan.FromSeconds(30))
            .MeasureAsync(Source, Candidate, Context, deadline.Token);
        Assert.False(result.Measured);
        Assert.Contains("no log", result.Error);
        AssertReaped(); AssertOwnedLogRemoved();
    }

    [PosixFact]
    public async Task Cancellation_reaps_the_decoder_and_removes_owned_quality_scratch()
    {
        using var cancel = new CancellationTokenSource();
        var service = Service("cancel");
        var run = service.MeasureAsync(Source, Candidate, Context, cancel.Token);
        for (var i = 0; i < 200 && !File.Exists(Pid); i++) await Task.Delay(25);
        Assert.True(File.Exists(Pid));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(10)));
        AssertReaped(); AssertOwnedLogRemoved();
    }

    private void AssertReaped()
    {
        try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Pid))); Assert.True(child.HasExited); }
        catch (ArgumentException) { }
    }

    private void AssertOwnedLogRemoved() => Assert.False(File.Exists(File.ReadAllText(Log)));

    private sealed class RecordingProgress : IProgress<double>
    {
        internal List<double> Values { get; } = [];
        public void Report(double value) => Values.Add(value);
    }

    public void Dispose()
    {
        if (File.Exists(Pid))
        {
            try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(Pid))); if (!child.HasExited) { child.Kill(true); child.WaitForExit(5000); } }
            catch (ArgumentException) { }
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
