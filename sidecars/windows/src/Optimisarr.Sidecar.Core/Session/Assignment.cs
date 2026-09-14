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
    QualityRequirement Quality);

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
    IReadOnlyList<string> Commands);

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
