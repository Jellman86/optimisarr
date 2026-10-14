using Optimisarr.Core.Domain;

namespace Optimisarr.Core.Workers;

/// <summary>Peak working-space admission, including measurements rather than just the encoded file.</summary>
public static class WorkerScratchBudget
{
    private const long MiB = 1024 * 1024;

    public static long? RequiredBytes(long sourceBytes, long? maximumCandidateBytes, RemotePerceptualSearchContract? search)
    {
        if (sourceBytes <= 0 || maximumCandidateBytes is <= 0) return null;
        try
        {
            checked
            {
                // Preserve the established estimate for assignments without the new workspace.
                if (search is null) return sourceBytes + sourceBytes / 2;
                if (!RemotePerceptualProbeValidator.ValidContract(search)) return null;
                var image = search.Kind == MediaKind.Image;
                // Without a size-saving ceiling, allow the full native-image read budget or
                // the highest audio bitrate over the duration, plus original embedded artwork.
                var output = maximumCandidateBytes ?? (image ? RemotePerceptualProbeValidator.MaximumSampleBytes
                    : sourceBytes + (long)Math.Ceiling(search.SourceAudio!.DurationSeconds * search.Plan.Values.Max() * 1000 / 8) + 16 * MiB);
                var samples = (image ? 1 : search.AudioWindows.Count) * RemotePerceptualProbeValidator.MaximumSampleBytes;
                var references = image ? 0 : search.AudioWindows.Count * RemotePerceptualSearchPlanner.MaximumReferenceBytes;
                var metricScratch = image ? 0 : 24 * MiB;
                // ExifTool atomically rewrites the candidate; both inodes can exist together.
                var metadataRewrite = image && search.Policy.ImageMetadataGateEnabled ? output : 0;
                // Probes are removed between settings, so this does not multiply by the four-probe search limit.
                return sourceBytes + output + samples + references + metricScratch + metadataRewrite + 32 * MiB;
            }
        }
        catch (OverflowException) { return null; }
    }
}
