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

/// What the server will hold this worker's quality evidence to. Carried so a measurement can be
/// taken under the same model and thresholds the server judges by; a score under any other
/// policy is evidence about something else.
public struct QualityRequirement: Sendable, Equatable {
    public let measure: Bool
    public let model: String
    public let frameSubsample: Int
    public let clipVmaf: Bool
    public let minimumHarmonicMean: Double
    public let minimumMinimum: Double

    public init(
        measure: Bool, model: String, frameSubsample: Int, clipVmaf: Bool,
        minimumHarmonicMean: Double, minimumMinimum: Double
    ) {
        self.measure = measure
        self.model = model
        self.frameSubsample = frameSubsample
        self.clipVmaf = clipVmaf
        self.minimumHarmonicMean = minimumHarmonicMean
        self.minimumMinimum = minimumMinimum
    }

    init?(json: [String: Any]) {
        guard
            let measure = json["measure"] as? Bool,
            let model = json["model"] as? String,
            let frameSubsample = (json["frameSubsample"] as? NSNumber)?.intValue,
            let clipVmaf = json["clipVmaf"] as? Bool,
            let harmonic = (json["minimumHarmonicMean"] as? NSNumber)?.doubleValue,
            let minimum = (json["minimumMinimum"] as? NSNumber)?.doubleValue
        else { return nil }
        self.init(
            measure: measure, model: model, frameSubsample: frameSubsample, clipVmaf: clipVmaf,
            minimumHarmonicMean: harmonic, minimumMinimum: minimum)
    }
}

/// One job the server has handed this worker, mirroring the server's `AssignmentDto`.
///
/// The argument array is the exact FFmpeg command the server would have run itself, with two
/// tokens standing in for paths. Nothing here is a path on any machine: the source is fetched by
/// lease, and this worker decides where its own scratch lives. That division is what lets the
/// command be validated rather than trusted — see `AssignmentCommand`.
public struct Assignment: Sendable, Equatable {
    public static let inputPlaceholder = "{{input}}"
    public static let outputPlaceholder = "{{output}}"

    public let leaseId: String
    public let jobId: Int
    public let sourceBytes: Int64
    public let videoEncoder: String
    public let renewWithinSeconds: Int
    public let arguments: [String]
    public let outputExtension: String
    public let quality: QualityRequirement

    public init(
        leaseId: String, jobId: Int, sourceBytes: Int64, videoEncoder: String,
        renewWithinSeconds: Int, arguments: [String], outputExtension: String,
        quality: QualityRequirement
    ) {
        self.leaseId = leaseId
        self.jobId = jobId
        self.sourceBytes = sourceBytes
        self.videoEncoder = videoEncoder
        self.renewWithinSeconds = renewWithinSeconds
        self.arguments = arguments
        self.outputExtension = outputExtension
        self.quality = quality
    }

    init?(json: [String: Any]) {
        guard
            let leaseId = json["leaseId"] as? String,
            let jobId = (json["jobId"] as? NSNumber)?.intValue,
            let sourceBytes = (json["sourceBytes"] as? NSNumber)?.int64Value,
            let encoder = json["videoEncoder"] as? String,
            let renew = (json["renewWithinSeconds"] as? NSNumber)?.intValue,
            let arguments = json["arguments"] as? [String],
            let outputExtension = json["outputExtension"] as? String,
            let qualityJson = json["quality"] as? [String: Any],
            let quality = QualityRequirement(json: qualityJson)
        else { return nil }
        self.init(
            leaseId: leaseId, jobId: jobId, sourceBytes: sourceBytes, videoEncoder: encoder,
            renewWithinSeconds: renew, arguments: arguments, outputExtension: outputExtension,
            quality: quality)
    }
}
