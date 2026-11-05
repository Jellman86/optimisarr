using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Optimisarr.Api.Queue;
using Optimisarr.Api.Realtime;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Tests;

/// <summary>
/// The server chooses soundtrack bitrates for a worker's job before offering it. Nothing here may
/// hold a claim open or keep a job from a worker: an unfinished search offers nothing yet, and any
/// failure offers the library bitrate, which the worker's own soundtrack gate still judges.
/// </summary>
// Shares the tokened host's collection: the work root comes from a process-wide variable that
// another class in this collection also sets, so the two must never run at the same time.
[Collection(TokenedApiCollection.Name)]
public sealed class WorkerSoundtrackSelectionsTests : IDisposable
{
    private readonly AdminTokenAuthEndpointTests.TokenedApi _api;
    private readonly string _root = Directory.CreateTempSubdirectory("optimisarr-soundtrack-selection-").FullName;
    private readonly string? _previousWork = Environment.GetEnvironmentVariable("OPTIMISARR_WORK_DIR");
    private readonly CancellationTokenSource _stopping = new();

    public WorkerSoundtrackSelectionsTests(AdminTokenAuthEndpointTests.TokenedApi api)
    {
        _api = api;
        Environment.SetEnvironmentVariable("OPTIMISARR_WORK_DIR", Work);
        Directory.CreateDirectory(Work);
        File.WriteAllText(Source, "original soundtrack source");
    }

    private string Work => Path.Combine(_root, "work");
    private string Source => Path.Combine(_root, "film with spaces.mkv");
    private string Scratch => Path.Combine(Work, ".soundtrack-search");

    private static TranscodeSpec Spec(int bitrate = 128) => new("film.mkv", "out.mkv", "libx265", 24, null, false,
        AudioEncoder: "aac", AudioBitrateKbps: bitrate);

    private static VerificationPolicy Policy => VerificationPolicy.Default with { MaximumSoundtrackQualityDistance = 0.5 };

    [Fact]
    public async Task An_unfinished_selection_offers_nothing_and_the_finished_one_is_reused()
    {
        var probe = new ScriptedProbe();
        var selections = Selections(probe);

        Assert.Null(selections.TryTake(1, Source, Spec(), Policy));
        await probe.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(selections.TryTake(1, Source, Spec(), Policy));

        probe.Release(MediaProbeResult.Failure("unreadable"));
        var selection = await FinishedAsync(selections, 1, Spec());

        Assert.Equal(128, selection.BaselineKbps);
        Assert.Null(selection.SelectedBitrates());
        Assert.Contains("could not be probed", selection.Unavailable);
        Assert.Contains("library bitrate", selection.Unavailable);
        Assert.Same(selection, selections.TryTake(1, Source, Spec(), Policy));
        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public async Task A_search_crash_offers_the_library_bitrate_and_the_next_request_searches_again()
    {
        var probe = new ScriptedProbe { Crash = true };
        var selections = Selections(probe);

        var selection = await FinishedAsync(selections, 2, Spec());

        Assert.Equal(128, selection.BaselineKbps);
        Assert.Null(selection.SelectedBitrates());
        Assert.StartsWith("Soundtrack assessment failed:", selection.Unavailable);
        // The retry may already have crashed again by the time it is asked about, so only the fact
        // that it searched again is asserted, not whether it has finished.
        selections.TryTake(2, Source, Spec(), Policy);
        await WaitUntilAsync(() => probe.Calls == 2);
    }

    [Fact]
    public async Task A_changed_setting_or_source_is_selected_again_rather_than_reusing_the_old_answer()
    {
        var probe = new ScriptedProbe { Answer = MediaProbeResult.Failure("unreadable") };
        var selections = Selections(probe);
        await FinishedAsync(selections, 3, Spec());

        var changedSetting = await FinishedAsync(selections, 3, Spec(bitrate: 96));
        Assert.Equal(96, changedSetting.BaselineKbps);
        Assert.Equal(2, probe.Calls);

        File.AppendAllText(Source, " and more bytes");
        await FinishedAsync(selections, 3, Spec(bitrate: 96));
        Assert.Equal(3, probe.Calls);
    }

    [Fact]
    public async Task Scratch_abandoned_by_a_stopped_server_is_cleared_before_the_first_search_only()
    {
        var abandoned = Path.Combine(Scratch, "7-abandoned");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "sample.m4a"), "partial");
        var probe = new ScriptedProbe { Answer = MediaProbeResult.Failure("unreadable") };
        var selections = Selections(probe);

        await FinishedAsync(selections, 4, Spec());
        Assert.False(Directory.Exists(abandoned));

        // Only a server that stopped mid-search leaves directories here; once this process has
        // cleared them, anything that appears later belongs to a search that is still running.
        var current = Path.Combine(Scratch, "8-current");
        Directory.CreateDirectory(current);
        await FinishedAsync(selections, 5, Spec());
        Assert.True(Directory.Exists(current));
    }

    private WorkerSoundtrackSelections Selections(IMediaProbeService probe)
    {
        var scopes = new ServiceCollection().AddSingleton(probe).BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        return new WorkerSoundtrackSelections(scopes,
            _api.Services.GetRequiredService<VerificationService>(),
            _api.Services.GetRequiredService<PerceptualResourceAdmission>(),
            _api.Services.GetRequiredService<ActiveEncodeRegistry>(),
            new WorkEnvironment(_root), NullLogger<WorkerSoundtrackSelections>.Instance, _stopping.Token);
    }

    private async Task<SoundtrackSearchSummary> FinishedAsync(WorkerSoundtrackSelections selections, int jobId, TranscodeSpec spec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (selections.TryTake(jobId, Source, spec, Policy) is { } finished) return finished;
            await Task.Delay(20);
        }
        Assert.Fail("The soundtrack selection did not finish before the deadline.");
        return null!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(condition());
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        Environment.SetEnvironmentVariable("OPTIMISARR_WORK_DIR", _previousWork);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedProbe : IMediaProbeService
    {
        private readonly TaskCompletionSource<MediaProbeResult> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public bool Crash { get; init; }
        public MediaProbeResult? Answer { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public Task Started => _started.Task;

        public void Release(MediaProbeResult result) => _answer.TrySetResult(result);

        public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            _started.TrySetResult();
            // Not one of the failures the search expects, so it escapes as a crash would.
            if (Crash) throw new NotSupportedException("probe crashed");
            return Answer ?? await _answer.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class WorkEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Optimisarr";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
