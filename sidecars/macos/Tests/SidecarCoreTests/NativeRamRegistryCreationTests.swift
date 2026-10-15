import Foundation
import Testing
@testable import SidecarCore

@Suite("Registry-backed RAM creation")
struct NativeRamRegistryCreationTests {
    private func root() throws -> URL {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: path, withIntermediateDirectories: true)
        return path
    }
    private func owner(_ registry: NativeWorkRegistry) async throws -> NativeAccountedOwner {
        try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 60, scratchBytes: 20, ramBytes: 512,
            ramLimit: 1000, availableMemory: 1000, disk: nil)
    }
    private func retirement(_ events: RamFactoryEvents) -> NativeWorkRetirement {
        NativeWorkRetirement(retire: { _, _, _ in throw NativeWorkRegistryError.invalid },
            consume: { _, _, _ in throw NativeWorkRegistryError.invalid }, retireRam: { _ in events.add("retire-mounted"); return true })
    }

    @Test("creation intent and partial allocation are durable before effects; mounted identity survives a restart before scope preparation")
    func mountedBeforeScope() async throws {
        let path = try root(); defer { try? FileManager.default.removeItem(at: path) }
        let directory = path.appendingPathComponent("journal")
        let journal = try NativeWorkJournal(directory: directory), boot = UUID(), memory = MemoryWorkBudget(), events = RamFactoryEvents()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: DiskWorkBudget(), retirement: retirement(events))
        let owner = try await owner(registry)
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            #expect((try? journal.load().first?.ramCreationAttempted) == true)
            events.add("attach")
            return FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events, observe: { _ in
                #expect((try? journal.load().first?.ramAllocation) != nil)
            })
        })
        let created = try #require(try await registry.createRamStorage(owner: owner, factory: factory, mountParent: path))
        let mounted = try #require(try journal.load().first)
        #expect(mounted.phase == .admitting && mounted.scope == nil && mounted.ramIdentity == created.identity)
        #expect(memory.reservedBytes == 572 && events.calls.contains("end-creation"))
        let id = journal.storeId; journal.close()
        let reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id); defer { reopened.close() }
        let restored = MemoryWorkBudget()
        let resumed = try NativeWorkRegistry(journal: reopened, bootSession: boot, memory: restored, disk: DiskWorkBudget(), retirement: retirement(events))
        #expect(try await resumed.recover())
        #expect(restored.reservedBytes == 0 && events.calls.last == "retire-mounted")
        #expect(try reopened.load().isEmpty)
    }

    @Test("cancellation leaves all charges and blocks physical retirement until the late formatter ends")
    func cancelledWhileFormatting() async throws {
        let path = try root(); defer { try? FileManager.default.removeItem(at: path) }
        let journal = try NativeWorkJournal(directory: path.appendingPathComponent("journal")); defer { journal.close() }
        let memory = MemoryWorkBudget(), events = RamFactoryEvents(), barrier = DispatchSemaphore(value: 0)
        defer { barrier.signal() }
        let lifecycle = NativeRamDiskLifecycle(retire: { _, _ in false }, retireAllocation: { _, _ in events.add("retire-partial"); return true })
        let retirement = NativeWorkRetirement(retire: { _, _, _ in throw NativeWorkRegistryError.invalid },
            consume: { _, _, _ in throw NativeWorkRegistryError.invalid }, retireRam: { _ in false },
            retireAllocation: { await lifecycle.retire($0) })
        let registry = try NativeWorkRegistry(journal: journal, bootSession: UUID(), memory: memory, disk: DiskWorkBudget(), retirement: retirement)
        let owner = try await owner(registry)
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events, formatBarrier: barrier)
        })
        let creation = Task { try await registry.createRamStorage(owner: owner, factory: factory, mountParent: path) }
        try await waitFor("the formatter to hold its original allocation") { events.calls.contains("format") }
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.finish(owner: owner) }
        await #expect(throws: NativeWorkRegistryError.self) { try await registry.recover() }
        creation.cancel()
        #expect(try await creation.value == nil)
        #expect(memory.reservedBytes == 572)
        #expect(try journal.load().first?.ramAllocation != nil)
        #expect(try await !registry.recover())
        #expect(!events.calls.contains("retire-partial"))
        barrier.signal()
        try await waitFor("the actual formatter lifetime to release its exclusion lease") { events.calls.contains("deinit") }
        #expect(try await registry.recover())
        #expect(memory.reservedBytes == 0 && events.calls.last == "retire-partial")
        #expect(!events.calls.contains("mount"))
    }

    enum Fault: CaseIterable { case intent, allocation, mounted }
    @Test("failed durable creation transitions cannot authorize the next effect", arguments: Fault.allCases)
    func failedCreationPersistence(_ fault: Fault) async throws {
        let path = try root(); defer { try? FileManager.default.removeItem(at: path) }
        let store = CheckpointFixture(), directory = path.appendingPathComponent("journal")
        let journal = try NativeWorkJournal(directory: directory, checkpointStore: store)
        let memory = MemoryWorkBudget(), events = RamFactoryEvents(), boot = UUID()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: DiskWorkBudget(), retirement: retirement(events))
        let owner = try await owner(registry)
        if fault == .intent { store.configure(.beforeFinal) }
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            events.add("attach")
            if fault == .allocation { store.configure(.beforeFinal) }
            return FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events, observe: { stage in
                if fault == .mounted && stage == "mount" { store.configure(.beforeFinal) }
            })
        })
        await #expect(throws: (any Error).self) { try await registry.createRamStorage(owner: owner, factory: factory, mountParent: path) }
        #expect(memory.reservedBytes == 572)
        if fault == .intent { #expect(events.calls.isEmpty) }
        if fault == .allocation { #expect(!events.calls.contains("format")) }
        #expect(!events.calls.contains("end-creation"))
        store.configure(nil); journal.close()
        let reopened = try NativeWorkJournal(directory: directory, checkpointStore: store); defer { reopened.close() }
        #expect(try reopened.load().first?.ramCreationAttempted == true)
        if fault == .mounted {
            let resumed = try NativeWorkRegistry(journal: reopened, bootSession: boot, memory: memory, disk: DiskWorkBudget(), retirement: retirement(events))
            #expect(try await resumed.recover())
            #expect(memory.reservedBytes == 0)
        }
    }
}
