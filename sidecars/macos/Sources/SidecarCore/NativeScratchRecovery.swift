import Foundation

/// Counts describe owned temporary work only; unknown or busy files remain on disk.
public struct NativeScratchRecovery: Codable, Equatable, Sendable {
    public let removed: Int
    public let busy: Int
    public let deferred: Int
    public let invalid: Int
    public let nextOwner: String?
    public let scanLimited: Bool

    public init(removed: Int = 0, busy: Int = 0, deferred: Int = 0, invalid: Int = 0,
                nextOwner: String? = nil, scanLimited: Bool = false) {
        self.removed = removed; self.busy = busy; self.deferred = deferred; self.invalid = invalid
        self.nextOwner = nextOwner; self.scanLimited = scanLimited
    }

    var isValidPage: Bool {
        let counts = [removed, busy, deferred, invalid]
        guard counts.allSatisfy({ (0...64).contains($0) }) else { return false }
        // A completed 64-owner sweep can discover one additional root-level orphan hold.
        // It is never a clean page or a cursor authorizing another page of owners.
        let finalRootHold = removed == 64 && busy == 0 && deferred == 1 && invalid == 0 && nextOwner == nil
        guard counts.reduce(0, +) <= 64 || finalRootHold else { return false }
        guard let nextOwner else { return true }
        guard nextOwner.utf8.count == 43, nextOwner.hasPrefix("owner-"), nextOwner.hasSuffix(".json") else { return false }
        let nonce = nextOwner.dropFirst(6).dropLast(5).utf8
        return nonce.contains(where: { $0 != 48 }) && nonce.allSatisfy {
            (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0)
        }
    }

    func adding(_ page: Self) -> Self {
        Self(removed: removed + page.removed, busy: busy + page.busy, deferred: deferred + page.deferred,
             invalid: invalid + page.invalid, nextOwner: page.nextOwner, scanLimited: scanLimited || page.scanLimited)
    }
}

/// The helper validates kernel identities and inherited ownership. Swift never recursively
/// deletes native scratch, even when recovery times out or an older helper refuses the request.
public struct NativeScratchRecoveryService: Sendable {
    private let tools: NativeQualityTools
    private let helper: any NativeQualityExchanging
    private let timeout: Duration
    private let maximumPages: Int

    public init(tools: NativeQualityTools, helper: (any NativeQualityExchanging)? = nil,
                timeout: Duration = .seconds(20), maximumPages: Int = 4) {
        self.tools = tools; self.helper = helper ?? NativeQualityHost(executable: tools.host)
        self.timeout = timeout; self.maximumPages = min(4, max(1, maximumPages))
    }

    public static func bundled() -> Self? { NativeQualityTools.bundled().map { Self(tools: $0) } }

    public func recover(root: URL, scope: NativeScratchScope? = nil) async throws -> NativeScratchRecovery {
        guard root.isFileURL, root.path.hasPrefix("/"), root.path.utf8.count <= 4096, !root.path.contains("\0") else {
            throw NativeQualityHostError()
        }
        guard scope == nil || scope!.isValid else { throw NativeQualityHostError() }
        return try await withThrowingTaskGroup(of: NativeScratchRecovery.self) { group in
            group.addTask { try await pages(root: root, scope: scope) }
            group.addTask { try await Task.sleep(for: timeout); throw NativeQualityHostError() }
            defer { group.cancelAll() }
            guard let result = try await group.next() else { throw NativeQualityHostError() }
            return result
        }
    }

    private func pages(root: URL, scope: NativeScratchScope?) async throws -> NativeScratchRecovery {
        var total = NativeScratchRecovery()
        for _ in 0..<maximumPages {
            try Task.checkCancellation()
            let version = scope == nil ? 1 : 2
            var body: [String: Any] = ["version": version, "mode": "recover", "ffmpegPath": tools.ffmpeg.path, "scratchRoot": root.path]
            if let scope { body["accountingScope"] = ["root": scope.root, "control": scope.control, "gate": scope.gate] }
            if let cursor = total.nextOwner { body["recoveryAfterOwner"] = cursor }
            let reply = try await helper.exchange(request: JSONSerialization.data(withJSONObject: body), expectedJobId: nil, report: { _ in })
            try Task.checkCancellation()
            guard reply.version == version, reply.type == "recovered", reply.jobId == nil, !reply.delivered, !reply.capacityBlocked,
                  reply.capabilities == nil, reply.nativeMemoryBytes == nil, reply.requiredScratchBytes == nil,
                  let page = reply.recovery, page.isValidPage,
                  page.nextOwner == nil || total.nextOwner == nil || page.nextOwner! > total.nextOwner! else { throw NativeQualityHostError() }
            total = total.adding(page)
            if page.nextOwner == nil { break }
        }
        return total
    }

    public func recoverWorkingRoots(settings: SettingsSnapshot, defaultRoot: URL = JobRunner.defaultScratchRoot(),
                                    capabilityRoot: URL = FileManager.default.temporaryDirectory) async {
        var roots = [defaultRoot, capabilityRoot]
        if case let .folder(folder) = settings.workLocation { roots.append(folder) }
        var visited = Set<String>()
        for root in roots where visited.insert(root.standardizedFileURL.path).inserted {
            guard !Task.isCancelled else { return }
            do {
                let result = try await recover(root: root)
                if result.removed > 0 { SidecarLog.storage.notice("Reclaimed \(result.removed) native working directories") }
                if result.deferred > 0 || result.invalid > 0 || result.scanLimited || result.nextOwner != nil {
                    SidecarLog.storage.notice("Some native working files remain for a later recovery attempt")
                }
            } catch {
                if !Task.isCancelled { SidecarLog.storage.notice("Native working-file recovery was unavailable; existing files were retained") }
            }
        }
    }
}
