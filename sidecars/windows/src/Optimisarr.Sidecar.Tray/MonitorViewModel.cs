using System;
using Optimisarr.Core.Domain;
using System.ComponentModel;
using System.Linq;
using System.Collections.Generic;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Tray;

internal sealed class MonitorViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MonitorSnapshot? Snapshot { get; private set; }
    private string? error;
    public bool Available => Snapshot is not null && error is null;
    public bool ShutdownArmed => Available && Snapshot?.ShutdownArmed == true;
    public bool ShowPause => !ShutdownArmed;
    public bool CanPause => Available && Snapshot?.ShutdownArmed != true;
    public bool CanArmShutdown => Available && (Snapshot?.ShutdownArmed == true
        ? Snapshot.ShutdownCanCancel : Snapshot?.State is "Connected" or "Working" or "Unreachable");
    public string Machine => Snapshot?.Machine ?? Environment.MachineName;
    /// <summary>The status chip's words and tone, from the table shared with the Mac menu
    /// (<c>docs/design/windows-sidecar/README.md</c>): pause and an armed shutdown outrank the connection.</summary>
    public string State => Status.Label;
    /// <summary>ok, warn, bad or info: the web interface's tone the chip is drawn in.</summary>
    public string StateTone => Status.Tone;
    private (string Label, string Tone) Status => !Available ? ("Worker offline", "bad")
        : Snapshot!.ShutdownArmed ? ("Shutdown armed", "warn")
        : Snapshot.Paused ? ("Paused", "info")
        : Snapshot.Jobs.Count > 0 ? ("Working", "ok")
        : Snapshot.State switch
        {
            "Working" => ("Working", "ok"),
            "Connected" => ("Ready", "ok"),
            "Unreachable" => ("No server", "warn"),
            "Unpaired" => ("Not paired", "info"),
            "Stopped" => ("Stopped", "warn"),
            _ => ("Needs attention", "bad"),
        };
    public string Title => !Available ? "Worker not connected" : Snapshot!.Jobs.FirstOrDefault()?.Title ?? (Snapshot.ShutdownArmed ? "Finishing before shutdown" : Snapshot.Paused ? "New jobs paused" : Snapshot.State == "Connected" ? "Ready for work" : "Worker needs attention");
    public string Stage => Snapshot?.Jobs.FirstOrDefault() is { } job && Available ? StageName(job.Stage) + " · " + job.Encoder : Available ? Snapshot!.ShutdownArmed ? "No new jobs will be accepted." : Snapshot.State == "Connected" ? "New jobs will appear here automatically." : Snapshot.Detail : "Start the worker or pair this PC in Preferences.";
    public string Cpu => Percent(Snapshot?.Load?.CpuBusyFraction);
    public string Gpu => Percent(Snapshot?.Load?.GpuBusyFraction);
    public string Free => Available && Snapshot?.FreeBytes is { } bytes ? Gigabytes(bytes) : "—";
    public string PauseLabel => Snapshot?.Paused == true ? "Resume accepting jobs" : Snapshot?.Jobs.Count > 0 ? "Pause after current job" : "Pause new jobs";
    /// <summary>Segoe Fluent Icons glyphs matching the Mac menu's symbols: play or pause, cancel or power.</summary>
    public string PauseGlyph => Snapshot?.Paused == true ? "\uE768" : "\uE769";
    public string ShutdownGlyph => Snapshot?.ShutdownArmed == true ? "\uE711" : "\uE7E8";
    public string ShutdownLabel => Snapshot?.ShutdownArmed == true ? "Cancel shutdown" : "Shut down when work is complete";
    public string ShutdownDetail => Snapshot?.ShutdownArmed != true
        ? "Stops new jobs, waits for held work to return, then starts a 60-second countdown."
        : Snapshot.State == "Unreachable" ? Snapshot.ShutdownDetail ?? "Server unreachable; shutdown is blocked."
        : Snapshot.Jobs.Any(job => job.Stage == RemoteStage.Delivering)
            ? "Finishing candidate upload and waiting for the server acknowledgement. No new jobs are accepted."
        : Snapshot.Jobs.Any(job => job.Stage == RemoteStage.Measuring)
            ? "Waiting for sidecar quality checks and verification to finish. No new jobs are accepted."
        : Snapshot.ShutdownDetail ?? "Waiting for the worker";
    public string Note => error ?? (Snapshot?.ShutdownArmed == true ? "Closing this panel does not cancel shutdown." : Snapshot?.Paused == true ? "Current work will finish. Paused until resumed or the service restarts." : "Closing this panel keeps your jobs running.");
    public string Detail => !Available ? "Live readings are unavailable." : string.Join("\n", Snapshot!.Jobs.Select(job => $"Job #{job.JobId} · {StageName(job.Stage)}" + (job.EncodedSeconds is { } seconds ? " · " + Encoded(seconds) : "")));
    public string Last => Available ? Snapshot?.LastOutcome ?? "No jobs completed since the worker started." : "";
    public string Server => Available ? Snapshot?.ServerAddress ?? "Not paired" : "Unavailable";
    public string Version => Snapshot?.Version ?? "Unavailable";
    /// <summary>The release page, re-checked here: the tray opens it, so it must be the project's own.</summary>
    public Uri? UpdateReleasePage => Available
        ? SidecarUpdate.TryCreate(Snapshot!.UpdateVersion, Snapshot.UpdateUrl)?.ReleasePage
        : null;
    public bool UpdateAvailable => UpdateReleasePage is not null;
    public string UpdateText => UpdateAvailable
        ? $"The server is on {Snapshot!.UpdateVersion} and this worker is older. Older sidecars can fail good encodes."
        : "";
    public string ConnectionDetail => Available ? Snapshot!.Detail : error ?? "Connecting to the local worker…";
    public bool Working => Available && Snapshot!.Jobs.Count > 0;
    public IReadOnlyList<MonitorJob> Jobs => Available ? Snapshot!.Jobs : [];
    public IReadOnlyList<MonitorJobRow> JobRows => Jobs.Select(job => new MonitorJobRow(
        job.Title,
        $"Job #{job.JobId} · {StageName(job.Stage)}" + (job.EncodedSeconds is { } seconds ? " · " + Encoded(seconds) : ""),
        job.PreviewJpeg, job.Kind == MediaKind.Audio)).ToArray();
    /// <summary>The line under the progress bar, as the Mac menu writes it; empty when a stage has
    /// nothing honest to report.</summary>
    public string ProgressText => Jobs.FirstOrDefault() is not { } job ? ""
        : job.EncodedSeconds is { } seconds ? Encoded(seconds)
        : job.Stage == RemoteStage.Measuring ? "Measuring quality" : "";
    public bool ShowProgressText => ProgressText.Length > 0;
    public bool StatusDetailVisible => Available && Snapshot!.State is "Unreachable" or "Faulted" or "Stopped" && !string.IsNullOrWhiteSpace(Snapshot.Detail);
    public string StatusDetail => StatusDetailVisible ? Snapshot!.Detail : "";
    public byte[]? Preview => Jobs.FirstOrDefault()?.PreviewJpeg;
    public bool ActiveAudio => Jobs.FirstOrDefault()?.Kind == MediaKind.Audio;
    /// <summary>A picture only while a video job runs, as on the Mac: an idle card has no frame to show.</summary>
    public bool ShowThumbnail => Working && !ActiveAudio;
    public double PreviewColumnWidth => ShowThumbnail ? 112 : 0;
    public byte[]? VideoPreview => ActiveAudio ? null : Preview;
    public string PreviewDescription => ActiveAudio ? "Source audio spectrogram; verification runs separately." : "Source frame";
    public bool PreviewMissing => Preview is null;
    public string PreviewLabel => !Working ? "NO ACTIVE MEDIA"
        : Jobs.FirstOrDefault()?.Kind == MediaKind.Audio ? "SOURCE AUDIO · SPECTROGRAM" : "WAITING FOR FRAME";
    public void Update(MonitorSnapshot snapshot) { Snapshot = snapshot; error = null; Changed(); }
    public void Disconnect(string reason) { error = reason; Changed(); }
    public void ClearPreviews()
    {
        if (Snapshot is null) return;
        Snapshot = Snapshot with { Jobs = Snapshot.Jobs.Select(job => job with { PreviewJpeg = null }).ToArray() };
        Changed();
    }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    /// <summary>"0:12:31 encoded": hours unpadded, as the Mac menu writes it.</summary>
    private static string Encoded(double seconds)
    {
        var whole = (long)Math.Max(0, seconds);
        return $"{whole / 3600}:{whole % 3600 / 60:00}:{whole % 60:00} encoded";
    }
    /// <summary>"428 GB", with one decimal below ten, as the Mac menu writes it.</summary>
    private static string Gigabytes(long bytes)
    {
        var value = bytes / 1_073_741_824d;
        return value < 10 ? value.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " GB" : Math.Round(value).ToString("0", System.Globalization.CultureInfo.CurrentCulture) + " GB";
    }
    private string Percent(double? value) => Available && value is { } number && double.IsFinite(number) ? $"{Math.Clamp(number, 0, 1) * 100:0}%" : "—";
    private static string StageName(RemoteStage stage) => stage switch
    {
        RemoteStage.FetchingSource => "Receiving source",
        RemoteStage.Encoding => "Encoding",
        RemoteStage.Measuring => "Quality & verification",
        RemoteStage.Delivering => "Returning candidate",
        _ => "Working"
    };
}

internal sealed record MonitorJobRow(string Title, string Caption, byte[]? PreviewJpeg, bool IsAudio = false)
{
    public string PreviewDescription => IsAudio ? "Source spectrogram · up to 3 s · 0–24 kHz (log)" : "Source frame";
}
