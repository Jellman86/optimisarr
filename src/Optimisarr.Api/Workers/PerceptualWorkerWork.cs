using System.Text.Json;
using System.Text.Json.Serialization;
using Optimisarr.Api.Queue;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Workers;

internal static class PerceptualWorkerWork
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static PerceptualSearchSummary Summary(RemotePerceptualSearchState state) => new(state.Contract.Kind, state.Contract.Encoder,
        state.SourceSha256 ?? "", state.Contract.Policy.MinimumImagePerceptualScore, state.Contract.Policy.MaximumAudioQualityDistance,
        state.Contract.AudioWindows, new(state.Probes, state.Decision ?? new(null, state.Contract.Plan.Baseline, true,
            state.SourceError ?? "Required source assessment is unavailable.")), MeasurementLocation: "Worker",
        PreparationBlocked: state.SourceRejected, SourceImageAssessment: state.Source?.Image?.Assessment);

    public static string Complete(string? snapshot, RemotePerceptualSearchState state, PerceptualSearchSummary summary)
    {
        if (snapshot is null || state.Encoding is null || !state.CanEncode)
            throw new InvalidOperationException("The completed search has no frozen encoding/verification intent.");
        var selected = state.Encoding.Select(state);
        var work = JsonSerializer.Deserialize<QueueDispatcher.JobWork>(snapshot, Json);
        var originalSpec = JsonSerializer.Serialize(state.Encoding.Spec, Json);
        var currentSpec = JsonSerializer.Serialize(work.Spec, Json);
        if (work.Spec is null || (currentSpec != originalSpec && currentSpec != JsonSerializer.Serialize(selected.Spec, Json))
            || JsonSerializer.Serialize(work.VerificationPolicy, Json) != JsonSerializer.Serialize(state.Contract.Policy, Json)
            || work.Arguments is null || !(work.Arguments.SequenceEqual(state.Encoding.Arguments) || work.Arguments.SequenceEqual(selected.Arguments)))
            throw new InvalidOperationException("The frozen encoding or verification contract changed during the search.");
        return JsonSerializer.Serialize(work with { Spec = selected.Spec, Arguments = selected.Arguments, PerceptualSearch = summary }, Json);
    }
}
