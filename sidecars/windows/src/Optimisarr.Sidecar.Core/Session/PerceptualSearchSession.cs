using System.Security.Cryptography;
using System.Text.Json;
using Optimisarr.Core.Workers;

namespace Optimisarr.Sidecar.Core.Session;

/// <summary>Consumes server-selected probes and checks every direction against the claim and the actual raw evidence.</summary>
public static class PerceptualSearchSession
{
    public static async Task<PerceptualSearchDirection> RunAsync(RemotePerceptualSearchStep first,
        IReadOnlyList<string> assignedArguments, string sourceHash,
        Func<RemotePerceptualSearchStep, CancellationToken, Task<RemotePerceptualProbeReport>> measure,
        Func<RemotePerceptualProbeReport, CancellationToken, Task<PerceptualSearchDirection>> report,
        CancellationToken token, Func<int, CancellationToken, Task>? retryWait = null)
    {
        token.ThrowIfCancellationRequested();
        if (first is null || assignedArguments is null || measure is null || report is null
            || sourceHash is not { Length: 64 } || !sourceHash.All(Uri.IsHexDigit))
            throw Refused("The search requires its claim, native callbacks and transferred source hash.");
        var arguments = Array.AsReadOnly(assignedArguments.ToArray());
        string frozenJson;
        RemotePerceptualSearchContract contract;
        RemotePerceptualSearchState state;
        try
        {
            RemotePerceptualSearchPlanner.ValidateStep(first);
            frozenJson = JsonSerializer.Serialize(first.Contract, SidecarClient.Json);
            contract = JsonSerializer.Deserialize<RemotePerceptualSearchContract>(frozenJson, SidecarClient.Json)
                ?? throw new ArgumentException("Missing search contract.");
            state = RemotePerceptualSearchState.Start(contract);
            FrozenPerceptualEncoding.SelectArguments(contract, arguments, contract.Plan.Baseline);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        { throw Refused("The frozen search or encoding contract is unsupported."); }
        var step = first;
        for (var index = 0; index < Optimisarr.Core.Queue.PerceptualQualitySearch.MaximumProbes; index++)
        {
            token.ThrowIfCancellationRequested();
            if (JsonSerializer.Serialize(step.Contract, SidecarClient.Json) != frozenJson
                || step.CandidateIndex != state.Probes.Count || step.Value != state.AskedValue)
                throw Refused("The server changed or repeated its frozen sample question.");
            try { RemotePerceptualSearchPlanner.ValidateStep(step); }
            catch (ArgumentException) { throw Refused("The sample commands do not match the frozen search."); }
            var measured = await measure(step, token);
            var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(measured, SidecarClient.Json)));
            var advanced = RemotePerceptualSearchCoordinator.Advance(state, sourceHash, measured, digest);
            if (advanced.State is not { } updated)
                throw Refused("The raw measurement does not match the requested sample: " + advanced.Error);
            state = updated;
            // Keep this exact report across an outage. It may already be committed on the
            // server; measuring again would change timestamps/evidence and become a conflict.
            var direction = await ReportWithRetryAsync(measured, report, retryWait, token);
            token.ThrowIfCancellationRequested();
            if (!direction.Valid) throw Refused("The server returned an ambiguous search direction.");
            if (state.SourceRejected && !direction.SourceBlocked)
                throw Refused("Required original assessment failed; an encode directive cannot override it.");
            if (direction.SourceBlocked) return direction;
            if (state.AskedValue is not null)
            {
                step = direction.NextStep ?? throw Refused("The server tried to encode before the measured search settled.");
                continue;
            }
            if (!state.CanEncode || direction.NextStep is not null || direction.SelectedValue != state.Decision!.SelectedValue)
                throw Refused("The server selection does not match the completed raw measurements.");
            IReadOnlyList<string> expected;
            try { expected = FrozenPerceptualEncoding.SelectArguments(contract, arguments, state.Decision.SelectedValue); }
            catch (ArgumentException) { throw Refused("The selected setting cannot be applied to the frozen encoder command."); }
            if (direction.Arguments is null || !direction.Arguments.SequenceEqual(expected))
                throw Refused("The final command changed intent beyond its measured quality setting.");
            return direction with { Arguments = expected };
        }
        throw Refused("The bounded perceptual search did not settle.");
    }

    private static async Task<PerceptualSearchDirection> ReportWithRetryAsync(RemotePerceptualProbeReport evidence,
        Func<RemotePerceptualProbeReport, CancellationToken, Task<PerceptualSearchDirection>> report,
        Func<int, CancellationToken, Task>? wait, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await report(evidence, token); }
            catch (SidecarException error) when (error.Recoverable && attempt < 4)
            {
                if (wait is not null) await wait(attempt, token);
                else await Task.Delay(TimeSpan.FromSeconds(1 << attempt), token);
            }
        }
    }
    private static SidecarException Refused(string reason) => new(reason, recoverable: false);
}
