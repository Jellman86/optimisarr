import Foundation
import Testing
@testable import SidecarCore

private final class NativeExchangeFixture: NativeQualityExchanging, @unchecked Sendable {
    let budget: MemoryWorkBudget
    let run: @Sendable (Data) async throws -> NativeQualityReply
    private let gate = NSLock()
    private var modes: [String] = []
    var requests: [String] { gate.withLock { modes } }
    init(budget: MemoryWorkBudget, run: @escaping @Sendable (Data) async throws -> NativeQualityReply) {
        self.budget = budget; self.run = run
    }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        let json = try JSONSerialization.jsonObject(with: request) as! [String: Any]
        let mode = json["mode"] as! String
        gate.withLock { modes.append(mode) }
        if mode == "requirements" {
            #expect(json["pairing"] == nil && json["grantedMemoryBytes"] == nil)
            return NativeQualityReply(type: "requirements", jobId: expectedJobId, delivered: false, capacityBlocked: false,
                capabilities: nil, nativeMemoryBytes: 40_000, requiredScratchBytes: 10_000)
        }
        #expect(budget.reservedBytes >= 40_000)
        return try await run(request)
    }
}

@Suite("Native quality job ownership")
struct NativeQualityJobRunnerTests {
    private let tools = NativeQualityTools(host: URL(fileURLWithPath: "/missing/host"), ffmpeg: URL(fileURLWithPath: "/missing/ffmpeg"))
    private let root = URL(fileURLWithPath: "/tmp/native-fixture")
    private let pairing = StoredPairing(serverAddress: "http://localhost:8787", credential: "private-fixture-credential-0123456789", workerId: 4)

    @Test("requirements precede a live memory reservation and the grant stays held through helper completion")
    func reservedRun() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { data in
            let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
            #expect((json["grantedMemoryBytes"] as? NSNumber)?.int64Value == 40_000)
            #expect(budget.reservedBytes == 40_000)
            return NativeQualityReply(type: "completed", jobId: 12, delivered: true, capacityBlocked: false, capabilities: nil)
        }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools,
            helper: helper, scratchRoot: root, memoryBudget: budget, availableMemory: { 100_000 }, workspace: { _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        #expect(outcome == .delivered(jobId: 12, bytes: nil))
        #expect(helper.requests == ["requirements", "run"])
        #expect(budget.reservedBytes == 0)
    }

