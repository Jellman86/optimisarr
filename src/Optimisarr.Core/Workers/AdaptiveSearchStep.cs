namespace Optimisarr.Core.Workers;

/// <summary>
/// One candidate quality the control plane wants measured, expressed entirely as commands a worker
/// can run.
///
/// <para>The search itself stays here. <see cref="Queue.AdaptiveQualitySearch"/> measures the
/// library's quality first, then brackets up or down depending on whether it passed, then bisects —
/// so each candidate depends on the last and the list cannot be sent up front. Shipping the
/// algorithm to the workers instead would mean writing that bracketing twice, in Swift and in C#,
/// and two implementations of a subtle search is two chances to disagree about what a library's
/// quality means.</para>
///
/// <para>So the worker is a measuring engine: it encodes the sample windows, runs the libvmaf
/// commands it was handed, and reports bytes and raw logs. Every number is parsed, pooled and
/// judged on the control plane by the same code that judges a local search. At most four of these
/// exchanges happen per job, each covering a minute or two of real work, so the round trips cost
/// nothing worth counting.</para>
///
/// <para><b>Why this belongs on the worker at all.</b> A quality chosen by measuring one encoder
/// means nothing on another: the search proves a value by encoding with a specific encoder and
/// scoring the result, and VideoToolbox at the translated equivalent of a QSV value is not the same
/// encode. Running the search where the encode will run is what makes the chosen value true.</para>
/// </summary>
public sealed record AdaptiveSearchStep(
    /// <summary>The candidate being measured, echoed back with the report so the two cannot drift.</summary>
    int Quality,

    /// <summary>
    /// One sample encode per measurement window, carrying <see cref="WorkerProtocol.InputPlaceholder"/>
    /// and <see cref="WorkerProtocol.OutputPlaceholder"/>. Video only: copied audio or subtitles
    /// would dominate the byte comparison the search is making.
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> SampleCommands,

    /// <summary>
    /// How to measure each sample, in the same shape the final verification already uses — so a
    /// worker that can measure a finished candidate can measure a sample with no new machinery.
    /// </summary>
    RemoteQualityContract Measurement);

/// <summary>
/// What a worker measured for one candidate.
///
/// <para>Bytes and raw logs, and nothing else. The worker does not say whether the candidate met
/// the target: that judgement needs the library's policy and the pooling rules, both of which live
/// on the control plane, and a worker asserting it would be asserting something it was never given
/// enough to decide.</para>
/// </summary>
public sealed record AdaptiveSearchReport(
    int Quality,
    long EncodedBytes,
    IReadOnlyList<string> Logs,
    /// <summary>
    /// <see cref="EncodedBytes"/> split by window, in command order. Optional: a worker older than
    /// the per-window size forecast sends only the total, which is still enough to forecast from.
    /// </summary>
    IReadOnlyList<long>? WindowEncodedBytes = null);

/// <summary>
/// The control plane's answer to a report: measure this next, or stop and encode.
///
/// <para><see cref="NextStep"/> and <see cref="SelectedQuality"/> are never both set. A worker that
/// receives a selected quality has finished searching and encodes with the arguments it already
/// holds, at that value.</para>
/// </summary>
public sealed record AdaptiveSearchDirection(
    AdaptiveSearchStep? NextStep,
    int? SelectedQuality,
    string Reason)
{
    public static AdaptiveSearchDirection Measure(AdaptiveSearchStep step, string reason) =>
        new(step, null, reason);

    public static AdaptiveSearchDirection Encode(int quality, string reason) =>
        new(null, quality, reason);
}
