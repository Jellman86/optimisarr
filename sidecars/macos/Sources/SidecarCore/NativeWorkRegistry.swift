import Foundation

public enum NativeWorkRegistryError: Error, Sendable { case unavailable, busy, invalid, capacity, unknownOwner }

public struct NativeWorkRetirement: Sendable {
    let retire: @Sendable (URL, NativeAccountedOwner, NativeScratchScope) async throws -> NativeScratchRetirement
    let consume: @Sendable (URL, NativeAccountedOwner, NativeScratchScope) async throws -> NativeScratchConsumption
    let retireRam: @Sendable (NativeRamDiskIdentity) async throws -> Bool
    let retireAllocation: @Sendable (NativeRamAllocationIdentity) async throws -> Bool

    public init(service: NativeResourceRetirementService,
                retireRam: @escaping @Sendable (NativeRamDiskIdentity) async throws -> Bool,
                retireAllocation: @escaping @Sendable (NativeRamAllocationIdentity) async throws -> Bool = { _ in false }) {
        self.init(retire: service.retire, consume: service.consume, retireRam: retireRam, retireAllocation: retireAllocation)
    }

    init(retire: @escaping @Sendable (URL, NativeAccountedOwner, NativeScratchScope) async throws -> NativeScratchRetirement,
         consume: @escaping @Sendable (URL, NativeAccountedOwner, NativeScratchScope) async throws -> NativeScratchConsumption,
         retireRam: @escaping @Sendable (NativeRamDiskIdentity) async throws -> Bool,
         retireAllocation: @escaping @Sendable (NativeRamAllocationIdentity) async throws -> Bool = { _ in false }) {
        self.retire = retire; self.consume = consume; self.retireRam = retireRam; self.retireAllocation = retireAllocation
    }
}

