import Foundation

/// The wire contract this sidecar speaks, mirroring `Optimisarr.Core.Workers.WorkerProtocol`.
///
/// The control plane owns the contract: it picks the highest version both sides support, and a
/// sidecar that can only speak something outside its range is refused rather than assumed
/// compatible. Advertising a range here rather than a single number is what lets this app keep
/// working across a server upgrade.
public enum WorkerProtocol {
    public static let minimum = 1
    public static let maximum = 1
}

/// What this machine has *proved* it can do.
///
/// Named rather than numbered, matching the server contract: an ordinal would change meaning if
/// the set ever gained a member, and this value decides whether a job may be offered at all.
public enum VmafCapability: String, Codable, Sendable, CaseIterable {
    case none = "None"
    case cpu = "Cpu"
    case cuda = "Cuda"
}

/// The capabilities a sidecar reports at pairing.
///
/// Deliberately proved rather than assumed. This build bundles no encoding tools, so it reports
/// none — which means the server's fail-closed capability matcher will never offer it work. That
/// is the correct outcome for a sidecar that cannot yet transcode: honesty here is what stops a
/// job being scheduled somewhere it would only fail.
public struct SidecarCapabilities: Sendable, Equatable {
    public var name: String
    public var operatingSystem: String
    public var architecture: String
    public var videoEncoders: [String]
    public var hardwareDecoders: [String]
    public var vmaf: VmafCapability
    public var freeScratchBytes: Int64
    public var maxConcurrency: Int

    public init(
        name: String,
        operatingSystem: String = "macos",
        architecture: String = SidecarCapabilities.currentArchitecture,
        videoEncoders: [String] = [],
        hardwareDecoders: [String] = [],
        vmaf: VmafCapability = .none,
        freeScratchBytes: Int64 = 0,
        maxConcurrency: Int = 0
    ) {
        self.name = name
        self.operatingSystem = operatingSystem
        self.architecture = architecture
        self.videoEncoders = videoEncoders
        self.hardwareDecoders = hardwareDecoders
        self.vmaf = vmaf
        self.freeScratchBytes = freeScratchBytes
        self.maxConcurrency = maxConcurrency
    }

    /// Reported rather than guessed from the product name, so a Rosetta or Intel host is honest.
    public static var currentArchitecture: String {
        #if arch(arm64)
        return "arm64"
        #elseif arch(x86_64)
        return "x64"
        #else
        return "unknown"
        #endif
    }

    /// What this build can prove today: nothing. No encoders are bundled, and Apple GPUs have no
    /// VMAF compute backend, so even a future build would report CPU VMAF rather than CUDA.
    /// `maxConcurrency` is zero, which reads as "drained" on the server — it will not be offered
    /// work, which is exactly right until this app can do any.
    public static func provenToday(name: String) -> SidecarCapabilities {
        SidecarCapabilities(name: name)
    }
}
