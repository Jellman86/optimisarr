import Foundation
import Testing
@testable import SidecarCore

@Suite("Native work reconciliation")
struct NativeWorkRegistryTests {
    private let boot = UUID()
    private let scope = NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4")
    private func fixture() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    private func record(ram: Bool = false) -> NativeWorkObligation {
        var record = NativeWorkObligation(owner: NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()),
            bootSession: boot, root: ram ? "/Volumes/OptimisarrWork-fixture" : "/previous/actual/folder", scope: scope,
            nativeBytes: 60, scratchBytes: 20, diskVolume: ram ? nil : "disk1", ramBytes: ram ? 40 : 0)
        record.phase = .running; record.receipt = .required
        if ram {
            record.ramCreationAttempted = true
            record.ramIdentity = NativeRamDiskIdentity(bootSession: boot, registryEntryId: 123, backingEntryId: 120, volumeId: UUID(),
                device: "/dev/disk9", mountPoint: record.root!, rootIdentity: scope.root)
        }
        return record
    }

    @Test("a restarted parent restores debt and a busy surviving writer prevents another grant")
    func busyRestart() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("ledger")); defer { journal.close() }
        let old = record(); try journal.save([old])
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget(), cleanup = RegistryCleanupFixture(busy: true)
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: cleanup.operations)
        #expect(memory.reservedBytes == 60)
        #expect(disk.available(volume: "disk1", liveBytes: 100) == 80)
        #expect(try await !registry.recover())
        await #expect(throws: NativeWorkRegistryError.self) {
            try await registry.reserve(jobId: 13, leaseId: UUID(), nativeBytes: 30, scratchBytes: 20, ramBytes: 0,
                ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "disk1", availableBytes: 100))
        }
        #expect(cleanup.calls == ["retire:\(old.root!)"])
        #expect(try journal.load() == [old])
        #expect(memory.reservedBytes == 60)
    }

    @Test("positive writer retirement is durable before consumption and all charges survive a lost response")
    func lostConsumptionResponse() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("ledger")
        let journal = try NativeWorkJournal(directory: directory)
        let old = record(); try journal.save([old]); let id = journal.storeId
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget()
        let cleanup = RegistryCleanupFixture(failConsumption: true, inspect: { stage in
            let current = try journal.load()
            if stage == "consume" {
                #expect(current.first?.phase == .writersRetired && current.first?.receipt == .required)
                #expect(memory.reservedBytes == 60)
            }
        })
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: cleanup.operations)
        await #expect(throws: NativeQualityHostError.self) { try await registry.recover() }
        #expect(memory.reservedBytes == 60 && disk.available(volume: "disk1", liveBytes: 100) == 80)
        #expect(try journal.load().first?.phase == .writersRetired)
        journal.close()
        let reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id); defer { reopened.close() }
        let resumedMemory = MemoryWorkBudget(), resumedDisk = DiskWorkBudget()
        let replay = RegistryCleanupFixture(alreadyAbsent: true)
        let resumed = try NativeWorkRegistry(journal: reopened, bootSession: boot, memory: resumedMemory, disk: resumedDisk,
            retirement: replay.operations)
        #expect(try await resumed.recover())
        #expect(replay.calls == ["consume:\(old.root!)"])
        #expect(resumedMemory.reservedBytes == 0 && resumedDisk.available(volume: "disk1", liveBytes: 100) == 100)
        #expect(try reopened.load().isEmpty)
        #expect(try await resumed.recover())
        #expect(replay.calls.count == 1)
    }

    @Test("RAM receipt consumption precedes ejection and failed ejection retains every charge")
    func failedRamEjection() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("ledger")); defer { journal.close() }
        let old = record(ram: true); try journal.save([old])
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget()
        let cleanup = RegistryCleanupFixture(ejected: false, inspect: { stage in
            let current = try journal.load()
            if stage == "eject" {
                #expect(current.first?.phase == .writersRetired && current.first?.receipt == .consumed)
                #expect(memory.reservedBytes == 100)
            }
        })
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: cleanup.operations)
        #expect(try await !registry.recover())
        #expect(cleanup.calls == ["retire:\(old.root!)", "consume:\(old.root!)", "eject:/dev/disk9"])
        #expect(memory.reservedBytes == 100)
        #expect(try journal.load().first?.receipt == .consumed)
        cleanup.ejected = true
        #expect(try await registry.recover())
        #expect(cleanup.calls.last == "eject:/dev/disk9" && cleanup.calls.count == 4)
        #expect(memory.reservedBytes == 0)
        #expect(try journal.load().isEmpty)
    }

    @Test("a durable reservation is recorded before launch and a never-launched attempt needs no writer receipt")
    func reservedWithoutLaunch() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("ledger")); defer { journal.close() }
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget(), cleanup = RegistryCleanupFixture()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: cleanup.operations)
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 60, scratchBytes: 20, ramBytes: 0,
            ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "disk1", availableBytes: 100))
        #expect(try journal.load().first?.phase == .admitting && memory.reservedBytes == 60)
        try await registry.prepare(owner: owner, root: URL(fileURLWithPath: "/previous/actual/folder"), scope: scope)
        #expect(try journal.load().first?.phase == .reserved)
        #expect(try await registry.finish(owner: owner))
        #expect(cleanup.calls.isEmpty)
        #expect(memory.reservedBytes == 0 && disk.available(volume: "disk1", liveBytes: 100) == 100)
    }

    @Test("unknown RAM allocation stays charged until its boot has ended")
    func unknownRam() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("ledger")
        let journal = try NativeWorkJournal(directory: directory)
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget(), cleanup = RegistryCleanupFixture()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: cleanup.operations)
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 60, scratchBytes: 20, ramBytes: 40,
            ramLimit: 100, availableMemory: 100, disk: nil)
        try await registry.markRamCreationAttempted(owner: owner)
        #expect(try await !registry.recover())
        #expect(memory.reservedBytes == 100 && cleanup.calls.isEmpty)
        let id = journal.storeId; journal.close()
        let reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id); defer { reopened.close() }
        let afterRebootMemory = MemoryWorkBudget()
        let afterReboot = try NativeWorkRegistry(journal: reopened, bootSession: UUID(), memory: afterRebootMemory,
            disk: DiskWorkBudget(), retirement: cleanup.operations)
        #expect(try await afterReboot.recover())
        #expect(afterRebootMemory.reservedBytes == 0 && cleanup.calls.isEmpty)
    }

    @Test("running intent is durable and a no-workspace terminal cannot release a potentially live duplicate owner")
    func runningIntent() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("ledger")); defer { journal.close() }
        let memory = MemoryWorkBudget(), cleanup = RegistryCleanupFixture(busy: true)
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: DiskWorkBudget(),
            retirement: cleanup.operations)
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 60, scratchBytes: 20, ramBytes: 0,
            ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "disk1", availableBytes: 100))
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.markRunning(owner: owner) }
        try await registry.prepare(owner: owner, root: URL(fileURLWithPath: "/previous/actual/folder"), scope: scope)
        try await registry.markRunning(owner: owner)
        #expect(try journal.load().first?.phase == .running && journal.load().first?.receipt == .required)
        let invalid = NativeQualityReply(type: "completed", jobId: owner.jobId, delivered: false, capacityBlocked: false,
            capabilities: nil, nativeWorkspaceCreated: false, nativeResourcesRetired: false)
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.finish(owner: owner, reply: invalid) }
        #expect(memory.reservedBytes == 60 && cleanup.calls.isEmpty)
        let terminal = NativeQualityReply(type: "completed", jobId: owner.jobId, delivered: false, capacityBlocked: false,
            capabilities: nil, version: 2, nativeWorkspaceCreated: false, nativeResourcesRetired: false)
        #expect(try await !registry.finish(owner: owner, reply: terminal))
        #expect(memory.reservedBytes == 60 && cleanup.calls == ["retire:/previous/actual/folder"])
    }

    @Test("failed durable transitions cannot authorize the next cleanup step", arguments: [NativeWorkObligation.Phase.writersRetired, .accountingReleased])
    func failedTransition(phase: NativeWorkObligation.Phase) async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("ledger")
        let initial = try NativeWorkJournal(directory: directory)
        let old = record(); try initial.save([old]); let id = initial.storeId; initial.close()
        let journal = try NativeWorkJournal(directory: directory, expectedStoreId: id, checkpoint: { stage in
            if stage == .replaced {
                let bytes = try Data(contentsOf: directory.appendingPathComponent(NativeWorkJournal.fileName))
                if try NativeWorkLedger.decode(bytes).first?.phase == phase { throw NativeWorkJournalError.unavailable }
            }
        })
        defer { journal.close() }
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget(), cleanup = RegistryCleanupFixture()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: cleanup.operations)
        await #expect(throws: NativeWorkJournalError.self) { try await registry.recover() }
        #expect(memory.reservedBytes == 60)
        #expect(cleanup.calls.count == (phase == .writersRetired ? 1 : 2))
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.recover() }
        let reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id); defer { reopened.close() }
        let resumedCleanup = RegistryCleanupFixture(alreadyAbsent: true)
        let resumed = try NativeWorkRegistry(journal: reopened, bootSession: boot, memory: memory,
            disk: disk, retirement: resumedCleanup.operations)
        #expect(memory.reservedBytes == 60)
        #expect(try await resumed.recover())
        #expect(memory.reservedBytes == 0 && disk.available(volume: "disk1", liveBytes: 100) == 100)
        #expect(resumedCleanup.calls.count == (phase == .writersRetired ? 1 : 0))
    }

    @Test("actor reentrancy cannot grant work while writer retirement is awaiting its helper")
    func concurrentRecovery() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("ledger")); defer { journal.close() }
        let old = record(); try journal.save([old])
        let barrier = AsyncStream<Void>.makeStream()
        defer { barrier.continuation.finish() }
        let cleanup = RegistryCleanupFixture(beforeRetire: { for await _ in barrier.stream { break } })
        let memory = MemoryWorkBudget()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: DiskWorkBudget(),
            retirement: cleanup.operations)
        let recovery = Task { try await registry.recover() }
        try await waitFor("retirement to enter its owned helper") { cleanup.calls.count == 1 }
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.recover() }
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.finish(owner: old.owner) }
        await #expect(throws: NativeWorkRegistryError.self) {
            try await registry.reserve(jobId: 13, leaseId: UUID(), nativeBytes: 10, scratchBytes: 10, ramBytes: 0,
                ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "disk1", availableBytes: 100))
        }
        #expect(memory.reservedBytes == 60)
        barrier.continuation.yield(())
        #expect(try await recovery.value)
        #expect(memory.reservedBytes == 0)
    }

    @Test("two registries sharing a journal cannot overwrite each other's cached obligations")
    func sharedJournal() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("ledger")); defer { journal.close() }
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget(), cleanup = RegistryCleanupFixture()
        let first = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk, retirement: cleanup.operations)
        let second = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk, retirement: cleanup.operations)
        let owner = try await first.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 30, scratchBytes: 20, ramBytes: 0,
            ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "disk1", availableBytes: 100))
        await #expect(throws: NativeWorkRegistryError.self) {
            try await second.reserve(jobId: 13, leaseId: UUID(), nativeBytes: 30, scratchBytes: 20, ramBytes: 0,
                ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "disk1", availableBytes: 100))
        }
        #expect(try journal.load().map(\.owner) == [owner])
        #expect(memory.reservedBytes == 30 && disk.available(volume: "disk1", liveBytes: 100) == 80)
    }
}

