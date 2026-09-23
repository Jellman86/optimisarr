namespace Optimisarr.Sidecar.Core.Session;

/// <summary>
/// One piece of work the server has handed over, exactly as the claim route describes it.
///
/// <para>Note what is absent: any path on the server. The source is fetched by lease, and this
/// machine decides where its own scratch lives, so nothing here can point at the server's disk.
/// The arguments carry placeholders instead, and a worker substitutes its own paths and nothing
/// else — it never rewrites the encode the server chose.</para>
/// </summary>
public sealed record Assignment(
    Guid LeaseId,
    int JobId,
    /// <summary>What is being encoded, for a person to read. A job number alone says nothing about
    /// which of their files a machine is busy with.</summary>
    string Title,
    long SourceBytes,
    string VideoEncoder,
    string Vmaf,
    DateTimeOffset ExpiresUtc,
    int RenewWithinSeconds,
    IReadOnlyList<string> Arguments,
    string OutputExtension,
    QualityRequirement Quality,

    /// <summary>
    /// The first candidate of a per-title quality search, when this job needs one. Null when the
    /// quality is already settled and the encode can start immediately — which is also what a
    /// server predating the search sends.
    /// </summary>
    AdaptiveSearchStep? Search = null,
    Optimisarr.Core.Workers.RemoteVerificationContract? FullVerification = null,
    long? MaxCandidateBytes = null,
    long? MinCandidateBytes = null);

/// <summary>
/// One candidate quality the control plane wants measured on this machine.
///
/// <para>The search belongs to the server: it measures the library's quality, then brackets up or
/// down depending on the result, then bisects. This machine never chooses a candidate — it encodes
/// the sample windows it is given, scores them with the commands it is given, and reports. A worker
/// choosing its own candidates would be running a different search from the one the library's
/// quality was defined against.</para>
///
/// <para>Why it happens here rather than on the server: a quality proven by measuring one encoder
/// means nothing on another, so the search has to run on the encoder that will do the encode.</para>
/// </summary>
public sealed record AdaptiveSearchStep(
    int Quality,
    IReadOnlyList<IReadOnlyList<string>> SampleCommands,
    QualityRequirement Measurement);

/// <summary>The server's answer to a reported measurement: measure this next, or stop and encode.</summary>
public sealed record AdaptiveSearchDirection(
    AdaptiveSearchStep? NextStep,
    int? SelectedQuality,
    /// <summary>
    /// The encode to run now the search is over, replacing the arguments the assignment carried.
    /// Those were built before a quality existed and name the library's value, so encoding with
    /// them would run the whole title at the baseline and discard what the search measured.
    /// </summary>
    IReadOnlyList<string>? Arguments)
{
    public bool IsComplete => NextStep is null;
}

/// <summary>
/// What the worker's VMAF evidence would be held to.
///
/// <para>The thresholds and model are stated so a worker measures against the policy the server
/// will judge by: a score taken under an easier policy, or with another model, is evidence about
/// something else entirely and would be worse than no evidence at all.</para>
///
/// <para>Measuring is optional. A worker that returns nothing is not refused — the server simply
/// scores the delivered candidate itself, which it does regardless before accepting anything.</para>
/// </summary>
public sealed record QualityRequirement(
    bool Measure,
    string Model,
    int FrameSubsample,
    bool ClipVmaf,
    double MinimumHarmonicMean,
    double MinimumMinimum,
    /// <summary>
    /// One libvmaf invocation per measurement window, each a full argument list.
    ///
    /// <para>A list of lists, matching what the server sends. This was a flat list of strings until
    /// the search needed to run them, which meant an assignment carrying any measurement command
    /// could not be deserialised at all — the claim threw, the job was never started, and the lease
    /// lapsed in silence two minutes later. That is the shape of the failure PICARD showed on
    /// 2026-09-14, and it went unnoticed because nothing on this platform had ever read the
    /// field.</para>
    /// </summary>
    IReadOnlyList<IReadOnlyList<string>> Commands);

/// <summary>Where a job has got to, as the server's lease renewal understands it.</summary>
public enum RemoteStage
{
    FetchingSource,
    Encoding,
    Measuring,
    Delivering,
}

/// <summary>
/// The placeholders the server puts in an assignment's arguments in place of paths.
///
/// <para>Mirrors <c>Optimisarr.Core.Workers.WorkerProtocol</c>. The output one appears as a prefix
/// carrying the container extension the server chose (<c>{{output}}.mkv</c>), because the extension
/// decides subtitle codecs and muxer and must never be this machine's guess.</para>
/// </summary>
/// <summary>
/// The tokens a measurement command carries in place of paths.
///
/// <para>Separate from the encode's because they name different things: a measurement compares two
/// files and writes a log, where an encode reads one and writes one.</para>
/// </summary>
public static class MeasurementPlaceholders
{
    public const string Distorted = "{{distorted}}";
    public const string Reference = "{{reference}}";
    public const string Log = "{{log}}";

    /// <summary>
    /// Where the candidate's extra lead over the source goes, inside the filter. The server cannot
    /// fill this in: only the machine holding both files can measure it. See <see cref="TimelineLead"/>.
    /// </summary>
    public const string DistortedShift = "{{distortedShift}}";

    /// <summary>
    /// Substitutes this machine's three paths, and nothing else. The filter graph, the model and
    /// the thresholds are the server's: a score taken under different settings is evidence about
    /// something else.
    ///
    /// <para>The two inputs are ordinary arguments and go in as they are. The log is named inside
    /// the filter description, where a Windows path is not a path at all until it is escaped — see
    /// <see cref="FilterPath"/>.</para>
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<string> command,
        string distorted,
        string reference,
        string log,
        string? distortedShift = null) =>
        [.. command.Select(argument =>
        {
            var resolved = argument
                .Replace(Distorted, distorted, StringComparison.Ordinal)
                .Replace(Reference, reference, StringComparison.Ordinal)
                .Replace(Log, FilterPath.ForFilterOption(log), StringComparison.Ordinal);
            // Left in place when there is nothing to substitute, so a command that needed a shift
            // and did not get one fails loudly in FFmpeg rather than silently measuring two
            // timelines that do not line up.
            return distortedShift is null
                ? resolved
                : resolved.Replace(DistortedShift, distortedShift, StringComparison.Ordinal);
        })];
}

public static class AssignmentPlaceholders
{
    public const string Input = "{{input}}";
    public const string Output = "{{output}}";

    /// <summary>
    /// Substitutes this machine's paths into the server's arguments, changing nothing else.
    ///
    /// <para>Whole-token replacement would be wrong: the output appears as a prefix with the
    /// extension appended, so the placeholder has to be replaced within the token rather than
    /// instead of it.</para>
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<string> arguments, string inputPath, string outputPathWithoutExtension) =>
        [.. arguments.Select(argument => argument
            .Replace(Input, inputPath, StringComparison.Ordinal)
            .Replace(Output, outputPathWithoutExtension, StringComparison.Ordinal))];
}
