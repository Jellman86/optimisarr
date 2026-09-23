import Foundation

/// The wire contract this sidecar speaks, mirroring `Optimisarr.Core.Workers.WorkerProtocol`.
///
/// The control plane owns the contract: it picks the highest version both sides support, and a
/// sidecar that can only speak something outside its range is refused rather than assumed
/// compatible. Advertising a range here rather than a single number is what lets this app keep
/// working across a server upgrade.
public enum WorkerProtocol {
    public static let minimum = 1
    public static let maximum = 2
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
    /// Audio encoders proved on this machine. The server refuses to offer a job that re-encodes
    /// audio to a worker that has not named its encoder, so an empty list means audio-copy work
    /// only rather than "unknown".
    public var audioEncoders: [String]
    public var hardwareDecoders: [String]
    public var vmaf: VmafCapability
    public var freeScratchBytes: Int64
    public var maxConcurrency: Int

    public init(
        name: String,
        operatingSystem: String = "macos",
        architecture: String = SidecarCapabilities.currentArchitecture,
        videoEncoders: [String] = [],
        audioEncoders: [String] = [],
        hardwareDecoders: [String] = [],
        vmaf: VmafCapability = .none,
        freeScratchBytes: Int64 = 0,
        maxConcurrency: Int = 0
    ) {
        self.name = name
        self.operatingSystem = operatingSystem
        self.architecture = architecture
        self.videoEncoders = videoEncoders
        self.audioEncoders = audioEncoders
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
    /// The server's own libvmaf command per measurement window, with path placeholders. Empty
    /// means nothing is to be measured here; an older server sends none.
    public let commands: [[String]]
    public let sampling: String

    public init(
        measure: Bool, model: String, frameSubsample: Int, clipVmaf: Bool,
        minimumHarmonicMean: Double, minimumMinimum: Double,
        commands: [[String]] = [], sampling: String = "None"
    ) {
        self.measure = measure
        self.model = model
        self.frameSubsample = frameSubsample
        self.clipVmaf = clipVmaf
        self.minimumHarmonicMean = minimumHarmonicMean
        self.minimumMinimum = minimumMinimum
        self.commands = commands
        self.sampling = sampling
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
            minimumHarmonicMean: harmonic, minimumMinimum: minimum,
            commands: json["commands"] as? [[String]] ?? [],
            sampling: json["sampling"] as? String ?? "None")
    }

    /// The same contract as it arrives inside a search step, where three of the fields are not
    /// facts about the measurement at all.
    ///
    /// `measure` is implied — a candidate is sent precisely to be measured — and `frameSubsample`
    /// and `clipVmaf` are already baked into the commands by the time they reach this machine.
    /// Requiring them cost a fortnight of searched jobs: a server sent the planner's own five-field
    /// contract, the strict decoder above returned nil, and the step it belonged to became "no
    /// search at all" rather than an error anybody could see. The server now sends one shape for
    /// both, and this stays lenient so a mismatch can never again disable a search in silence.
    init?(measurementJson json: [String: Any]) {
        guard
            let model = json["model"] as? String,
            let harmonic = (json["minimumHarmonicMean"] as? NSNumber)?.doubleValue,
            let minimum = (json["minimumMinimum"] as? NSNumber)?.doubleValue,
            let commands = json["commands"] as? [[String]]
        else { return nil }
        self.init(
            measure: json["measure"] as? Bool ?? true,
            model: model,
            frameSubsample: (json["frameSubsample"] as? NSNumber)?.intValue ?? 1,
            clipVmaf: json["clipVmaf"] as? Bool ?? false,
            minimumHarmonicMean: harmonic, minimumMinimum: minimum,
            commands: commands,
            sampling: json["sampling"] as? String ?? "None")
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
    /// What is being encoded. Empty from a server that predates the field, in which case the menu
    /// falls back to the job number.
    public let title: String
    public let sourceBytes: Int64
    public let videoEncoder: String
    public let renewWithinSeconds: Int
    public let arguments: [String]
    public let outputExtension: String
    public let quality: QualityRequirement
    /// The first candidate of a per-title quality search, when this job needs one. Nil when the
    /// quality is already settled and the encode can start immediately.
    public let search: AdaptiveSearchStep?
    public let fullVerification: FullVerificationContract?
    /// Frozen by the server when a full candidate must be smaller than its source.
    public let maxCandidateBytes: Int64?
    public let minCandidateBytes: Int64?

    public init(
        leaseId: String, jobId: Int, title: String = "", sourceBytes: Int64, videoEncoder: String,
        renewWithinSeconds: Int, arguments: [String], outputExtension: String,
        quality: QualityRequirement, search: AdaptiveSearchStep? = nil,
        fullVerification: FullVerificationContract? = nil, maxCandidateBytes: Int64? = nil,
        minCandidateBytes: Int64? = nil
    ) {
        self.leaseId = leaseId
        self.jobId = jobId
        self.title = title
        self.sourceBytes = sourceBytes
        self.videoEncoder = videoEncoder
        self.renewWithinSeconds = renewWithinSeconds
        self.arguments = arguments
        self.outputExtension = outputExtension
        self.quality = quality
        self.search = search
        self.fullVerification = fullVerification
        self.maxCandidateBytes = maxCandidateBytes
        self.minCandidateBytes = minCandidateBytes
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
        var fullVerification: FullVerificationContract?
        if let raw = json["fullVerification"], !(raw is NSNull) {
            guard let data = try? JSONSerialization.data(withJSONObject: raw),
                  let contract = try? JSONDecoder().decode(FullVerificationContract.self, from: data) else { return nil }
            fullVerification = contract
        }
        self.init(
            leaseId: leaseId, jobId: jobId,
            // Optional: a server that predates the field simply leaves the menu showing the job
            // number, which is what it showed before.
            title: json["title"] as? String ?? "",
            sourceBytes: sourceBytes, videoEncoder: encoder,
            renewWithinSeconds: renew, arguments: arguments, outputExtension: outputExtension,
            quality: quality,
            // Absent from a server that predates the search, and from every job whose quality is
            // already settled — both mean "encode straight away", which is what this app did
            // before the field existed.
            search: Assignment.search(from: json), fullVerification: fullVerification,
            maxCandidateBytes: (json["maxCandidateBytes"] as? NSNumber)?.int64Value,
            minCandidateBytes: (json["minCandidateBytes"] as? NSNumber)?.int64Value)
    }

    /// Says so when a search arrives that cannot be read.
    ///
    /// Nil has two meanings here and only one of them is ordinary: no search was sent, or one was
    /// sent and could not be understood. They looked identical, and the second is the one that
    /// quietly encodes a whole title at the library's baseline.
    private static func search(from json: [String: Any]) -> AdaptiveSearchStep? {
        guard let searchJson = json["search"] as? [String: Any] else { return nil }
        guard let step = AdaptiveSearchStep(json: searchJson) else {
            SidecarLog.job.error(
                "A quality search was offered but could not be read, so this job would encode at the library's quality. Keys: \(searchJson.keys.sorted().joined(separator: ", "), privacy: .public)")
            return nil
        }
        return step
    }
}
