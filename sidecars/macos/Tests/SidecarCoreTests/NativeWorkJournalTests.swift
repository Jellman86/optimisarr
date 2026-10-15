import Foundation
import Testing
@testable import SidecarCore

@Suite("Durable native work journal")
struct NativeWorkJournalTests {
    private func fixture() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    private func record() -> NativeWorkObligation {
        NativeWorkObligation(owner: NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), bootSession: UUID(),
            root: "/previous/actual/root", scope: NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4"),
            nativeBytes: 512 * 1024 * 1024, scratchBytes: 100_000, diskVolume: "disk1", ramBytes: 0)
    }

    @Test("journal reload retains obligations and its stable lock excludes another parent")
    func restartAndExclusion() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let first = try NativeWorkJournal(directory: directory)
        let obligation = record()
        try first.save([obligation])
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: directory, expectedStoreId: first.storeId) }
        let identity = first.storeId
        first.close(); first.close()
        let second = try NativeWorkJournal(directory: directory, expectedStoreId: identity)
        defer { second.close() }
        #expect(try second.load() == [obligation])
        try second.save([])
        #expect(try second.load().isEmpty)
    }

    @Test("missing pinned journal data never becomes a fresh empty store")
    func missingPinnedData() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let journal = try NativeWorkJournal(directory: directory)
        let identity = journal.storeId; journal.close()
        let path = directory.appendingPathComponent(NativeWorkJournal.fileName)
        try FileManager.default.removeItem(at: path)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: directory, expectedStoreId: identity) }
        #expect(!FileManager.default.fileExists(atPath: path.path))
        try FileManager.default.removeItem(at: directory)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: directory, expectedStoreId: identity) }
        #expect(!FileManager.default.fileExists(atPath: directory.path))
    }

    @Test("corrupt state is retained and does not leave a leaked coordinator lock")
    func corruptState() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let journal = try NativeWorkJournal(directory: directory)
        let identity = journal.storeId; journal.close()
        let path = directory.appendingPathComponent(NativeWorkJournal.fileName)
        try Data("{}".utf8).write(to: path)
        for _ in 0..<2 {
            #expect(throws: NativeWorkLedgerError.self) { try NativeWorkJournal(directory: directory, expectedStoreId: identity) }
        }
        #expect(try Data(contentsOf: path) == Data("{}".utf8))
    }

    @Test("redirected or shared ledger files cannot be read or overwritten as owned accounting")
    func redirectedLedger() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let journal = try NativeWorkJournal(directory: directory)
        defer { journal.close() }
        let path = directory.appendingPathComponent(NativeWorkJournal.fileName)
        let original = root.appendingPathComponent("personal-file")
        try Data("keep original".utf8).write(to: original)
        try FileManager.default.removeItem(at: path)
        try FileManager.default.createSymbolicLink(at: path, withDestinationURL: original)
        #expect(throws: NativeWorkJournalError.self) { try journal.save([]) }
        #expect(try Data(contentsOf: original) == Data("keep original".utf8))
        #expect(try FileManager.default.destinationOfSymbolicLink(atPath: path.path) == original.path)
    }

    @Test("changed coordinator or ledger bytes stop a live parent's commit")
    func changedControl() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let journal = try NativeWorkJournal(directory: directory)
        defer { journal.close() }
        let path = directory.appendingPathComponent(NativeWorkJournal.fileName)
        try NativeWorkLedger.encode([record()]).write(to: path)
        #expect(throws: NativeWorkJournalError.self) { try journal.save([]) }
        #expect(try NativeWorkLedger.decode(Data(contentsOf: path)).count == 1)
    }

    @Test("a post-replacement durability failure closes the journal before it can expose the transition")
    func failedDurability() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let initial = try NativeWorkJournal(directory: directory)
        let id = initial.storeId; initial.close()
        let journal = try NativeWorkJournal(directory: directory, expectedStoreId: id, checkpoint: { stage in
            if stage == .replaced { throw NativeWorkJournalError.unavailable }
        })
        #expect(throws: NativeWorkJournalError.self) { try journal.save([record()]) }
        #expect(throws: NativeWorkJournalError.self) { try journal.load() }
        #expect(throws: NativeWorkJournalError.self) { try journal.save([]) }
        let reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id)
        defer { reopened.close() }
        #expect(try reopened.load().count == 1)
    }

    @Test("a failure before replacement retains the old state and allows a later durable retry")
    func failedBeforeReplacement() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let initial = try NativeWorkJournal(directory: directory)
        let id = initial.storeId; initial.close()
        let journal = try NativeWorkJournal(directory: directory, expectedStoreId: id, checkpoint: { stage in
            if stage == .temporarySynced { throw NativeWorkJournalError.unavailable }
        })
        defer { journal.close() }
        #expect(throws: NativeWorkJournalError.self) { try journal.save([record()]) }
        #expect(try journal.load().isEmpty)
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(Set(files) == Set([NativeWorkJournal.fileName, NativeWorkJournal.coordinatorName]))
    }

    @Test("reopening cannot expose existing bytes without confirming their durability again")
    func reopeningDurability() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let journal = try NativeWorkJournal(directory: directory)
        try journal.save([record()]); let id = journal.storeId; journal.close()
        #expect(throws: NativeWorkJournalError.self) {
            try NativeWorkJournal(directory: directory, expectedStoreId: id, checkpoint: { stage in
                if stage == .reopening { throw NativeWorkJournalError.unavailable }
            })
        }
        let reopened = try NativeWorkJournal(directory: directory, expectedStoreId: id); defer { reopened.close() }
        #expect(try reopened.load().count == 1)
    }

    @Test("hard links and a substituted coordinator do not authorize accounting writes")
    func sharedAndSubstitutedFiles() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let directory = root.appendingPathComponent("accounting")
        let journal = try NativeWorkJournal(directory: directory)
        defer { journal.close() }
        let ledger = directory.appendingPathComponent(NativeWorkJournal.fileName)
        let linked = root.appendingPathComponent("linked")
        try FileManager.default.linkItem(at: ledger, to: linked)
        #expect(throws: NativeWorkJournalError.self) { try journal.load() }
        #expect(throws: NativeWorkJournalError.self) { try journal.save([]) }
        try FileManager.default.removeItem(at: linked)
        let coordinator = directory.appendingPathComponent(NativeWorkJournal.coordinatorName)
        try FileManager.default.removeItem(at: coordinator)
        try Data((journal.storeId.uuidString + "\n").utf8).write(to: coordinator)
        #expect(throws: NativeWorkJournalError.self) { try journal.save([]) }
    }

    @Test("conditional replacement is atomic across parents sharing the journal object")
    func conditionalReplacement() async throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let journal = try NativeWorkJournal(directory: root.appendingPathComponent("accounting")); defer { journal.close() }
        let successes = await withTaskGroup(of: Bool.self, returning: Int.self) { group in
            for _ in 0..<8 {
                let next = record()
                group.addTask {
                    do { try journal.compareAndSave(previous: [], replacement: [next]); return true }
                    catch { return false }
                }
            }
            var count = 0
            for await success in group where success { count += 1 }
            return count
        }
        #expect(successes == 1)
        #expect(try journal.load().count == 1)
    }
}