    @Test("unknown or insufficient current memory and unknown disk volumes release before running the media helper")
    func capacityRefusal() async throws {
        for (memory, disk): (Int64?, Int64?) in [(nil, 100_000), (39_999, 100_000), (100_000, nil), (100_000, 9_999)] {
            let budget = MemoryWorkBudget()
            let helper = NativeExchangeFixture(budget: budget) { _ in Issue.record("No native run may start"); throw NativeQualityHostError() }
            let transport = StubTransport(status: 204)
            let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
                scratchRoot: root, memoryBudget: budget, availableMemory: { memory }, workspace: { _ in disk.map { NativeDiskCapacity(volume: "fixture", availableBytes: $0) } })
            let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
            guard case .capacityBlocked = outcome else { Issue.record("Expected acknowledged capacity refusal"); continue }
            #expect(transport.lastRequest?.url?.path.hasSuffix("/release") == true)
            #expect(helper.requests == ["requirements"])
            #expect(budget.reservedBytes == 0)
        }
    }

    @Test("a failed capacity handback is unconfirmed, never a confirmed cooldown")
    func capacityHandbackFailure() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in throw NativeQualityHostError() }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: NativeLeaseFixture(releaseStatus: 503)), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, availableMemory: { nil }, workspace: { _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .unconfirmed = outcome else { Issue.record("Unacknowledged release must block shutdown"); return }
        #expect(budget.reservedBytes == 0)
    }

    @Test("a generic false helper outcome requires a separate confirmed handback")
    func falseOutcomeNeedsAcknowledgement() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            NativeQualityReply(type: "completed", jobId: 12, delivered: false, capacityBlocked: false, capabilities: nil)
        }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: NativeLeaseFixture(releaseStatus: 503)), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, availableMemory: { 100_000 }, workspace: { _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .unconfirmed = outcome else { Issue.record("False completion alone cannot end a lease"); return }
        #expect(budget.reservedBytes == 0)
    }

    @Test("a RAM job holds the entire volume plus native peak and retains allocated RAM if ejection fails")
    func fullRamAllocation() async throws {
        let budget = MemoryWorkBudget()
        let allocation = try #require(RamDisk.allocationBytes(for: 10_000))
        let helper = NativeExchangeFixture(budget: budget) { data in
            #expect(budget.reservedBytes == 40_000 + allocation)
            let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
            #expect((json["grantedMemoryBytes"] as? NSNumber)?.int64Value == 40_000 + allocation)
            return NativeQualityReply(type: "completed", jobId: 12, delivered: true, capacityBlocked: false, capabilities: nil)
        }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools, helper: helper,
            scratchRoot: root, settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 100_000_000),
            memoryBudget: budget, availableMemory: { 100_000_000 }, workspace: { _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) },
            makeRamDisk: { _ in RamDisk(mountPoint: URL(fileURLWithPath: "/tmp/owned-ram-fixture"), device: "/dev/fixture") },
            destroyRamDisk: { _ in false }, availableScratch: { _ in 100_000 })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        #expect(outcome == .delivered(jobId: 12, bytes: nil))
        #expect(budget.reservedBytes == allocation)
        #expect(budget.reservedRamBytes == allocation)
    }

    @Test("an unavailable RAM preference falls back to a freshly admitted disk job")
    func ramFallback() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            #expect(budget.reservedBytes == 40_000 && budget.reservedRamBytes == 0)
            return NativeQualityReply(type: "completed", jobId: 12, delivered: true, capacityBlocked: false, capabilities: nil)
        }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools, helper: helper,
            scratchRoot: root, settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 100_000_000),
            memoryBudget: budget, availableMemory: { 100_000 }, workspace: { _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) },
            makeRamDisk: { _ in Issue.record("RAM must not allocate beyond live capacity"); return nil })
        #expect(await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in } == .delivered(jobId: 12, bytes: nil))
        #expect(budget.reservedBytes == 0)
    }
    @Test("memory pressure appearing during RAM-volume creation refuses before the media helper runs and ejects the volume")
    func pressureDuringCreation() async throws {
        let live = NativeFixtureState()
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            Issue.record("Stale capacity cannot start the media helper")
            return NativeQualityReply(type: "completed", jobId: 12, delivered: true, capacityBlocked: false, capabilities: nil)
        }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools, helper: helper,
            scratchRoot: root, settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 100_000_000),
            memoryBudget: budget, availableMemory: live.readMemory, workspace: { _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) },
            makeRamDisk: { _ in live.loseCapacity(); return RamDisk(mountPoint: URL(fileURLWithPath: "/tmp/owned-ram-fixture"), device: "/dev/fixture") },
            destroyRamDisk: { _ in live.eject(); return true }, availableScratch: { _ in 100_000 })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .capacityBlocked = outcome else { Issue.record("Expected capacity hand-back before run"); return }
        #expect(helper.requests == ["requirements"])
        #expect(live.ejected)
        #expect(budget.reservedBytes == 0)
    }

    @Test("cancellation keeps memory reserved until the helper has finished stopping its children")
    func cancellationOwnership() async throws {
        let budget = MemoryWorkBudget()
        let cleanup = NativeCleanupGate()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            try await withTaskCancellationHandler {
                await cleanup.wait()
                throw CancellationError()
            } onCancel: { cleanup.cancelled() }
        }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, availableMemory: { 100_000 },
            workspace: { _ in NativeDiskCapacity(volume: "cancel-fixture", availableBytes: 100_000) })
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        let task = Task { await runner.execute(assignment, pairing: pairing) { _ in } }
        try await waitFor("the native helper to run") { helper.requests.contains("run") && cleanup.waiting }
        task.cancel()
        try await waitFor("owned child cancellation to begin") { cleanup.wasCancelled }
        #expect(budget.reservedBytes == 40_000)
        cleanup.finish()
        let result = await task.value
        guard case .released = result else { Issue.record("Cancelled work must hand back"); return }
        #expect(budget.reservedBytes == 0)
    }

    @Test("a lease lost during workspace preparation cannot start media work")
    func preparationLeaseLost() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            Issue.record("The media helper must not start after lease loss")
            return NativeQualityReply(type: "completed", jobId: 12, delivered: true, capacityBlocked: false, capabilities: nil)
        }
        let transport = NativeLeaseFixture(releaseStatus: 204, renewals: [204, 409])
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, availableMemory: { 100_000 },
            workspace: { _ in NativeDiskCapacity(volume: "lease-fixture", availableBytes: 100_000) })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .leaseLost = outcome else { Issue.record("Expected authoritative lease loss before native run"); return }
        #expect(helper.requests == ["requirements"])
        #expect(budget.reservedBytes == 0)
    }

}

