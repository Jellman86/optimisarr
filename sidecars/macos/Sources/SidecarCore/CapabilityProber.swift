import Foundation

/// Runs a command and returns what it said. Behind a protocol so probing can be tested without a
/// real ffmpeg — the parsing and the confirmation policy are the parts worth pinning, and neither
/// needs an 80MB binary present to verify.
public protocol CommandRunner: Sendable {
    func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: String)
}

public struct ProcessCommandRunner: CommandRunner {
    public init() {}

    public func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: String) {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe

        do {
            try process.run()
        } catch {
            return (-1, error.localizedDescription)
        }

        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return (process.terminationStatus, String(decoding: data, as: UTF8.self))
    }
}

/// Works out what this Mac can actually do.
///
/// Two stages, matching the server's `HardwareCapabilityService`: parse `ffmpeg -encoders` for a
/// cheap first pass, then confirm each hardware encoder with a real throwaway encode. Every Apple
/// ffmpeg build lists VideoToolbox whether or not this particular machine can open it, so listing
/// alone would have the sidecar advertise encoders that fail on first use — and a job scheduled
/// against a false capability is a job that can only fail.
public struct CapabilityProber: Sendable {
    private let runner: CommandRunner
    private let ffmpeg: URL?
    private let scratchDirectory: URL

    public init(
        ffmpeg: URL? = CapabilityProber.bundledFfmpeg(),
        runner: CommandRunner = ProcessCommandRunner(),
        scratchDirectory: URL = FileManager.default.temporaryDirectory
    ) {
        self.ffmpeg = ffmpeg
        self.runner = runner
        self.scratchDirectory = scratchDirectory
    }

    /// The ffmpeg shipped inside the app bundle.
    ///
    /// Bundled rather than found on the system so the worker's build matches the server's. The
    /// roadmap treats FFmpeg build as a scheduling criterion for good reason: the same encode
    /// settings must produce comparable output for the server's verification of a returned
    /// candidate to mean anything.
    public static func bundledFfmpeg() -> URL? {
        guard let resource = Bundle.main.resourceURL?.appendingPathComponent("ffmpeg"),
              FileManager.default.isExecutableFile(atPath: resource.path)
        else {
            return nil
        }
        return resource
    }

    /// What this machine has proved. With no ffmpeg there is nothing to prove, so it reports
    /// nothing and the server's fail-closed matcher simply never offers it work.
    public func probe(name: String, maxConcurrency: Int = 1) async -> SidecarCapabilities {
        guard let ffmpeg else {
            return SidecarCapabilities.provenToday(name: name)
        }

        let listing = await runner.run(ffmpeg, ["-hide_banner", "-encoders"])
        guard listing.exitCode == 0 else {
            return SidecarCapabilities.provenToday(name: name)
        }

        var proved: [String] = []
        for encoder in EncoderListParser.parse(listing.output) {
            if EncoderListParser.needsConfirmation(encoder) {
                let probe = await runner.run(ffmpeg, EncoderProbeCommand.arguments(for: encoder))
                guard probe.exitCode == 0 else { continue }
            }
            proved.append(encoder)
        }

        let filters = await runner.run(ffmpeg, ["-hide_banner", "-filters"])
        let vmaf = filters.exitCode == 0 ? VmafSupportParser.parse(filters.output) : VmafCapability.none

        return SidecarCapabilities(
            name: name,
            videoEncoders: proved,
            hardwareDecoders: [],
            vmaf: vmaf,
            freeScratchBytes: freeScratchBytes(),
            // Nothing to offer means nothing to accept. Reporting concurrency while advertising no
            // encoder would have the server see a live worker it can never actually use.
            maxConcurrency: proved.isEmpty ? 0 : maxConcurrency)
    }

    /// Real free space where the candidate would be written, not a guess. A worker that cannot hold
    /// the output has no business starting the encode, and the server checks this before offering.
    private func freeScratchBytes() -> Int64 {
        let values = try? scratchDirectory.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        return values?.volumeAvailableCapacityForImportantUsage ?? 0
    }
}
