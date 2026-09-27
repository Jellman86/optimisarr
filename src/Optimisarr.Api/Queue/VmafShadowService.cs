using Optimisarr.Core.Verification;

namespace Optimisarr.Api.Queue;

/// <summary>
/// Optional server research against files already present for verification. Both models score
/// the same small windows; neither observation is given to VerificationEvaluator. Running before
/// finalisation keeps the source/candidate alive without retaining another copy of either file.
/// </summary>
public sealed class VmafShadowService(
    bool enabled,
    Func<string, string, QualityMeasurementContext, CancellationToken, Task<QualityResult>> measure,
    TimeSpan? timeout = null) : IDisposable
{
    private readonly SemaphoreSlim _slot = new(1, 1);

    public async Task<VmafShadowEvidence?> ObserveAsync(
        string source, string candidate, QualityMeasurementContext context, string? skipReason,
        CancellationToken cancellationToken)
    {
        if (!enabled) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var baselineModel = QualityScoreCommandBuilder.ModelVersionFor(
            context.ReferenceCrop?.Width ?? context.ReferenceWidth,
            context.ReferenceCrop?.Height ?? context.ReferenceHeight);
        var candidateModel = baselineModel == QualityScoreCommandBuilder.UhdModelVersion
            ? VmafShadowPlan.UhdModel : VmafShadowPlan.HdModel;
        var windows = new List<VmafShadowWindow>();
        VmafShadowEvidence Result(string status, string? detail = null) =>
            new(status, detail, windows.ToArray(), baselineModel, candidateModel, DateTimeOffset.UtcNow,
                VmafShadowPlan.RetainContext(context));

        if ((skipReason ?? VmafShadowPlan.SkipReason(context)) is { } reason)
            return Result("Skipped", reason);
        if (!await _slot.WaitAsync(0, cancellationToken))
            return Result("Skipped", "Another server research measurement is running; no observation was queued.");
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
            try
            {
                foreach (var window in VmafShadowPlan.Windows(context.ReferenceDurationSeconds!.Value))
                {
                    var baselineContext = context with
                    {
                        ModelVersion = baselineModel, Acceleration = VmafAcceleration.None, FrameSubsample = 1,
                        ReferenceStartSeconds = window.StartSeconds, DistortedStartSeconds = window.StartSeconds,
                        MeasureDurationSeconds = window.DurationSeconds,
                        DistortedShiftToken = context.PairFramesByNumber ? "0" : null
                    };
                    var baseline = await measure(source, candidate, baselineContext, budget.Token);
                    if (!baseline.Measured || baseline.Scores is null || baseline.DistortedShiftToken is null)
                    {
                        windows.Add(new(window.StartSeconds!.Value, window.DurationSeconds!.Value,
                            baseline.DistortedShiftToken, VmafShadowPlan.RetainScores(baseline.Scores), null));
                        return Result("Unavailable", baseline.Error ?? "Baseline alignment could not be established.");
                    }
                    var observed = await measure(source, candidate, baselineContext with
                    {
                        ModelVersion = candidateModel, DistortedShiftToken = baseline.DistortedShiftToken
                    }, budget.Token);
                    windows.Add(new(window.StartSeconds!.Value, window.DurationSeconds!.Value,
                        baseline.DistortedShiftToken, VmafShadowPlan.RetainScores(baseline.Scores),
                        VmafShadowPlan.RetainScores(observed.Scores)));
                    if (!observed.Measured)
                        return Result("Unavailable", observed.Error ?? "Candidate model could not be measured.");
                    if (!VmafShadowPlan.ValidPair(baseline.Scores, observed.Scores))
                        return Result("Unavailable", "Models did not return finite scores with matching positive frame counts.");
                }
                return Result("Measured");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Result("TimedOut", "The server research time budget expired; normal verification remains authoritative.");
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                return Result("Unavailable", $"Research measurement failed: {exception.Message}");
            }
        }
        finally { _slot.Release(); }
    }

    public void Dispose() => _slot.Dispose();
}