@Suite("Native progress evidence")
struct NativeProgressTests {
    @Test("native stage-only events do not invent transferred bytes or encoded time")
    func noInventedCounters() {
        for stage in ["FetchingSource", "Encoding", "Measuring", "Delivering"] {
            let event = NativeQualityEvent(type: "progress", jobId: 12, stage: stage, encodedSeconds: nil, detail: nil)
            let progress = NativeQualityJobRunner.progress(event)
            #expect(progress != nil)
            #expect(progress?.transferredBytes == nil)
        }
        let status = NativeQualityEvent(type: "status", jobId: 12, stage: nil, encodedSeconds: nil, detail: "Preparing")
        #expect(NativeQualityJobRunner.progress(status) == nil)
        let encoded = NativeQualityEvent(type: "progress", jobId: 12, stage: "Encoding", encodedSeconds: 2.5, detail: nil)
        #expect(NativeQualityJobRunner.progress(encoded) == .encoding(encodedSeconds: 2.5))
    }
}

@Suite("Mac native dispatch")
struct NativeDispatchTests {
    @Test("the ordinary client claim preserves the native payload and a missing installed helper never falls through to legacy encoding")
    func nativeClaimAndMissingHelper() async throws {
        let json = try JSONSerialization.jsonObject(with: NativeQualityAssignmentTests.wire()) as! [String: Any]
        let transport = RoutedTransport(assignment: json)
        let client = SidecarClient(transport: transport)
        let assignment = try #require(try await client.claim(serverAddress: "http://localhost:8787", credential: "fixture"))
        #expect(assignment.nativeQuality != nil && assignment.isImage)
        let release = StubTransport(status: 204)
        let runner = JobRunner(client: SidecarClient(transport: release), nativeTools: nil)
        let pairing = StoredPairing(serverAddress: "http://localhost:8787", credential: "fixture", workerId: 4)
        let result = await runner.execute(assignment, pairing: pairing) { _ in }
        guard case .released = result else { Issue.record("Missing shared helper must hand back the native job"); return }
        #expect(release.lastRequest?.url?.path.hasSuffix("/release") == true)
    }
}

private final class NativeFixtureState: @unchecked Sendable {
    private let gate = NSLock()
    private var memory: Int64? = 100_000_000
    private var didEject = false
    var ejected: Bool { gate.withLock { didEject } }
    func readMemory() -> Int64? { gate.withLock { memory } }
    func loseCapacity() { gate.withLock { memory = nil } }
    func eject() { gate.withLock { didEject = true } }
}

private final class NativeCleanupGate: @unchecked Sendable {
    private let gate = NSLock()
    private var continuation: CheckedContinuation<Void, Never>?
    private var didCancel = false
    var waiting: Bool { gate.withLock { continuation != nil } }
    var wasCancelled: Bool { gate.withLock { didCancel } }
    func cancelled() { gate.withLock { didCancel = true } }
    func wait() async { await withCheckedContinuation { continuation in gate.withLock { self.continuation = continuation } } }
    func finish() {
        let value = gate.withLock { let value = continuation; continuation = nil; return value }
        value?.resume()
    }
}

private final class NativeLeaseFixture: HTTPTransport, @unchecked Sendable {
    private let gate = NSLock()
    private var renewals: [Int]
    let releaseStatus: Int
    init(releaseStatus: Int, renewals: [Int] = [204]) { self.releaseStatus = releaseStatus; self.renewals = renewals }
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let status = gate.withLock {
            if request.url!.path.hasSuffix("/renew") { return renewals.count > 1 ? renewals.removeFirst() : renewals[0] }
            return releaseStatus
        }
        return (Data(), HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!)
    }
    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse { try await send(request).1 }
    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) { try await send(request) }
}
