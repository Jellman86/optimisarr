import Foundation
import Testing
@testable import SidecarCore

/// Runs the real prober against the real bundled ffmpeg.
///
/// Skipped unless `OPTIMISARR_FFMPEG` points at one, because it needs a build that
/// `scripts/build-ffmpeg.sh` produces. The stubbed tests prove the prober behaves as intended
/// against scripted output; only this proves the intent matches what ffmpeg actually says on real
/// hardware — which is the whole reason the probe confirms with an encode instead of trusting a
/// listing.
///
///     OPTIMISARR_FFMPEG=$(pwd)/vendor/ffmpeg swift test
@Suite("Live capability", .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"] != nil))
struct LiveCapabilityTests {
    private var ffmpeg: URL {
        URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"] ?? "")
    }

    @Test("proves what this machine can really do")
    func provesRealCapabilities() async {
        let capabilities = await CapabilityProber(ffmpeg: ffmpeg).probe(name: "Live probe", maxConcurrency: 2)

        // libx265 is compiled in, so it must be advertised.
        #expect(capabilities.videoEncoders.contains("libx265"))

        // VMAF is the reason this ffmpeg is built from source rather than downloaded; without it
        // the worker could not measure the quality of what it encodes.
        #expect(capabilities.vmaf == .cpu)

        // Free space is read from the real volume, not guessed.
        #expect(capabilities.freeScratchBytes > 0)

        // Something was proved, so capacity is offered.
        #expect(capabilities.maxConcurrency == 2)
    }

    @Test("advertises VideoToolbox encode only after a real encode succeeds")
    func videoToolboxEncodeIsProved() async {
        let capabilities = await CapabilityProber(ffmpeg: ffmpeg).probe(name: "Live probe")

        // On Apple Silicon this should pass its confirmation encode. If it ever does not, the right
        // outcome is absence from this list rather than a claim that fails on the first real job.
        #expect(capabilities.videoEncoders.contains("hevc_videotoolbox"))
    }

    @Test("proves VideoToolbox decode with a real encode-then-decode round trip")
    func videoToolboxDecodeIsProved() async {
        let capabilities = await CapabilityProber(ffmpeg: ffmpeg).probe(name: "Live probe")

        // Listing an accelerator says ffmpeg was compiled for it, not that this machine can open
        // it — so the prober encodes a throwaway clip and decodes it back before claiming decode.
        #expect(capabilities.hardwareDecoders.contains("videotoolbox"))
    }
}
