namespace Optimisarr.Core.Workers;

/// <summary>
/// What a remote worker is asked to measure, fixed when the job is claimed and recorded on the
/// lease so the evidence that comes back is judged against exactly what was asked, not against
/// whatever the library's policy says by the time the candidate is verified.
///
/// The commands are the server's own libvmaf invocations, one per measurement window, with three
/// placeholders the worker substitutes for its own scratch paths. The worker runs them and returns
/// the raw libvmaf JSON logs; every number is then parsed, pooled and judged here, by the same code
/// that judges a local measurement. The worker has nothing to compute and nothing to compare.
/// </summary>
public sealed record RemoteQualityContract(
    string Model,
    string Sampling,
    double MinimumHarmonicMean,
    double MinimumMinimum,
    IReadOnlyList<IReadOnlyList<string>> Commands)
{
    public const string DistortedPlaceholder = "{{distorted}}";
    public const string ReferencePlaceholder = "{{reference}}";
    public const string LogPlaceholder = "{{log}}";
    // Substituted by the worker inside the filter graph with the seconds by which its candidate
    // presents each picture later than the source, measured from both files' container and video
    // starts once the encode exists. See QualityMeasurementContext.DistortedShiftToken.
    public const string DistortedShiftPlaceholder = "{{distortedShift}}";

    public int WindowCount => Commands.Count;
}