/// Persistent obligations outlive both the menu-bar app and its helper. New native work is
/// serialized until every previous writer, receipt and owned RAM allocation is reconciled.
public actor NativeWorkRegistry {
    private let journal: NativeWorkJournal
    private let bootSession: UUID
    private let memory: MemoryWorkBudget
    private let disk: DiskWorkBudget
    private let retirement: NativeWorkRetirement
    private var records: [NativeWorkObligation]
    private var reconciling = false
    private var failed = false

    public init(journal: NativeWorkJournal, bootSession: UUID, memory: MemoryWorkBudget = .shared,
                disk: DiskWorkBudget = .shared, retirement: NativeWorkRetirement) throws {
        guard bootSession != NativeWorkLedger.zero else { throw NativeWorkRegistryError.invalid }
        self.journal = journal; self.bootSession = bootSession; self.memory = memory; self.disk = disk; self.retirement = retirement
        records = try journal.load()
        for record in records where record.holdsResources {
            guard memory.restoreObligation(record.owner.owner, nativeBytes: record.nativeBytes, ramBytes: record.ramBytes),
                  record.diskVolume.map({ disk.restoreObligation(record.owner.owner, volume: $0, bytes: record.scratchBytes) }) ?? true
            else { throw NativeWorkRegistryError.unavailable }
        }
    }

    public func reserve(jobId: Int, leaseId: UUID, nativeBytes: Int64, scratchBytes: Int64, ramBytes: Int64,
                        ramLimit: Int64, availableMemory: Int64?, disk capacity: NativeDiskCapacity?) throws -> NativeAccountedOwner {
        try checkAvailable()
        guard records.isEmpty else { throw NativeWorkRegistryError.busy }
        guard jobId > 0, leaseId != NativeWorkLedger.zero, nativeBytes > 0, scratchBytes > 0, ramBytes >= 0,
              ramBytes == 0 ? capacity != nil : capacity == nil else { throw NativeWorkRegistryError.invalid }
        guard let token = memory.reserve(nativeBytes: nativeBytes, ramBytes: ramBytes, ramLimit: ramLimit,
                                         availableMemory: availableMemory) else { throw NativeWorkRegistryError.capacity }
        var reservedDisk = false
        do {
            if let capacity {
                guard disk.reserve(volume: capacity.volume, bytes: scratchBytes, available: capacity.availableBytes,
                                   owner: token) != nil else { throw NativeWorkRegistryError.capacity }
                reservedDisk = true
            }
            let owner = NativeAccountedOwner(owner: token, jobId: jobId, leaseId: leaseId)
            var record = NativeWorkObligation(owner: owner, bootSession: bootSession, nativeBytes: nativeBytes,
                scratchBytes: scratchBytes, diskVolume: capacity?.volume, ramBytes: ramBytes)
            record.phase = .admitting
            try persist([record])
            return owner
        } catch {
            // No helper or RAM creation has been authorized before admission is returned.
            memory.release(token)
            if reservedDisk { disk.release(token) }
            throw error
        }
    }

    public func markRamCreationAttempted(owner: NativeAccountedOwner) throws {
        try checkAvailable()
        var record = try find(owner)
        guard record.phase == .admitting, record.ramBytes > 0, !record.ramCreationAttempted else {
            throw NativeWorkRegistryError.invalid
        }
        record.ramCreationAttempted = true
        try replace(record)
    }

    public func prepare(owner: NativeAccountedOwner, root: URL, scope: NativeScratchScope,
                        ramIdentity: NativeRamDiskIdentity? = nil) throws {
        try checkAvailable()
        var record = try find(owner)
        guard record.phase == .admitting, root.isFileURL,
              record.ramBytes == 0 || record.ramCreationAttempted && record.ramAllocation != nil && ramIdentity != nil else {
            throw NativeWorkRegistryError.invalid
        }
        record.root = root.path; record.scope = scope; record.ramIdentity = ramIdentity; record.phase = .reserved
        guard record.isValid else { throw NativeWorkRegistryError.invalid }
        try replace(record)
    }

    public func recordRamAllocation(owner: NativeAccountedOwner, allocation: NativeRamAllocationIdentity) throws {
        try checkAvailable()
        var record = try find(owner)
        guard record.phase == .admitting, record.ramCreationAttempted, record.ramAllocation == nil else {
            throw NativeWorkRegistryError.invalid
        }
        record.ramAllocation = allocation
        guard record.isValid else { throw NativeWorkRegistryError.invalid }
        try replace(record)
    }

    public func markRunning(owner: NativeAccountedOwner) throws {
        try checkAvailable()
        var record = try find(owner)
        guard record.phase == .reserved else { throw NativeWorkRegistryError.invalid }
        record.phase = .running; record.receipt = .required
        try replace(record)
    }

    public func recover() async throws -> Bool {
        try checkAvailable()
        reconciling = true; defer { reconciling = false }
        for owner in records.map(\.owner) { _ = try await reconcile(owner) }
        return records.isEmpty
    }

    public func finish(owner: NativeAccountedOwner, reply: NativeQualityReply? = nil) async throws -> Bool {
        try checkAvailable()
        let record = try find(owner)
        if let reply {
            guard record.phase == .running, reply.version == 2, reply.type == "completed", reply.jobId == owner.jobId,
                  let created = reply.nativeWorkspaceCreated, let retired = reply.nativeResourcesRetired,
                  created || !retired else { throw NativeWorkRegistryError.invalid }
        }
        reconciling = true; defer { reconciling = false }
        return try await reconcile(owner)
    }

    private func reconcile(_ owner: NativeAccountedOwner) async throws -> Bool {
        var record = try find(owner)
        if record.ramBytes > 0 && record.bootSession != bootSession {
            // RAM and its writers cannot survive a completed machine boot. Do not address a
            // potentially reused disk name or recreate a missing former RAM mount.
            try remove(record)
            memory.release(owner.owner); disk.release(owner.owner)
            return true
        }
        if record.phase == .running {
            guard let root = record.root, let scope = record.scope else { throw NativeWorkRegistryError.invalid }
            let proof = try await retirement.retire(URL(fileURLWithPath: root), owner, scope)
            guard proof.isValid else { throw NativeWorkRegistryError.invalid }
            // A duplicate invocation can return completed without creating a workspace while
            // the original owner is still writing. Only targeted retirement covers that owner.
            guard proof.retired else { return false }
            record.phase = .writersRetired
            try replace(record)
        } else if record.phase == .admitting || record.phase == .reserved {
            record.phase = .writersRetired
            try replace(record)
        }
        if record.receipt == .required {
            guard let root = record.root, let scope = record.scope else { throw NativeWorkRegistryError.invalid }
            let proof = try await retirement.consume(URL(fileURLWithPath: root), owner, scope)
            guard proof.isValid else { throw NativeWorkRegistryError.invalid }
            guard proof.consumed || proof.alreadyAbsent else { return false }
            record.receipt = .consumed
            try replace(record)
        }
        if record.ramBytes > 0 && !record.ramRetired {
            if let identity = record.ramIdentity {
                guard try await retirement.retireRam(identity) else { return false }
            } else if let allocation = record.ramAllocation {
                guard try await retirement.retireAllocation(allocation) else { return false }
            } else if record.ramCreationAttempted { return false }
            record.ramRetired = true
            try replace(record)
        }
        if record.phase != .accountingReleased {
            record.phase = .accountingReleased
            try replace(record)
        }
        memory.release(owner.owner); disk.release(owner.owner)
        try remove(record)
        return true
    }

    private func checkAvailable() throws {
        guard !failed else { throw NativeWorkRegistryError.unavailable }
        guard !reconciling else { throw NativeWorkRegistryError.busy }
        do { try validateSnapshot() }
        catch { failed = true; throw error }
    }

    private func find(_ owner: NativeAccountedOwner) throws -> NativeWorkObligation {
        guard let record = records.first(where: { $0.owner == owner }) else { throw NativeWorkRegistryError.unknownOwner }
        return record
    }

    private func replace(_ record: NativeWorkObligation) throws {
        try persist(records.map { $0.owner == record.owner ? record : $0 })
    }

    private func remove(_ record: NativeWorkObligation) throws { try persist(records.filter { $0.owner != record.owner }) }

    private func persist(_ changed: [NativeWorkObligation]) throws {
        do { try journal.compareAndSave(previous: records, replacement: changed); records = changed }
        catch { failed = true; throw error }
    }

    private func validateSnapshot() throws {
        guard try journal.load() == records else { throw NativeWorkRegistryError.unavailable }
    }
}
