using System;
using System.ComponentModel;
using System.Linq;
using Optimisarr.Sidecar.Core.Session;

namespace Optimisarr.Sidecar.Tray;

internal sealed class MonitorViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public MonitorSnapshot? Snapshot { get; private set; }
    private string? error;
    public bool Available => Snapshot is not null && error is null;
    public string Machine => Snapshot?.Machine ?? Environment.MachineName;
    public string State => !Available ? "NO CONNECTION" : Snapshot!.Paused ? "PAUSED" : Snapshot.State == "Unreachable" ? "NO SERVER" : Snapshot.Jobs.Count > 0 ? "WORKING" : Snapshot.State.ToUpperInvariant();
    public string Title => !Available ? "Worker not connected" : Snapshot!.Jobs.FirstOrDefault()?.Title ?? (Snapshot.Paused ? "New jobs paused" : Snapshot.State == "Connected" ? "Ready for work" : "Worker needs attention");
    public string Stage => Snapshot?.Jobs.FirstOrDefault() is { } job && Available ? StageName(job.Stage) + " · " + job.Encoder : Available ? Snapshot!.State == "Connected" ? "New jobs will appear here automatically." : Snapshot.Detail : "Start the worker or pair this PC in Preferences.";
    public string Cpu => Percent(Snapshot?.Load?.CpuBusyFraction);
    public string Gpu => Percent(Snapshot?.Load?.GpuBusyFraction);
    public string Free => Available && Snapshot?.FreeBytes is { } bytes ? $"{bytes / 1_073_741_824d:0.#} GB" : "—";
    public string PauseLabel => Snapshot?.Paused == true ? "Resume accepting jobs" : Snapshot?.Jobs.Count > 0 ? "Pause after current job" : "Pause new jobs";
    public string Note => error ?? (Snapshot?.Paused == true ? "Current work will finish. Paused until resumed or the service restarts." : "Closing this panel keeps your jobs running.");
    public string Detail => !Available ? "Live readings are unavailable." : string.Join("\n", Snapshot!.Jobs.Select(job => $"Job #{job.JobId} · {StageName(job.Stage)}" + (job.EncodedSeconds is { } seconds ? $" · {TimeSpan.FromSeconds(Math.Max(0, seconds)):hh\\:mm\\:ss} encoded" : "")));
    public string Last => Available ? Snapshot?.LastOutcome ?? "No jobs completed since the worker started." : "";
    public string Server => Available ? Snapshot?.ServerAddress ?? "Not paired" : "Unavailable";
    public string Version => Snapshot?.Version ?? "Unavailable";
    public string ConnectionDetail => Available ? Snapshot!.Detail : error ?? "Connecting to the local worker…";
    public bool Working => Available && Snapshot!.Jobs.Count > 0;
    public void Update(MonitorSnapshot snapshot) { Snapshot = snapshot; error = null; Changed(); }
    public void Disconnect(string reason) { error = reason; Changed(); }
    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
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
