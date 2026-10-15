import Foundation
import Testing
@testable import SidecarCore

@Suite("Independently checkpointed native accounting")
struct NativeWorkCheckpointTests {
    private func fixture() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }
    private func record() -> NativeWorkObligation {
        NativeWorkObligation(owner: NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), bootSession: UUID(),
            root: "/original/root", scope: NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4"),
            nativeBytes: 1000, scratchBytes: 2000, diskVolume: "disk1", ramBytes: 0)
    }
    @Test("bootstrap pins the store and each returned transition has an independently committed hash")
    func bootstrap() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture()
        let first = try NativeWorkJournal(directory: root, checkpointStore: store)
        let id = first.storeId
        #expect(store.state?.storeId == id)
        #expect(store.state?.directory == root.path)
        let debt = record()
        try first.save([debt])
        #expect(store.state?.committed == NativeWorkCheckpoint.digest(try Data(contentsOf: root.appendingPathComponent(NativeWorkJournal.fileName))))
        #expect(store.state?.pending == nil)
        #expect(store.writes == 3)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        first.close()
        let reopened = try NativeWorkJournal(directory: root, checkpointStore: store); defer { reopened.close() }
        #expect(try reopened.load() == [debt])
        #expect(reopened.storeId == id)
    }
    @Test("an absent anchor cannot adopt an existing nonempty journal")
    func missingAnchor() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let old = try NativeWorkJournal(directory: root); try old.save([record()]); old.close()
        let before = try Data(contentsOf: root.appendingPathComponent(NativeWorkJournal.fileName)), store = CheckpointFixture()
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        #expect(store.state == nil && store.writes == 0)
        #expect(try Data(contentsOf: root.appendingPathComponent(NativeWorkJournal.fileName)) == before)
    }
    @Test("anchored missing data, another store and valid old snapshots cannot erase accounting")
    func missingAndRollback() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture(), journal = try NativeWorkJournal(directory: root, checkpointStore: store)
        let path = root.appendingPathComponent(NativeWorkJournal.fileName), old = try Data(contentsOf: path)
        try journal.save([record()]); journal.close()
        let current = try Data(contentsOf: path), anchor = store.state
        try old.write(to: path)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        #expect(store.state == anchor)
        try FileManager.default.removeItem(at: path)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        #expect(!FileManager.default.fileExists(atPath: path.path))
        try current.write(to: path, options: .atomic)
        try Data((UUID().uuidString + "\n").utf8).write(to: root.appendingPathComponent(NativeWorkJournal.coordinatorName))
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
    }
    @Test("uncertain pending/final anchor writes close the journal; reopen resolves its durable file", arguments: CheckpointFixture.Fault.allCases)
    func uncertainWrites(_ fault: CheckpointFixture.Fault) throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture(), journal = try NativeWorkJournal(directory: root, checkpointStore: store)
        store.configure(fault)
        let debt = record()
        #expect(throws: (any Error).self) { try journal.save([debt]) }
        #expect(throws: NativeWorkJournalError.self) { try journal.load() }
        #expect(throws: NativeWorkJournalError.self) { try journal.save([]) }
        store.configure(nil)
        let reopened = try NativeWorkJournal(directory: root, checkpointStore: store); defer { reopened.close() }
        #expect(try reopened.load() == (fault.isFinal ? [debt] : []))
        #expect(store.state?.pending == nil)
        #expect(store.state?.committed == NativeWorkCheckpoint.digest(try Data(contentsOf: root.appendingPathComponent(NativeWorkJournal.fileName))))
    }
    @Test("interrupted bootstrap never exposes a journal before its anchor is confirmed", arguments: [CheckpointFixture.Fault.beforePending, .afterPending, .pendingReadback])
    func interruptedBootstrap(_ fault: CheckpointFixture.Fault) throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture(); store.configure(fault)
        #expect(throws: (any Error).self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        store.configure(nil)
        let reopened = try NativeWorkJournal(directory: root, checkpointStore: store); defer { reopened.close() }
        #expect(try reopened.load().isEmpty)
        #expect(store.state?.storeId == reopened.storeId)
    }
    @Test("pending recovery refuses a third older snapshot")
    func unrelatedPendingSnapshot() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture(), journal = try NativeWorkJournal(directory: root, checkpointStore: store)
        let path = root.appendingPathComponent(NativeWorkJournal.fileName), old = try Data(contentsOf: path)
        try journal.save([record()]); store.configure(.beforeFinal)
        #expect(throws: (any Error).self) { try journal.save([record()]) }
        store.configure(nil)
        let pending = store.state
        #expect(pending?.pending != nil)
        try old.write(to: path)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        #expect(store.state == pending)
    }
    @Test("a changed independent anchor vetoes an already open journal")
    func changedAnchor() throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture(), journal = try NativeWorkJournal(directory: root, checkpointStore: store); defer { journal.close() }
        store.substitute(NativeWorkCheckpoint(storeId: UUID(), directory: root.path, committed: String(repeating: "a", count: 64), pending: nil))
        #expect(throws: NativeWorkJournalError.self) { try journal.load() }
        #expect(throws: NativeWorkJournalError.self) { try journal.save([record()]) }
    }
    @Test("filesystem failure after pending intent retains that intent until reopening confirms the durable file",
          arguments: [NativeWorkJournal.Checkpoint.replaced, .filesystemSynced])
    func filesystemBoundary(_ boundary: NativeWorkJournal.Checkpoint) throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture()
        let initial = try NativeWorkJournal(directory: root, checkpointStore: store); initial.close()
        let journal = try NativeWorkJournal(directory: root, checkpointStore: store, checkpoint: { stage in
            if stage == boundary { throw NativeWorkJournalError.unavailable }
        })
        let debt = record()
        #expect(throws: NativeWorkJournalError.self) { try journal.save([debt]) }
        #expect(throws: NativeWorkJournalError.self) { try journal.load() }
        #expect(store.state?.pending != nil)
        let reopened = try NativeWorkJournal(directory: root, checkpointStore: store); defer { reopened.close() }
        #expect(try reopened.load() == [debt])
        #expect(store.state?.pending == nil)
    }

    @Test("reopening and failed recovery confirmation never expose either pending alternative", arguments: [false, true])
    func recoveryBoundary(newFile: Bool) throws {
        let root = try fixture(); defer { try? FileManager.default.removeItem(at: root) }
        let store = CheckpointFixture(), journal = try NativeWorkJournal(directory: root, checkpointStore: store)
        let debt = record()
        store.configure(newFile ? .beforeFinal : .afterPending)
        #expect(throws: (any Error).self) { try journal.save([debt]) }
        store.configure(nil)
        let pending = store.state
        #expect(pending?.pending != nil)
        #expect(throws: NativeWorkJournalError.self) {
            try NativeWorkJournal(directory: root, checkpointStore: store, checkpoint: { stage in
                if stage == .reopening { throw NativeWorkJournalError.unavailable }
            })
        }
        #expect(store.state == pending)
        store.configure(.beforePending)
        #expect(throws: NativeWorkJournalError.self) { try NativeWorkJournal(directory: root, checkpointStore: store) }
        #expect(store.state == pending)
        store.configure(nil)
        let reopened = try NativeWorkJournal(directory: root, checkpointStore: store); defer { reopened.close() }
        #expect(try reopened.load() == (newFile ? [debt] : []))
        #expect(store.state?.pending == nil)
    }

    @Test("anchor bytes reject duplicate, unknown, malformed and oversized state")
    func strictFormat() throws {
        let value = NativeWorkCheckpoint(storeId: UUID(), directory: "/owned/accounting", committed: String(repeating: "a", count: 64), pending: String(repeating: "b", count: 64))
        let bytes = try value.encode()
        #expect(try NativeWorkCheckpoint.decode(bytes) == value)
        var object = try #require(JSONSerialization.jsonObject(with: bytes) as? [String: Any]); object["other"] = true
        #expect(throws: (any Error).self) { try NativeWorkCheckpoint.decode(JSONSerialization.data(withJSONObject: object)) }
        #expect(throws: (any Error).self) { try NativeWorkCheckpoint.decode(Data((String(decoding: bytes.dropLast(), as: UTF8.self) + ",\"pending\":null}").utf8)) }
        #expect(throws: (any Error).self) { try NativeWorkCheckpoint.decode(bytes + Data(repeating: 32, count: 8193 - bytes.count)) }
        #expect(throws: (any Error).self) { try NativeWorkCheckpoint(storeId: value.storeId, directory: value.directory, committed: "bad", pending: nil).encode() }
    }
}

