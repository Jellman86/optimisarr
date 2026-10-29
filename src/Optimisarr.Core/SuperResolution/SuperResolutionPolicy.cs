using Optimisarr.Core.Verification;

namespace Optimisarr.Core.SuperResolution;

/// <summary>
/// The verification a super-resolution candidate is judged by. Upscaling is the point of the
/// job: the output is larger than the source by construction, so the size-saving gates — built
/// to stop encodes that do not shrink — would reject every candidate before its quality was
/// ever judged. Every other gate (decodability, timeline, stream retention, perceptual quality)
/// applies unchanged. Opting a library into super-resolution is the operator's explicit
/// acceptance that its outputs may be larger than the files they replace.
/// </summary>
public static class SuperResolutionPolicy
{
    public static VerificationPolicy ForUpscaledCandidate(VerificationPolicy policy) =>
        policy with { RequireSizeReduction = false };
}
