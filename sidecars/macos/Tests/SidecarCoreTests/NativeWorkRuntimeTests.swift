import Foundation
import Testing
@testable import SidecarCore

@Suite("Native runtime lifecycle exclusion")
struct NativeWorkRuntimeTests {
    @Test("completed restoration cannot authorize claims while the first unaccounted-owner scan is suspended")
    func firstScanMustComplete() async throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        let gate = NativeRuntimeOpenTestGate()
        let runtime = fixture.runtime(recover: { _ in await gate.wait(); return NativeScratchRecovery() })
        let refresh = Task { await runtime.refresh(roots: [URL(fileURLWithPath: "/fixture")]) }
        try await waitFor("the initial owner scan") { gate.count == 1 }
        #expect(await !runtime.allowsClaims())
        #expect(await runtime.begin(roots: []) == nil)
        gate.finish()
        #expect(await refresh.value == .ready)
        #expect(await runtime.allowsClaims())
    }
    @Test("an explicitly isolated runtime anchors its journal without using the installed store")
    func isolatedAnchoredStore() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let checkpoint = CheckpointFixture()
        let tools = NativeQualityTools(host: URL(fileURLWithPath: "/missing/host"), ffmpeg: URL(fileURLWithPath: "/missing/ffmpeg"))
        let runtime = NativeWorkRuntime(directory: root, checkpointStore: checkpoint, tools: tools,
            memory: MemoryWorkBudget(), disk: DiskWorkBudget())
        #expect(await runtime.refresh(roots: []) == .ready)
        #expect(checkpoint.state?.directory == root.path)
        #expect(checkpoint.state?.pending == nil)
    }
    @Test("disk capacity cannot be borrowed from a different root identity")
    func wrongCapacityRoot() throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        #expect(MacNativeWorkspace.read(fixture.root, scope: NativeScratchScope(root: "darwin:1:2", control: "x", gate: "y")) == nil)
    }
    @Test("startup restores debt before checking missing tools and never grants native authority")
    func missingToolsRestoreDebt() async throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        var record = NativeWorkObligation(owner: NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()),
            bootSession: fixture.boot, nativeBytes: 60, scratchBytes: 20, diskVolume: "fixture", ramBytes: 0)
        record.phase = .admitting
        try fixture.journal.save([record])
        let runtime = fixture.runtime(toolsAvailable: false)
        #expect(await runtime.refresh(roots: []) == .blocked)
        #expect(fixture.memory.reservedBytes == 60)
        #expect(await runtime.begin(roots: []) == nil)
        #expect(try fixture.journal.load() == [record])
    }

    @Test("refresh cannot recover an admitting or reserved record owned by the live attempt", arguments: [false, true])
    func activeReservation(prepared: Bool) async throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        let runtime = fixture.runtime()
        let attempt = try #require(await runtime.begin(roots: []))
        let owner = try await attempt.registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 60, scratchBytes: 20,
            ramBytes: 0, ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "fixture", availableBytes: 100))
        if prepared {
            try await attempt.registry.prepare(owner: owner, root: URL(fileURLWithPath: "/previous/actual/root"),
                scope: NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4"))
        }
        let before = try fixture.journal.load()
        #expect(await runtime.refresh(roots: [URL(fileURLWithPath: "/changed/settings")]) == .active)
        #expect(await runtime.begin(roots: []) == nil)
        #expect(try fixture.journal.load() == before && fixture.memory.reservedBytes == 60)
        #expect(await runtime.end(attempt, owner: owner))
        #expect(fixture.memory.reservedBytes == 0)
        #expect(try fixture.journal.load().isEmpty)
        #expect(await runtime.refresh(roots: []) == .ready)
    }

    @Test("busy or incomplete legacy scans block a grant and are retried", arguments: ["busy", "deferred", "invalid", "limited", "cursor"])
    func incompleteRecovery(reason: String) async throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        let scan = RuntimeScanFixture(reason)
        let runtime = fixture.runtime(recover: scan.recover)
        let root = URL(fileURLWithPath: "/fixture")
        #expect(await runtime.refresh(roots: [root]) == .blocked)
        #expect(await !runtime.allowsClaims())
        #expect(await runtime.begin(roots: [root]) == nil)
        scan.clear()
        #expect(await runtime.refresh(roots: [root]) == .ready)
        #expect(await runtime.allowsClaims())
    }

    @Test("cancelling one startup waiter neither cancels nor duplicates the shared open")
    func startupSingleFlight() async throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        let gate = NativeRuntimeOpenTestGate()
        let runtime = NativeWorkRuntime(open: {
            await gate.wait()
            return try fixture.registry()
        }, toolsAvailable: true, recover: { _ in NativeScratchRecovery() }, openingTimeout: .milliseconds(50))
        let first = Task { await runtime.refresh(roots: []) }
        try await waitFor("startup open") { gate.count == 1 }
        first.cancel()
        #expect(await first.value == .blocked)
        #expect(await runtime.refresh(roots: []) == .blocked)
        #expect(gate.count == 1)
        gate.finish()
        let deadline = ContinuousClock.now.advanced(by: .seconds(2))
        var readiness = await runtime.refresh(roots: [])
        while readiness != .ready, ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
            readiness = await runtime.refresh(roots: [])
        }
        #expect(readiness == .ready)
        #expect(gate.count == 1)
    }

    @Test("an unreadable journal closes native admission without legacy cleanup")
    func unavailableStore() async {
        let runtime = NativeWorkRuntime(open: { throw NativeWorkJournalError.changed }, toolsAvailable: true,
            recover: { _ in Issue.record("Unconfirmed accounting must precede all legacy recovery"); return NativeScratchRecovery() })
        #expect(await runtime.refresh(roots: [URL(fileURLWithPath: "/fixture")]) == .blocked)
        #expect(await runtime.begin(roots: []) == nil)
    }

    @Test("duplicate finalization cannot clear the active guard while the original finalizer is suspended")
    func duplicateFinalization() async throws {
        let fixture = try NativeRuntimeTestFixture(); defer { fixture.close() }
        let gate = NativeRuntimeOpenTestGate()
        let registry = try NativeWorkRegistry(journal: fixture.journal, bootSession: fixture.boot,
            memory: fixture.memory, disk: fixture.disk,
            retirement: NativeWorkRetirement(retire: { _, _, _ in
                await gate.wait(); return NativeScratchRetirement(retired: true, busy: false)
            }, consume: { _, _, _ in NativeScratchConsumption(consumed: true, alreadyAbsent: false) }, retireRam: { _ in false }))
        let runtime = NativeWorkRuntime(open: { registry }, toolsAvailable: true, recover: { _ in NativeScratchRecovery() })
        let attempt = try #require(await runtime.begin(roots: []))
        let owner = try await registry.reserve(jobId: 12, leaseId: UUID(), nativeBytes: 60, scratchBytes: 20, ramBytes: 0,
            ramLimit: 100, availableMemory: 100, disk: NativeDiskCapacity(volume: "fixture", availableBytes: 100))
        try await registry.prepare(owner: owner, root: URL(fileURLWithPath: "/original"),
            scope: NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4"))
        try await registry.markRunning(owner: owner)
        let first = Task { await runtime.end(attempt, owner: owner) }
        try await waitFor("the original finalizer") { gate.count == 1 }
        #expect(await runtime.end(attempt, owner: owner) == false)
        #expect(await runtime.refresh(roots: []) == .active)
        #expect(fixture.memory.reservedBytes == 60)
        gate.finish()
        #expect(await first.value)
        #expect(await runtime.refresh(roots: []) == .ready)
    }
}

