import Foundation

/// Private, version-fenced ownership operations carry no pairing, grant or media command.
public struct NativeResourceRetirementService: Sendable {
    private let tools: NativeQualityTools
    private let helper: any NativeQualityExchanging
    private let timeout: Duration

    public init(tools: NativeQualityTools, helper: (any NativeQualityExchanging)? = nil, timeout: Duration = .seconds(10)) {
        self.tools = tools; self.helper = helper ?? NativeQualityHost(executable: tools.host); self.timeout = timeout
    }

    public func prepareScope(root: URL) async throws -> NativeScratchScope {
        let reply = try await exchange(mode: "scope", root: root)
        guard reply.type == "scope", reply.jobId == nil, reply.retirement == nil, reply.consumption == nil,
              let scope = reply.accountingScope, scope.isValid else { throw NativeQualityHostError() }
        return scope
    }

    public func retire(root: URL, owner: NativeAccountedOwner, scope: NativeScratchScope) async throws -> NativeScratchRetirement {
        let reply = try await exchange(mode: "retire", root: root, owner: owner, scope: scope)
        guard reply.type == "retired", reply.jobId == owner.jobId, reply.accountingScope == nil, reply.consumption == nil,
              let retirement = reply.retirement, retirement.isValid else { throw NativeQualityHostError() }
        return retirement
    }

    /// Call only after writer retirement and consume intent are durable outside working media.
    public func consume(root: URL, owner: NativeAccountedOwner, scope: NativeScratchScope) async throws -> NativeScratchConsumption {
        let reply = try await exchange(mode: "forget", root: root, owner: owner, scope: scope)
        guard reply.type == "forgotten", reply.jobId == owner.jobId, reply.accountingScope == nil, reply.retirement == nil,
              let consumption = reply.consumption, consumption.isValid else { throw NativeQualityHostError() }
        return consumption
    }

    private func exchange(mode: String, root: URL, owner: NativeAccountedOwner? = nil,
                          scope: NativeScratchScope? = nil) async throws -> NativeQualityReply {
        try Task.checkCancellation()
        guard [root, tools.ffmpeg, tools.host].allSatisfy({
            $0.isFileURL && $0.path.hasPrefix("/") && $0.path.utf8.count <= 4096 && !$0.path.contains("\0")
        }), mode == "scope" ? owner == nil && scope == nil : owner?.isValid == true && scope?.isValid == true else {
            throw NativeQualityHostError()
        }
        var body: [String: Any] = ["version": 2, "mode": mode, "ffmpegPath": tools.ffmpeg.path, "scratchRoot": root.path]
        if let owner {
            body["accountedOwner"] = ["owner": owner.owner.uuidString, "jobId": owner.jobId, "leaseId": owner.leaseId.uuidString]
        }
        if let scope { body["accountingScope"] = ["root": scope.root, "control": scope.control, "gate": scope.gate] }
        let data = try JSONSerialization.data(withJSONObject: body)
        let reply = try await withThrowingTaskGroup(of: NativeQualityReply.self) { group in
            group.addTask { try await helper.exchange(request: data, expectedJobId: owner?.jobId, report: { _ in }) }
            group.addTask { try await Task.sleep(for: timeout); throw NativeQualityHostError() }
            defer { group.cancelAll() }
            guard let reply = try await group.next() else { throw NativeQualityHostError() }
            return reply
        }
        guard reply.version == 2, !reply.delivered, !reply.capacityBlocked, reply.capabilities == nil,
              reply.nativeMemoryBytes == nil, reply.requiredScratchBytes == nil, reply.recovery == nil,
              reply.nativeWorkspaceCreated == nil, reply.nativeResourcesRetired == nil else { throw NativeQualityHostError() }
        return reply
    }
}
