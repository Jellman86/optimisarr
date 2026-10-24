import Darwin
import Foundation
import Testing
@testable import SidecarCore

extension LiveRamDiskTestSuite {
@Suite("Live registry-backed native RAM factory", .serialized,
    .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_RAMDISK"] != nil && NativeRamDiskLifecycle.isSupported))
struct LiveNativeRamFactoryTests {
    private let bytes: Int64 = 48 * 1024 * 1024
    private func root() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        return root
    }
    private func retirement(_ service: NativeRamDiskLifecycle) -> NativeWorkRetirement {
        NativeWorkRetirement(retire: { _, _, _ in throw NativeWorkRegistryError.invalid },
            consume: { _, _, _ in throw NativeWorkRegistryError.invalid }, retireRam: { await service.retire($0) },
            retireAllocation: { await service.retire($0) })
    }
    private func cleanup(_ journal: NativeWorkJournal, root: URL) {
        let records = try? journal.load()
        journal.close()
        var confirmed = records != nil
        for record in records ?? [] {
            if let mounted = record.ramIdentity {
                _ = MacNativeRamDiskSystem.retire(mounted, permitted: { true })
            }
            if let allocation = record.ramAllocation {
                confirmed = MacNativeRamDiskSystem.retireAllocation(allocation, permitted: { true }) && confirmed
            } else if record.ramCreationAttempted { confirmed = false }
            if let mount = record.ramIdentity?.mountPoint {
                // Nonrecursive removal refuses a replacement mount or unknown contents.
                confirmed = (Darwin.rmdir(mount) == 0 || errno == ENOENT) && confirmed
            }
        }
        if confirmed {
            for child in (try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)) ?? []
                where child.lastPathComponent != "journal" {
                confirmed = Darwin.rmdir(child.path) == 0 && confirmed
            }
        }
        if confirmed {
            let directory = root.appendingPathComponent("journal")
            for name in [NativeWorkJournal.fileName, NativeWorkJournal.coordinatorName] {
                _ = Darwin.unlink(directory.appendingPathComponent(name).path)
            }
            confirmed = Darwin.rmdir(directory.path) == 0 && Darwin.rmdir(root.path) == 0
        }
        if !confirmed { Issue.record("Disposable factory recovery intent retained at \(root.path)") }
        #expect(confirmed)
    }

    @Test("the production factory persists a real mounted root before scope and preserves a busy copy")
    func createAndRetire() async throws {
        let root = try root(), journal = try NativeWorkJournal(directory: root.appendingPathComponent("journal"))
        defer { cleanup(journal, root: root) }
        let memory = MemoryWorkBudget(), lifecycle = NativeRamDiskLifecycle()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: #require(MacNativeRamDiskSystem.bootSession()),
            memory: memory, disk: DiskWorkBudget(), retirement: retirement(lifecycle))
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 1024, scratchBytes: 1024,
            ramBytes: bytes, ramLimit: bytes, availableMemory: bytes + 1024, disk: nil)
        let created = try #require(try await registry.createRamStorage(owner: owner, mountParent: root))
        #expect(try journal.load().first?.ramIdentity == created.identity)
        let canonical = try #require(realpath(root.path, nil)); defer { free(canonical) }
        #expect(created.identity.volumeId != NativeWorkLedger.zero
            && created.identity.mountPoint == String(cString: canonical) + "/" + owner.owner.uuidString)
        let copy = created.disk.mountPoint.appendingPathComponent("owned-copy")
        try Data("factory-owned disposable copy".utf8).write(to: copy)
        let writer = try FileHandle(forUpdating: copy); defer { try? writer.close() }
        #expect(try await !registry.finish(owner: owner))
        #expect(memory.reservedBytes == bytes + 1024)
        #expect(try Data(contentsOf: copy) == Data("factory-owned disposable copy".utf8))
        try writer.close()
        #expect(try await registry.finish(owner: owner))
        #expect(memory.reservedBytes == 0 && (try? journal.load().isEmpty) == true)
    }

    @Test("a real factory volume recovers after parent journal restart before helper scope preparation")
    func restartBeforeScope() async throws {
        let root = try root(), directory = root.appendingPathComponent("journal")
        let journal = try NativeWorkJournal(directory: directory)
        var cleanupJournal = journal
        defer { cleanup(cleanupJournal, root: root) }
        let lifecycle = NativeRamDiskLifecycle(), boot = try #require(MacNativeRamDiskSystem.bootSession())
        let registry = try NativeWorkRegistry(journal: journal, bootSession: boot, memory: MemoryWorkBudget(),
            disk: DiskWorkBudget(), retirement: retirement(lifecycle))
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 1024, scratchBytes: 1024,
            ramBytes: bytes, ramLimit: bytes, availableMemory: bytes + 1024, disk: nil)
        _ = try #require(try await registry.createRamStorage(owner: owner, mountParent: root))
        let id = journal.storeId; journal.close()
        let reopened: NativeWorkJournal
        do { reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id) }
        catch {
            if let usable = try? NativeWorkJournal(directory: directory, expectedStoreId: id) { cleanupJournal = usable }
            throw error
        }
        cleanupJournal = reopened
        let memory = MemoryWorkBudget()
        let resumed = try NativeWorkRegistry(journal: reopened, bootSession: boot, memory: memory, disk: DiskWorkBudget(), retirement: retirement(lifecycle))
        #expect(memory.reservedBytes == bytes + 1024)
        #expect(try await resumed.recover())
        #expect(memory.reservedBytes == 0 && (try? reopened.load().isEmpty) == true)
    }

    @Test("a mount preparation failure retains a known allocation until positive partial retirement")
    func failedMountPreparation() async throws {
        let root = try root(), journal = try NativeWorkJournal(directory: root.appendingPathComponent("journal"))
        defer { cleanup(journal, root: root) }
        let memory = MemoryWorkBudget(), lifecycle = NativeRamDiskLifecycle()
        let registry = try NativeWorkRegistry(journal: journal, bootSession: #require(MacNativeRamDiskSystem.bootSession()),
            memory: memory, disk: DiskWorkBudget(), retirement: retirement(lifecycle))
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 1024, scratchBytes: 1024,
            ramBytes: bytes, ramLimit: bytes, availableMemory: bytes + 1024, disk: nil)
        #expect(try await registry.createRamStorage(owner: owner, mountParent: root.appendingPathComponent("missing-parent")) == nil)
        #expect(memory.reservedBytes == bytes + 1024)
        let record = try #require(try journal.load().first)
        #expect(record.ramAllocation != nil && record.ramIdentity == nil)
        #expect(try await registry.finish(owner: owner))
        #expect(memory.reservedBytes == 0 && (try? journal.load().isEmpty) == true)
    }
}
}