final class CheckpointFixture: NativeWorkCheckpointStore, @unchecked Sendable {
    enum Fault: CaseIterable, Sendable {
        case beforePending, afterPending, pendingReadback, beforeFinal, afterFinal, finalReadback
        var isFinal: Bool { [.beforeFinal, .afterFinal, .finalReadback].contains(self) }
        var index: Int { isFinal ? 2 : 1 }
    }
    private let gate = NSLock()
    private var value: NativeWorkCheckpoint?
    private var fault: Fault?
    private var writeCount = 0
    private var failNextRead = false
    var state: NativeWorkCheckpoint? { gate.withLock { value } }
    var writes: Int { gate.withLock { writeCount } }
    func configure(_ fault: Fault?) { gate.withLock { self.fault = fault; writeCount = 0; failNextRead = false } }
    func substitute(_ value: NativeWorkCheckpoint) { gate.withLock { self.value = value } }
    func read() throws -> NativeWorkCheckpoint? {
        try gate.withLock {
            if failNextRead { failNextRead = false; throw NativeWorkJournalError.unavailable }
            return value
        }
    }
    func write(_ next: NativeWorkCheckpoint, replacing expected: NativeWorkCheckpoint?) throws {
        try gate.withLock {
            guard value == expected else { throw NativeWorkJournalError.changed }
            writeCount += 1
            let active = fault?.index == writeCount ? fault : nil
            if active == .beforePending || active == .beforeFinal { throw NativeWorkJournalError.unavailable }
            value = next
            if active == .afterPending || active == .afterFinal { throw NativeWorkJournalError.unavailable }
            if active == .pendingReadback || active == .finalReadback { failNextRead = true }
        }
    }
}
