import Foundation

/// Tool proof and volatile capacity are separate: only the app may supply current resources.
public struct WorkerPerceptualCapabilities: Encodable, Equatable, Sendable {
    public let imageEncoders: [String]
    public let imageMetadataSupported: Bool
    public let audioMetricRevision: String?
    public let imageMetricRevision: String?
    public var availableMemoryBytes: Int64?
    public var scratchUsesMemory: Bool?

    private struct Proof: Decodable {
        let imageEncoders: [String]?
        let imageMetadataSupported: Bool
        let audioMetricRevision: String?
        let imageMetricRevision: String?
        let availableMemoryBytes: Int64?
        let scratchUsesMemory: Bool?
    }

    init?(proof: Data) {
        guard let wire = try? NativeWireJSON.compact(proof), NativeWireJSON.hasUniqueProperties(wire),
              NativeWireJSON.rootKeys(wire).contains("imageEncoders"),
              let value = try? JSONDecoder().decode(Proof.self, from: wire),
              value.availableMemoryBytes == nil, value.scratchUsesMemory == nil else { return nil }
        let encoders = value.imageEncoders ?? []
        guard encoders.count <= 64, encoders.reduce(0, { $0 + $1.utf8.count + 1 }) <= 1024,
              encoders.allSatisfy({ name in
                  !name.isEmpty && name.utf8.count <= 64 && name.utf8.allSatisfy {
                      (65...90).contains($0) || (97...122).contains($0) || (48...57).contains($0) || $0 == 95
                  }
              }), [value.audioMetricRevision, value.imageMetricRevision].allSatisfy({ revision in
                  revision == nil || revision!.utf8.count == 40 && revision!.utf8.allSatisfy {
                      (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
                  }
              }) else { return nil }
        imageEncoders = encoders; imageMetadataSupported = value.imageMetadataSupported
        audioMetricRevision = value.audioMetricRevision; imageMetricRevision = value.imageMetricRevision
        availableMemoryBytes = nil; scratchUsesMemory = nil
    }

    var wire: [String: Any] {
        var value: [String: Any] = ["imageEncoders": imageEncoders, "imageMetadataSupported": imageMetadataSupported]
        if let audioMetricRevision { value["audioMetricRevision"] = audioMetricRevision }
        if let imageMetricRevision { value["imageMetricRevision"] = imageMetricRevision }
        if let availableMemoryBytes { value["availableMemoryBytes"] = availableMemoryBytes }
        if let scratchUsesMemory { value["scratchUsesMemory"] = scratchUsesMemory }
        return value
    }
}

public struct NativeQualityCapabilityProber: Sendable {
    private let tools: NativeQualityTools
    private let helper: any NativeQualityExchanging
    private let timeout: Duration

    public init(tools: NativeQualityTools, helper: (any NativeQualityExchanging)? = nil, timeout: Duration = .seconds(70)) {
        self.tools = tools; self.helper = helper ?? NativeQualityHost(executable: tools.host); self.timeout = timeout
    }

    public static func bundled() -> Self? { NativeQualityTools.bundled().map { Self(tools: $0) } }

    public func probe(scratchRoot: URL) async -> WorkerPerceptualCapabilities? {
        do {
            try Task.checkCancellation()
            let paths = [tools.host, tools.ffmpeg, tools.imageMetric, tools.audioMetric, tools.metadataTool, scratchRoot].compactMap { $0 }
            guard paths.allSatisfy({ $0.isFileURL && $0.path.hasPrefix("/") && $0.path.utf8.count <= 4096 && !$0.path.contains("\0") }) else { return nil }
            var body: [String: Any] = ["version": 1, "mode": "capabilities", "ffmpegPath": tools.ffmpeg.path, "scratchRoot": scratchRoot.path]
            if let path = tools.imageMetric { body["imageMetric"] = path.path }
            if let path = tools.audioMetric { body["audioMetric"] = path.path }
            if let path = tools.metadataTool { body["metadataTool"] = path.path }
            let request = try JSONSerialization.data(withJSONObject: body)
            let reply = try await withThrowingTaskGroup(of: NativeQualityReply.self) { group in
                group.addTask { try await helper.exchange(request: request, expectedJobId: nil, report: { _ in }) }
                group.addTask { try await Task.sleep(for: timeout); throw NativeQualityHostError() }
                defer { group.cancelAll() }
                guard let reply = try await group.next() else { throw NativeQualityHostError() }
                return reply
            }
            try Task.checkCancellation()
            guard reply.type == "capabilities", reply.jobId == nil, let proof = reply.capabilities else { return nil }
            return WorkerPerceptualCapabilities(proof: proof)
        } catch { return nil }
    }
}

/// Advertise the physical disk fallback conservatively; per-job RAM creation has its own admission.
public struct NativeWorkerResources: Sendable {
    private let settings: SettingsSnapshot
    private let scratchRoot: URL
    private let memoryBudget: MemoryWorkBudget
    private let diskBudget: DiskWorkBudget
    private let availableMemory: @Sendable () -> Int64?
    private let workspace: @Sendable (URL) -> NativeDiskCapacity?

    public init(settings: SettingsSnapshot = SettingsSnapshot(), scratchRoot: URL = JobRunner.defaultScratchRoot(),
                memoryBudget: MemoryWorkBudget = .shared, diskBudget: DiskWorkBudget = .shared,
                availableMemory: @escaping @Sendable () -> Int64? = MacMemoryAvailability.read,
                workspace: @escaping @Sendable (URL) -> NativeDiskCapacity? = NativeQualityJobRunner.availableDiskWorkspace) {
        self.settings = settings; self.scratchRoot = scratchRoot; self.memoryBudget = memoryBudget
        self.diskBudget = diskBudget; self.availableMemory = availableMemory; self.workspace = workspace
    }

    public func refresh(_ capabilities: SidecarCapabilities) -> SidecarCapabilities {
        guard var proof = capabilities.perceptual else { return capabilities }
        var refreshed = capabilities
        let root: URL
        if case let .folder(folder) = settings.current.workLocation { root = folder } else { root = scratchRoot }
        if let disk = workspace(root), let memory = memoryBudget.available(availableMemory()),
           let space = diskBudget.available(volume: disk.volume, liveBytes: disk.availableBytes) {
            refreshed.freeScratchBytes = space; proof.availableMemoryBytes = memory; proof.scratchUsesMemory = false
        } else {
            refreshed.freeScratchBytes = 0; proof.availableMemoryBytes = nil; proof.scratchUsesMemory = nil
        }
        refreshed.perceptual = proof
        return refreshed
    }
}