private final class RegistryCleanupFixture: @unchecked Sendable {
    private let lock = NSLock()
    private var history: [String] = []
    private var ejectResult: Bool
    let busy: Bool, failConsumption: Bool, alreadyAbsent: Bool
    let inspect: @Sendable (String) throws -> Void
    let beforeRetire: @Sendable () async -> Void
    init(busy: Bool = false, failConsumption: Bool = false, alreadyAbsent: Bool = false, ejected: Bool = true,
         inspect: @escaping @Sendable (String) throws -> Void = { _ in }, beforeRetire: @escaping @Sendable () async -> Void = {}) {
        self.busy = busy; self.failConsumption = failConsumption; self.alreadyAbsent = alreadyAbsent
        self.ejectResult = ejected; self.inspect = inspect; self.beforeRetire = beforeRetire
    }
    var calls: [String] { lock.withLock { history } }
    var ejected: Bool { get { lock.withLock { ejectResult } } set { lock.withLock { ejectResult = newValue } } }
    var operations: NativeWorkRetirement {
        NativeWorkRetirement(retire: { root, _, _ in
            self.lock.withLock { self.history.append("retire:" + root.path) }; try self.inspect("retire")
            await self.beforeRetire()
            return NativeScratchRetirement(retired: !self.busy, busy: self.busy)
        }, consume: { root, _, _ in
            self.lock.withLock { self.history.append("consume:" + root.path) }; try self.inspect("consume")
            if self.failConsumption { throw NativeQualityHostError() }
            return NativeScratchConsumption(consumed: !self.alreadyAbsent, alreadyAbsent: self.alreadyAbsent)
        }, retireRam: { identity in
            self.lock.withLock { self.history.append("eject:" + identity.device) }; try self.inspect("eject")
            return self.ejected
        })
    }
}
