using Optimisarr.Core.Queue;

namespace Optimisarr.Core.Workers;

public sealed record RemotePerceptualToolIdentity(string EncoderSha256, string? MetricSha256, string? FfmpegSha256, string? FfprobeSha256)
{
    public bool CompatibleWith(RemotePerceptualToolIdentity next) => Same(EncoderSha256, next.EncoderSha256)
        && Same(MetricSha256, next.MetricSha256) && Same(FfmpegSha256, next.FfmpegSha256) && Same(FfprobeSha256, next.FfprobeSha256);
    public RemotePerceptualToolIdentity Merge(RemotePerceptualToolIdentity next) => new(EncoderSha256,
        MetricSha256 ?? next.MetricSha256, FfmpegSha256 ?? next.FfmpegSha256, FfprobeSha256 ?? next.FfprobeSha256);
    private static bool Same(string? first, string? second) => first is null || second is null
        || string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Persisted on one lease. A new holder starts a new contract and cannot adopt this selection.</summary>
public sealed record RemotePerceptualSearchState(RemotePerceptualSearchContract Contract,
    IReadOnlyList<PerceptualSearchProbe> Probes, int? AskedValue, PerceptualQualityDecision? Decision = null,
    int? LastReportValue = null, string? LastReportSha256 = null, bool SourceRejected = false, string? SourceError = null,
    string? SourceSha256 = null, RemotePerceptualToolIdentity? Tools = null,
    FrozenPerceptualEncoding? Encoding = null, RemotePerceptualSourceEvidence? Source = null)
{
    public bool CanEncode
    {
        get
        {
            if (SourceRejected || AskedValue is not null || Decision is not { Complete: true }
                || Probes is not { Count: > 0 and <= PerceptualQualitySearch.MaximumProbes }
                || Probes.Any(p => p?.Measurement is null) || !RemotePerceptualProbeValidator.ValidContract(Contract)
                || SourceSha256 is not { Length: 64 } || !SourceSha256.All(Uri.IsHexDigit) || Tools is null
                || Probes.Select(p => p.Value).Distinct().Count() != Probes.Count
                || Probes[0].Value != Contract.Plan.Baseline || Probes.Any(p => !Contract.Plan.Values.Contains(p.Value)
                    || p.EncodedBytes < 0 || p.Measurement.Available && p.EncodedBytes == 0)) return false;
            if (Probes.Any(p => !p.Measurement.Available))
                return Probes.Take(Probes.Count - 1).All(p => p.Measurement.Available)
                    && !Probes[^1].Measurement.Available && Decision.FellBack && Decision.SelectedValue == Contract.Plan.Baseline;
            try
            {
                var expected = PerceptualQualitySearch.Decide(Contract.Plan,
                    Probes.Select(p => new PerceptualQualityProbe(p.Value, p.Measurement.MeetsTarget, p.EncodedBytes)).ToArray());
                return expected.Complete && expected.SelectedValue == Decision.SelectedValue && expected.FellBack == Decision.FellBack;
            }
            catch (ArgumentException) { return false; }
        }
    }

    public static RemotePerceptualSearchState Start(RemotePerceptualSearchContract contract, FrozenPerceptualEncoding? encoding = null)
    {
        if (!RemotePerceptualProbeValidator.ValidContract(contract)) throw new ArgumentException("Invalid perceptual search contract.", nameof(contract));
        if (encoding is not null && encoding.ContractId != contract.Id) throw new ArgumentException("The frozen encode belongs to another search.", nameof(encoding));
        return new(contract, [], contract.Plan.Baseline, Encoding: encoding is null ? null : FrozenPerceptualEncoding.Create(contract, encoding.Spec, encoding.Arguments));
    }
}

public sealed record RemotePerceptualSearchAdvance(RemotePerceptualSearchState? State, bool Duplicate, string? Error)
{
    public bool Accepted => State is not null;
}

/// <summary>Lease-bound, replay-safe advancement of the same bounded search used locally.</summary>
public static class RemotePerceptualSearchCoordinator
{
    public static RemotePerceptualSearchAdvance Advance(RemotePerceptualSearchState state, string sourceHash,
        RemotePerceptualProbeReport report, string reportSha256)
    {
        if (state is null || report is null || !RemotePerceptualProbeValidator.ValidContract(state.Contract)
            || !Hash(sourceHash) || !Hash(reportSha256) || state.Contract.Id != report.ContractId
            || state.Contract.Encoder != report.Encoder || !SameHash(sourceHash, report.SourceSha256)
            || state.SourceSha256 is not null && !SameHash(state.SourceSha256, sourceHash)
            || state.Probes is null || state.Probes.Count > PerceptualQualitySearch.MaximumProbes
            || state.Probes.Any(p => p?.Measurement is null)
            || state.Probes.Select(p => p.Value).Distinct().Count() != state.Probes.Count)
            return Refuse("The report is not bound to this search's source, encoder and contract.");
        if (report.Value == state.LastReportValue)
            return SameHash(reportSha256, state.LastReportSha256) ? new(state, true, null)
                : Refuse("Different evidence is already recorded for that sample setting.");
        if (state.SourceRejected || state.AskedValue is not { } asked || state.Probes.Count >= PerceptualQualitySearch.MaximumProbes)
            return Refuse("This search is settled and has requested no further sample.");
        if (state.Probes.Any(p => !p.Measurement.Available))
            return Refuse("A settled unavailable measurement cannot request another sample.");
        PerceptualQualityDecision expected;
        try
        {
            expected = PerceptualQualitySearch.Decide(state.Contract.Plan,
                state.Probes.Select(p => new PerceptualQualityProbe(p.Value, p.Measurement.MeetsTarget, p.EncodedBytes)).ToArray());
        }
        catch (ArgumentException) { return Refuse("The stored search probes are outside their contract."); }
        if (expected.NextValue != asked) return Refuse("The stored search question does not match its recorded probes.");
        var validated = RemotePerceptualProbeValidator.Validate(state.Contract, asked, sourceHash, report);
        if (validated.SourceRejected)
            return new(state with { AskedValue = null, SourceRejected = true, SourceError = validated.Error,
                LastReportValue = report.Value, LastReportSha256 = reportSha256, SourceSha256 = sourceHash, Source = report.Source }, false, null);
        if (validated.Probe is not { } probe) return Refuse(validated.Error ?? "Incomplete perceptual sample evidence.");
        if (state.Source is { } original && (original.Audio != report.Source.Audio || original.ImageMetadata != report.Source.ImageMetadata))
            return Refuse("The original's audio or metadata evidence changed during this search; start a fresh contract before encoding.");
        var tools = RemotePerceptualProbeValidator.ToolIdentity(state.Contract, report);
        if (state.Tools is not null && !state.Tools.CompatibleWith(tools))
            return Refuse("The worker tools changed during this search; start a fresh contract before encoding.");
        var probes = state.Probes.Append(probe).ToArray();
        var decision = probe.Measurement.Available
            ? PerceptualQualitySearch.Decide(state.Contract.Plan, probes.Select(p => new PerceptualQualityProbe(
                p.Value, p.Measurement.MeetsTarget, p.EncodedBytes)).ToArray())
            : PerceptualQualitySearch.Unavailable(state.Contract.Plan, probe.Measurement.Error);
        return new(state with { Probes = probes, AskedValue = decision.NextValue, Decision = decision,
            LastReportValue = report.Value, LastReportSha256 = reportSha256, SourceSha256 = sourceHash, Tools = state.Tools?.Merge(tools) ?? tools, Source = state.Source ?? report.Source }, false, null);
    }

    private static RemotePerceptualSearchAdvance Refuse(string reason) => new(null, false, reason);
    private static bool Hash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);
    private static bool SameHash(string? expected, string? actual) => Hash(expected) && Hash(actual)
        && string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
}
