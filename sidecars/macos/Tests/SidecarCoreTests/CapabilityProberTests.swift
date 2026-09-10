import Foundation
import Testing
@testable import SidecarCore

/// Answers scripted per command, so the two-stage probe can be walked without a real ffmpeg.
final class ScriptedRunner: CommandRunner, @unchecked Sendable {
    private let lock = NSLock()
    private var replies: [String: (Int32, String)]
    private(set) var probedEncoders: [String] = []

    init(_ replies: [String: (Int32, String)]) { self.replies = replies }

    func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: String) {
        lock.withLock {
            if arguments.contains("-encoders") { return replies["encoders"] ?? (1, "") }
            if arguments.contains("-filters") { return replies["filters"] ?? (1, "") }
            if arguments.contains("-hwaccels") { return replies["hwaccels"] ?? (1, "") }
            // The decode half of the hardware round trip.
            if arguments.contains("-hwaccel") { return replies["decode"] ?? (1, "") }
            // A confirmation encode; remember which encoder was proved.
            if let index = arguments.firstIndex(of: "-c:v"), index + 1 < arguments.count {
                let encoder = arguments[index + 1]
                probedEncoders.append(encoder)
                return replies[encoder] ?? (0, "")
            }
            return (1, "")
        }
    }
}

private let listing = """
 V....D hevc_videotoolbox    VideoToolbox H.265 Encoder
 V....D libx265              libx265 H.265 / HEVC
"""

@Suite("Capability probing")
struct CapabilityProberTests {
    private func prober(_ runner: ScriptedRunner) -> CapabilityProber {
        CapabilityProber(ffmpeg: URL(fileURLWithPath: "/fake/ffmpeg"), runner: runner)
    }

    @Test("claims nothing at all when there is no ffmpeg to ask")
    func noFfmpeg() async {
        // The state this app ships in today. Nothing proved means the server's fail-closed matcher
        // never offers it work, which is correct rather than a limitation to work around.
        let capabilities = await CapabilityProber(ffmpeg: nil).probe(name: "Bare")

        #expect(capabilities.videoEncoders.isEmpty)
        #expect(capabilities.vmaf == .none)
        #expect(capabilities.maxConcurrency == 0)
    }

    @Test("proves a VideoToolbox encoder with a real encode before advertising it")
    func confirmsHardware() async {
        let runner = ScriptedRunner([
            "encoders": (0, listing),
            "filters": (0, "libvmaf"),
            "hevc_videotoolbox": (0, ""),
        ])

        let capabilities = await prober(runner).probe(name: "Mac", maxConcurrency: 2)

        #expect(capabilities.videoEncoders.contains("hevc_videotoolbox"))
        #expect(runner.probedEncoders.contains("hevc_videotoolbox"))
        // CPU encoders are trusted from the listing, exactly as the server treats them.
        #expect(!runner.probedEncoders.contains("libx265"))
        #expect(capabilities.videoEncoders.contains("libx265"))
    }

    @Test("a listed but broken VideoToolbox encoder is not advertised")
    func rejectsBrokenHardware() async {
        // The reason listing alone is not enough: every Apple build lists VideoToolbox, and a job
        // scheduled against a capability that fails on first use can only fail.
        let runner = ScriptedRunner([
            "encoders": (0, listing),
            "filters": (0, "libvmaf"),
            "hevc_videotoolbox": (1, "Error opening encoder"),
        ])

        let capabilities = await prober(runner).probe(name: "Mac")

        #expect(!capabilities.videoEncoders.contains("hevc_videotoolbox"))
        #expect(capabilities.videoEncoders.contains("libx265"))
    }

    @Test("reports CPU VMAF when the filter is present")
    func detectsVmaf() async {
        let runner = ScriptedRunner([
            "encoders": (0, listing), "filters": (0, "libvmaf"), "hevc_videotoolbox": (0, ""),
        ])

        #expect(await prober(runner).probe(name: "Mac").vmaf == .cpu)
    }

    @Test("advertises no concurrency when it has no encoder to offer")
    func noEncodersMeansNoConcurrency() async {
        // Otherwise the server sees a live worker with capacity that it can never actually use.
        let runner = ScriptedRunner(["encoders": (0, " A....D aac  AAC"), "filters": (0, "")])

        let capabilities = await prober(runner).probe(name: "Mac", maxConcurrency: 4)

        #expect(capabilities.videoEncoders.isEmpty)
        #expect(capabilities.maxConcurrency == 0)
    }

    @Test("a failed listing is treated as no capability, not as an error to ignore")
    func failedListing() async {
        let runner = ScriptedRunner(["encoders": (1, "not found")])

        #expect(await prober(runner).probe(name: "Mac").videoEncoders.isEmpty)
    }

    @Test("hardware decode is proved by a real round trip, not by the accelerator listing")
    func provesHardwareDecode() async {
        let runner = ScriptedRunner([
            "encoders": (0, listing), "filters": (0, "libvmaf"),
            "hevc_videotoolbox": (0, ""),
            "hwaccels": (0, "Hardware acceleration methods:\nvideotoolbox"),
            "decode": (0, ""),
        ])

        let capabilities = await prober(runner).probe(name: "Mac")

        #expect(capabilities.hardwareDecoders.contains("videotoolbox"))
    }

    @Test("an accelerator that lists but cannot decode is not advertised")
    func rejectsBrokenDecode() async {
        // Same reasoning as the encoder: listing says ffmpeg was compiled for it, not that this
        // machine can open it.
        let runner = ScriptedRunner([
            "encoders": (0, listing), "filters": (0, "libvmaf"),
            "hevc_videotoolbox": (0, ""),
            "hwaccels": (0, "Hardware acceleration methods:\nvideotoolbox"),
            "decode": (1, "Failed to open decoder"),
        ])

        #expect(await prober(runner).probe(name: "Mac").hardwareDecoders.isEmpty)
    }

    @Test("no hardware encoder means no hardware decode is claimed")
    func conservativeWithoutHardwareEncoder() async {
        // The round trip needs a hardware encoder to produce something to decode. Reporting decode
        // without having proved it would be a guess, and the conservative answer is silence.
        let runner = ScriptedRunner([
            "encoders": (0, " V....D libx265  libx265 H.265 / HEVC"),
            "filters": (0, "libvmaf"),
            "hwaccels": (0, "Hardware acceleration methods:\nvideotoolbox"),
        ])

        #expect(await prober(runner).probe(name: "Mac").hardwareDecoders.isEmpty)
    }
}
