import Foundation

public enum NativeWorkReadiness: Sendable, Equatable { case ready, active, blocked }

struct NativeWorkAttempt: Sendable {
    let id: UUID
    let registry: NativeWorkRegistry
}

/// One app-scoped owner separates orphan recovery from live execution. Heartbeats may inspect
/// active work but cannot reconcile it, including the interval before a helper is launched.
public actor NativeWorkRuntime {
    public static let shared = NativeWorkRuntime()
    private let open: @Sendable () async throws -> NativeWorkRegistry
    private let toolsAvailable: Bool
    private let recover: @Sendable (URL) async throws -> NativeScratchRecovery
    private let openingTimeout: Duration
    private var registry: NativeWorkRegistry?
    private var opening = false
    private var unavailable = false
    private var active: UUID?
    private var ending = false
    private var maintaining = false
    private var ready = false
    private var accountingTrusted = false

    public func allowsClaims() -> Bool { accountingTrusted && !unavailable }

    public func state() -> NativeWorkReadiness {
        if active != nil { return .active }
        return ready && !maintaining && !unavailable ? .ready : .blocked
    }

    public init(tools: NativeQualityTools? = NativeQualityTools.bundled(),
                helper: (any NativeQualityExchanging)? = nil,
                memory: MemoryWorkBudget = .shared, disk: DiskWorkBudget = .shared) {
        toolsAvailable = tools != nil
        openingTimeout = .seconds(5)
        open = {
            guard let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first,
                  let boot = MacNativeRamDiskSystem.bootSession() else { throw NativeWorkRegistryError.unavailable }
            let directory = base.appendingPathComponent("OptimisarrSidecar/native-accounting", isDirectory: true)
            return try Self.openRegistry(directory: directory, checkpointStore: KeychainNativeWorkCheckpointStore(backend: .fileBased),
                tools: tools, helper: helper, memory: memory, disk: disk, boot: boot)
        }
        recover = { root in
            guard let tools else { throw NativeWorkRegistryError.unavailable }
            return try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root)
        }
    }

    /// Embedders and disposable acceptance processes supply both an independent directory and
    /// checkpoint backend explicitly; neither can accidentally reuse the installed anchor.
    public init(directory: URL, checkpointStore: any NativeWorkCheckpointStore, tools: NativeQualityTools?,
                helper: (any NativeQualityExchanging)? = nil,
                memory: MemoryWorkBudget = .shared, disk: DiskWorkBudget = .shared) {
        toolsAvailable = tools != nil
        openingTimeout = .seconds(5)
        open = {
            guard let boot = MacNativeRamDiskSystem.bootSession() else { throw NativeWorkRegistryError.unavailable }
            return try Self.openRegistry(directory: directory, checkpointStore: checkpointStore, tools: tools,
                helper: helper, memory: memory, disk: disk, boot: boot)
        }
        recover = { root in
            guard let tools else { throw NativeWorkRegistryError.unavailable }
            return try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root)
        }
    }

    private static func openRegistry(directory: URL, checkpointStore: any NativeWorkCheckpointStore,
                                     tools: NativeQualityTools?, helper: (any NativeQualityExchanging)?,
                                     memory: MemoryWorkBudget, disk: DiskWorkBudget, boot: UUID) throws -> NativeWorkRegistry {
        let journal = try NativeWorkJournal(directory: directory, checkpointStore: checkpointStore)
        let service = tools.map { NativeResourceRetirementService(tools: $0, helper: helper) }
        let lifecycle = NativeRamDiskLifecycle.shared
        return try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: NativeWorkRetirement(retire: { root, owner, scope in
                guard let service else { throw NativeWorkRegistryError.unavailable }
                return try await service.retire(root: root, owner: owner, scope: scope)
            }, consume: { root, owner, scope in
                guard let service else { throw NativeWorkRegistryError.unavailable }
                return try await service.consume(root: root, owner: owner, scope: scope)
            }, retireRam: { await lifecycle.retire($0) }, retireAllocation: { await lifecycle.retire($0) }))
    }

    init(open: @escaping @Sendable () async throws -> NativeWorkRegistry, toolsAvailable: Bool,
         recover: @escaping @Sendable (URL) async throws -> NativeScratchRecovery,
         openingTimeout: Duration = .seconds(5)) {
        self.open = open; self.toolsAvailable = toolsAvailable; self.recover = recover
        self.openingTimeout = openingTimeout
    }

    public func refresh(roots: [URL]) async -> NativeWorkReadiness {
        if active != nil { return .active }
        guard !maintaining, let registry = await load(), toolsAvailable else { return .blocked }
        if active != nil { return .active }
        guard !maintaining else { return .blocked }
        ready = false
        accountingTrusted = false
        maintaining = true
        defer { maintaining = false }
        do {
            let reconciled = try await registry.recover()
            guard reconciled else { return .blocked }
            var visited = Set<String>()
            for root in roots where visited.insert(root.standardizedFileURL.path).inserted {
                let result = try await recover(root)
                guard result.busy == 0, result.deferred == 0, result.invalid == 0,
                      !result.scanLimited, result.nextOwner == nil else { return .blocked }
            }
            ready = !Task.isCancelled
            accountingTrusted = ready
            return ready ? .ready : .blocked
        } catch { accountingTrusted = false; return .blocked }
    }

    func begin(roots: [URL]) async -> NativeWorkAttempt? {
        guard !Task.isCancelled, await refresh(roots: roots) == .ready,
              !Task.isCancelled, active == nil, !maintaining, let registry else { return nil }
        let id = UUID(); active = id
        return NativeWorkAttempt(id: id, registry: registry)
    }

    /// Called from the runner's cancellation-independent finalizer. Delivery and retirement
    /// are separate outcomes: a busy owner retains debt even after a successful delivery.
    func end(_ attempt: NativeWorkAttempt, owner: NativeAccountedOwner?, reply: NativeQualityReply? = nil) async -> Bool {
        guard active == attempt.id, !ending else { return false }
        ending = true
        defer { ending = false; ready = false; if active == attempt.id { active = nil } }
        do {
            let retired: Bool
            if let owner { retired = try await attempt.registry.finish(owner: owner, reply: reply) }
            else { retired = try await attempt.registry.recover() }
            // A restored ledger covers our owner, not a legacy survivor outside the ledger.
            // Refresh must exclude those owners again before admitting any new work.
            accountingTrusted = false
            return retired
        } catch { accountingTrusted = false; return false }
    }

    private func load() async -> NativeWorkRegistry? {
        guard !unavailable else { return nil }
        if let registry { return registry }
        if !opening {
            opening = true
            let open = self.open
            // Keychain/filesystem work is single-flight and never runs on the menu actor.
            // A cancelled waiter must not cancel restoration or start a second store owner.
            Task.detached(priority: .utility) {
                do { await self.loaded(try await open()) }
                catch { await self.failedOpening() }
            }
        }
        let deadline = ContinuousClock.now.advanced(by: openingTimeout)
        while opening && ContinuousClock.now < deadline {
            do { try await Task.sleep(for: .milliseconds(10)) }
            catch { return nil }
        }
        return registry
    }

    private func loaded(_ registry: NativeWorkRegistry) { self.registry = registry; accountingTrusted = false; opening = false }
    private func failedOpening() { unavailable = true; accountingTrusted = false; opening = false }
}
