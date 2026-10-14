using Microsoft.Extensions.DependencyInjection;
using Optimisarr.Core.Domain;
using Optimisarr.Core.Library;
using Optimisarr.Core.Queue;
using Optimisarr.Core.Rules;
using Optimisarr.Core.Verification;
using Optimisarr.Core.Workers;

namespace Optimisarr.Api.Queue;

public sealed partial class QueueDispatcher
{
    private async Task<(RemotePerceptualSearchStep Step, FrozenPerceptualEncoding Encoding)> PlanRemotePerceptualAsync(
        JobWork work, CancellationToken token)
    {
        var image = work.Spec.Kind == MediaKind.Image;
        var encoder = image ? work.Spec.ImageEncoder! : work.Spec.AudioEncoder!;
        var saved = image ? work.Spec.ImageQuality ?? ImageTarget.DefaultQuality : work.Spec.AudioBitrateKbps ?? AudioTarget.DefaultBitrateKbps;
        var retry = work.PerceptualRetryCount == 1 && work.PerceptualSearch is { RetryValue: { } floor } previous
            && previous.Encoder == encoder && previous.Kind == work.Spec.Kind
            && previous.MinimumImageScore == work.VerificationPolicy.MinimumImagePerceptualScore
            && previous.MaximumAudioDistance == work.VerificationPolicy.MaximumAudioQualityDistance ? floor : (int?)null;
        var plan = image ? PerceptualQualityPlan.Image(encoder, saved, retry) : PerceptualQualityPlan.Audio(encoder, saved, retry);
        AudioQualityInput? audio = null; ImageQualityMeasurementContext? picture = null;
        if (image)
        {
            if (work.SourcePicture is not { } dimensions)
                throw new InvalidOperationException("Quality-first image work requires known original dimensions.");
            picture = new(dimensions.Width, dimensions.Height, ImageSafety.MayContainAlpha(work.SourcePixelFormat));
        }
        else
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var probe = await scope.ServiceProvider.GetRequiredService<IMediaProbeService>().ProbeAsync(work.Original.Path, token);
            audio = probe.RawJson is { } json ? AudioQualityInput.Parse(json) : null;
            if (audio is null) throw new InvalidOperationException("Quality-first audio work requires one known-duration mono/stereo track.");
        }
        var contract = new RemotePerceptualSearchContract(Guid.NewGuid(), work.Spec.Kind, encoder, plan, work.VerificationPolicy,
            audio is null ? [] : PerceptualSamplePlanner.AudioWindows(audio.DurationSeconds), audio, picture,
            retry is null ? null : work.PerceptualSearch!.SourceSha256);
        var encoding = FrozenPerceptualEncoding.Create(contract, work.Spec, work.Arguments);
        return (RemotePerceptualSearchPlanner.Plan(contract, plan.Baseline, 0, work.Spec), encoding);
    }
}
