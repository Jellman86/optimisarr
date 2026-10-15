using Optimisarr.Core.Verification;

namespace Optimisarr.Core.Workers;

/// <summary>
/// Explicit wire identity. The report's computed release labels must never relabel an older
/// worker's metric when its JSON is read by a newer server.
/// </summary>
public sealed record RemoteImagePerceptualEvidence(string Metric, string Revision, string Preparation, ImagePerceptualReport Assessment)
{
    public static RemoteImagePerceptualEvidence From(ImagePerceptualReport report) => new(report.Metric, report.Revision, report.Preparation, report);

    public static string? VersionError(RemoteImagePerceptualEvidence? value) => value?.Assessment is null
        ? "The worker returned no native image metric evidence."
        : value.Metric != "ssimulacra2" || value.Revision != ImagePerceptualResultParser.Revision || value.Preparation != ImagePerceptualResultParser.Preparation
            ? "The image metric or preparation does not match this release." : null;
}