final class NativeRuntimeTestFixture: @unchecked Sendable {
    let root: URL, journal: NativeWorkJournal
    let boot = UUID(), memory = MemoryWorkBudget(), disk = DiskWorkBudget()
    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        journal = try NativeWorkJournal(directory: root)
    }
    func close() { journal.close(); try? FileManager.default.removeItem(at: root) }
    func registry() throws -> NativeWorkRegistry {
        try NativeWorkRegistry(journal: journal, bootSession: boot, memory: memory, disk: disk,
            retirement: NativeWorkRetirement(retire: { _, _, _ in NativeScratchRetirement(retired: false, busy: true) },
                consume: { _, _, _ in throw NativeQualityHostError() }, retireRam: { _ in false }))
    }
    func runtime(toolsAvailable: Bool = true,
                 recover: @escaping @Sendable (URL) async throws -> NativeScratchRecovery = { _ in NativeScratchRecovery() }) -> NativeWorkRuntime {
        NativeWorkRuntime(open: { try self.registry() }, toolsAvailable: toolsAvailable, recover: recover)
    }
}

private final class RuntimeScanFixture: @unchecked Sendable {
    let gate = NSLock()
    var reason: String
    init(_ reason: String) { self.reason = reason }
    func clear() { gate.withLock { reason = "" } }
    func recover(_ root: URL) async throws -> NativeScratchRecovery {
        gate.withLock {
            switch reason {
            case "busy": NativeScratchRecovery(busy: 1)
            case "deferred": NativeScratchRecovery(deferred: 1)
            case "invalid": NativeScratchRecovery(invalid: 1)
            case "limited": NativeScratchRecovery(scanLimited: true)
            case "cursor": NativeScratchRecovery(nextOwner: "owner-11111111111111111111111111111111.json")
            default: NativeScratchRecovery()
            }
        }
    }
}

final class NativeRuntimeOpenTestGate: @unchecked Sendable {
    private let gate = NSLock()
    private var continuation: CheckedContinuation<Void, Never>?
    private var calls = 0
    var count: Int { gate.withLock { calls } }
    func wait() async {
        await withCheckedContinuation { continuation in gate.withLock { calls += 1; self.continuation = continuation } }
    }
    func finish() { gate.withLock { let value = continuation; continuation = nil; return value }?.resume() }
}
