import Foundation
import Testing
@testable import SidecarCore

private final class NativeExchangeFixture: NativeQualityExchanging, @unchecked Sendable {
    let budget: MemoryWorkBudget
    let recoveryFails: Bool
    let run: @Sendable (Data) async throws -> NativeQualityReply
    let boundary: @Sendable (String) async throws -> Void
    private let gate = NSLock()
    private var modes: [String] = []
    private var journals: [(NativeWorkJournal, URL)] = []
    private var busy = false
    private var scope = NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4")
    var requests: [String] { gate.withLock { modes } }
    func setBusy(_ value: Bool) { gate.withLock { busy = value } }
    func replaceScope() { gate.withLock { scope = NativeScratchScope(root: "darwin:2:2", control: "darwin:2:3", gate: "darwin:2:4") } }
    init(budget: MemoryWorkBudget, recoveryFails: Bool = false,
         boundary: @escaping @Sendable (String) async throws -> Void = { _ in },
         run: @escaping @Sendable (Data) async throws -> NativeQualityReply) {
        self.budget = budget; self.run = run; self.recoveryFails = recoveryFails; self.boundary = boundary
    }
    deinit { for (journal, root) in journals { journal.close(); try? FileManager.default.removeItem(at: root) } }
    func runtime(retireRam: @escaping @Sendable (NativeRamDiskIdentity) async throws -> Bool = { _ in true }) throws -> NativeWorkRuntime {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let journal = try NativeWorkJournal(directory: root)
        gate.withLock { journals.append((journal, root)) }
        let service = NativeResourceRetirementService(tools: NativeQualityTools(host: URL(fileURLWithPath: "/fixture"), ffmpeg: URL(fileURLWithPath: "/fixture")), helper: self)
        let registry = try NativeWorkRegistry(journal: journal, bootSession: UUID(), memory: budget, disk: DiskWorkBudget(),
            retirement: NativeWorkRetirement(service: service, retireRam: retireRam))
        return NativeWorkRuntime(open: { registry }, toolsAvailable: true, recover: { root in
            try await NativeScratchRecoveryService(tools: NativeQualityTools(host: URL(fileURLWithPath: "/fixture"), ffmpeg: URL(fileURLWithPath: "/fixture")), helper: self).recover(root: root)
        })
    }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        let json = try JSONSerialization.jsonObject(with: request) as! [String: Any]
        let mode = json["mode"] as! String
        gate.withLock { modes.append(mode) }
        try await boundary(mode)
        if mode == "requirements" {
            #expect(json["pairing"] == nil && json["grantedMemoryBytes"] == nil)
            return NativeQualityReply(type: "requirements", jobId: expectedJobId, delivered: false, capacityBlocked: false,
                capabilities: nil, nativeMemoryBytes: 40_000, requiredScratchBytes: 10_000, version: 2)
        }
        if mode == "recover" {
            if recoveryFails { throw NativeQualityHostError() }
            #expect(budget.reservedBytes == 0)
            return NativeQualityReply(type: "recovered", jobId: nil, delivered: false, capacityBlocked: false,
                capabilities: nil, recovery: NativeScratchRecovery(removed: 1), version: json["version"] as! Int)
        }
        if mode == "scope" {
            return NativeQualityReply(type: "scope", jobId: nil, delivered: false, capacityBlocked: false, capabilities: nil,
                version: 2, accountingScope: gate.withLock { scope })
        }
        if mode == "retire" {
            let blocked = gate.withLock { busy }
            return NativeQualityReply(type: "retired", jobId: expectedJobId, delivered: false, capacityBlocked: false, capabilities: nil,
                version: 2, retirement: NativeScratchRetirement(retired: !blocked, busy: blocked))
        }
        if mode == "forget" {
            return NativeQualityReply(type: "forgotten", jobId: expectedJobId, delivered: false, capacityBlocked: false, capabilities: nil,
                version: 2, consumption: NativeScratchConsumption(consumed: true, alreadyAbsent: false))
        }
        #expect(budget.reservedBytes >= 40_000)
        let reply = try await run(request)
        return NativeQualityReply(type: reply.type, jobId: reply.jobId, delivered: reply.delivered, capacityBlocked: reply.capacityBlocked,
            capabilities: reply.capabilities, version: 2, nativeWorkspaceCreated: true, nativeResourcesRetired: false)
    }
}

@Suite("Native quality job ownership")
struct NativeQualityJobRunnerTests {
    private let tools = NativeQualityTools(host: URL(fileURLWithPath: "/missing/host"), ffmpeg: URL(fileURLWithPath: "/missing/ffmpeg"))
    private let root = URL(fileURLWithPath: "/tmp/native-fixture")
    private let pairing = StoredPairing(serverAddress: "http://localhost:8787", credential: "private-fixture-credential-0123456789", workerId: 4)

    @Test("memory lost during the final physical-volume sample refuses before markRunning")
    func pressureDuringFinalVolumeCheck() async throws {
        let budget = MemoryWorkBudget(), live = NativeFixtureState(), samples = RunnerWorkspaceSamples()
        let helper = NativeExchangeFixture(budget: budget) { _ in Issue.record("Stale memory cannot start native work"); throw NativeQualityHostError() }
        let transport = StubTransport(status: 204)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: live.readMemory,
            workspace: { _, _ in
                if samples.next() == 2 { live.loseCapacity() }
                return NativeDiskCapacity(volume: "fixture", availableBytes: 100_000)
            })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .capacityBlocked = outcome else { Issue.record("Changed memory needs a neutral return"); return }
        #expect(transport.lastRequest?.url?.path.hasSuffix("/capacity-unavailable") == true)
        #expect(!helper.requests.contains("run") && !helper.requests.contains("retire"))
        #expect(budget.reservedBytes == 0)
    }

    @Test("a changed scope after disk admission cannot launch a helper against the replacement root")
    func redirectedRootAfterCapacity() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in Issue.record("Changed physical roots cannot start media work"); throw NativeQualityHostError() }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools,
            helper: helper, scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 },
            workspace: { _, _ in helper.replaceScope(); return NativeDiskCapacity(volume: "first-disk", availableBytes: 100_000) })
        _ = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        #expect(!helper.requests.contains("run") && budget.reservedBytes == 0)
    }

    @Test("delivery retains the entire grant while a surviving writer is busy and blocks another run")
    func deliveredWithBusyOwner() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            NativeQualityReply(type: "completed", jobId: 12, delivered: true, capacityBlocked: false, capabilities: nil)
        }
        helper.setBusy(true)
        let runtime = try helper.runtime()
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: StubTransport(status: 204)), tools: tools,
            helper: helper, scratchRoot: root, memoryBudget: budget, runtime: runtime, availableMemory: { 100_000 },
            workspace: { _, _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) })
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        #expect(await runner.execute(assignment, pairing: pairing) { _ in } == .delivered(jobId: 12, bytes: nil))
        #expect(budget.reservedBytes == 40_000)
        let modes = helper.requests
        guard case .capacityBlocked = await runner.execute(assignment, pairing: pairing, progress: { _ in }) else {
            Issue.record("Retained writer debt must block another native run"); return
        }
        #expect(helper.requests == modes + ["retire"] && budget.reservedBytes == 40_000)
        helper.setBusy(false)
        #expect(await runtime.refresh(roots: []) == .ready)
        #expect(budget.reservedBytes == 0)
    }

    @Test("cancellation during unresolved attach retains unknown RAM debt and never falls back", arguments: [false, true])
    func cancellationDuringAttach(fail: Bool) async throws {
        let budget = MemoryWorkBudget(), barrier = RunnerAttachBarrier()
        let helper = NativeExchangeFixture(budget: budget) { _ in Issue.record("Cancelled attach cannot run media work"); throw NativeQualityHostError() }
        let runtime = try helper.runtime()
        let factory = fixtureRamFactory(attached: barrier.enter, fail: fail)
        let transport = NativeInterruptionFixture(status: 204)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 100_000_000),
            memoryBudget: budget, runtime: runtime, availableMemory: { 100_000_000 },
            workspace: { _, _ in Issue.record("Unknown RAM allocation cannot fall back to disk"); return nil },
            ramFactory: factory, mountParent: root)
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        let task = Task { await runner.execute(assignment, pairing: pairing) { _ in } }
        try await waitFor("the RAM attach boundary") { barrier.entered }
        task.cancel()
        let outcome = await task.value
        guard case .released = outcome else { Issue.record("Cancellation needs an independent acknowledgement"); barrier.release(); return }
        #expect(transport.lastPath?.hasSuffix("/interrupted") == true)
        #expect(budget.reservedBytes == 40_000 + RamDisk.allocationBytes(for: 10_000)!)
        #expect(!helper.requests.contains("run"))
        barrier.release()
        #expect(await runtime.refresh(roots: []) == .blocked)
    }

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
            helper: helper, scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 }, workspace: { chosen, scope in
                #expect(chosen == root)
                #expect(helper.requests == ["recover", "recover", "requirements", "scope", "recover"] || helper.requests == ["recover", "recover", "requirements", "scope", "recover", "scope"])
                #expect(scope.root == "darwin:1:2")
                return NativeDiskCapacity(volume: "fixture", availableBytes: 100_000)
            })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        #expect(outcome == .delivered(jobId: 12, bytes: nil))
        #expect(helper.requests == ["recover", "recover", "requirements", "scope", "recover", "scope", "run", "retire", "forget"])
        #expect(budget.reservedBytes == 0)
    }

    @Test("unknown or insufficient current memory and unknown disk volumes release before running the media helper")
    func capacityRefusal() async throws {
        for (memory, disk): (Int64?, Int64?) in [(nil, 100_000), (39_999, 100_000), (100_000, nil), (100_000, 9_999)] {
            let budget = MemoryWorkBudget()
            let helper = NativeExchangeFixture(budget: budget) { _ in Issue.record("No native run may start"); throw NativeQualityHostError() }
            let transport = StubTransport(status: 204)
            let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
                scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { memory }, workspace: { _, _ in disk.map { NativeDiskCapacity(volume: "fixture", availableBytes: $0) } })
            let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
            guard case .capacityBlocked = outcome else { Issue.record("Expected acknowledged capacity refusal"); continue }
            #expect(transport.lastRequest?.url?.path.hasSuffix("/capacity-unavailable") == true)
            #expect(helper.requests == ["recover", "recover", "requirements", "scope", "recover"])
            #expect(budget.reservedBytes == 0)
        }
    }

    @Test("unavailable recovery is a neutral capacity return before disk admission or native media work")
    func recoveryUnavailable() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget, recoveryFails: true) { _ in
            Issue.record("Recovery failure cannot start media work"); throw NativeQualityHostError()
        }
        let transport = StubTransport(status: 204)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 }, workspace: { _, _ in
                Issue.record("Recovery must precede capacity sampling"); return nil
            })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .capacityBlocked = outcome else { Issue.record("Expected neutral, acknowledged capacity return"); return }
        #expect(transport.lastRequest?.url?.path.hasSuffix("/capacity-unavailable") == true)
        #expect(helper.requests == ["recover"] && budget.reservedBytes == 0)
    }

    @Test("a failed capacity handback is unconfirmed, never a confirmed cooldown")
    func capacityHandbackFailure() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in throw NativeQualityHostError() }
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: NativeLeaseFixture(releaseStatus: 503)), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { nil }, workspace: { _, _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) })
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
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 }, workspace: { _, _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) })
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
            memoryBudget: budget, runtime: try helper.runtime(retireRam: { _ in false }), availableMemory: { 100_000_000 }, workspace: { _, _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) },
            ramFactory: fixtureRamFactory(), mountParent: root, availableScratch: { _ in 100_000 })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        #expect(outcome == .delivered(jobId: 12, bytes: nil))
        #expect(budget.reservedBytes == allocation + 40_000)
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
            memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 }, workspace: { _, _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) },
            ramFactory: fixtureRamFactory(attached: { Issue.record("RAM must not allocate beyond live capacity") }), mountParent: root)
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
            memoryBudget: budget, runtime: try helper.runtime(retireRam: { _ in live.eject(); return true }), availableMemory: live.readMemory, workspace: { _, _ in NativeDiskCapacity(volume: "fixture", availableBytes: 100_000) },
            ramFactory: fixtureRamFactory(attached: live.loseCapacity), mountParent: root, availableScratch: { _ in 100_000 })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .capacityBlocked = outcome else { Issue.record("Expected capacity hand-back before run"); return }
        #expect(helper.requests == ["recover", "recover", "requirements", "scope"])
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
        let transport = NativeInterruptionFixture(status: 204)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 },
            workspace: { _, _ in NativeDiskCapacity(volume: "cancel-fixture", availableBytes: 100_000) })
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        let task = Task { await runner.execute(assignment, pairing: pairing) { _ in } }
        try await waitFor("the native helper to run") { helper.requests.contains("run") && cleanup.waiting }
        task.cancel()
        try await waitFor("owned child cancellation to begin") { cleanup.wasCancelled }
        #expect(budget.reservedBytes == 40_000)
        cleanup.finish()
        let result = await task.value
        guard case .released = result else { Issue.record("Cancelled work must hand back"); return }
        #expect(transport.lastPath?.hasSuffix("/interrupted") == true)
        #expect(budget.reservedBytes == 0)
    }

    @Test("pre-run cancellation uses an uncancelled acknowledged interruption without starting the helper", arguments: [204, 503, 409])
    func earlyCancellation(status: Int) async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            Issue.record("Cancelled work cannot start media tools"); throw NativeQualityHostError()
        }
        let transport = NativeInterruptionFixture(status: status)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime())
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        let start = NativeCleanupGate()
        let task = Task {
            await start.wait()
            return await runner.execute(assignment, pairing: pairing) { _ in }
        }
        try await waitFor("the cancellation fixture to be ready") { start.waiting }
        task.cancel(); start.finish()
        let result = await task.value
        #expect(helper.requests.isEmpty && budget.reservedBytes == 0)
        #expect(transport.lastPath?.hasSuffix("/interrupted") == true)
        switch (status, result) {
        case (204, .released), (503, .unconfirmed), (409, .leaseLost): break
        default: Issue.record("Only an acknowledged interruption or authoritative lease loss may end this attempt: \(result)")
        }
    }

    @Test("a failed native helper is an interruption rather than a permanent file refusal")
    func helperFailure() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in throw NativeQualityHostError() }
        let transport = NativeInterruptionFixture(status: 204)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 },
            workspace: { _, _ in NativeDiskCapacity(volume: "crash-fixture", availableBytes: 100_000) })
        let result = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .released = result else { Issue.record("Helper failure needs an acknowledged interruption"); return }
        #expect(transport.lastPath?.hasSuffix("/interrupted") == true)
    }

    @Test("cancellation arriving during a capacity handback still gets an independent interruption acknowledgement")
    func cancellationDuringHandback() async throws {
        let budget = MemoryWorkBudget()
        let helper = NativeExchangeFixture(budget: budget) { _ in
            Issue.record("Unknown capacity cannot start native media work"); throw NativeQualityHostError()
        }
        let transport = NativeInterruptionFixture(status: 204, cancelCapacity: true)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { nil },
            workspace: { _, _ in NativeDiskCapacity(volume: "cancel-capacity", availableBytes: 100_000) })
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        let result = await Task { await runner.execute(assignment, pairing: pairing) { _ in } }.value
        guard case .released = result else { Issue.record("Cancelled capacity transport must acknowledge interruption"); return }
        #expect(transport.lastPath?.hasSuffix("/interrupted") == true)
        #expect(budget.reservedBytes == 0 && !helper.requests.contains("run"))
    }

    @Test("requirements, recovery and RAM preparation cancellation cannot create file refusals", arguments: ["requirements", "recover"])
    func preparationCancellation(boundary: String) async throws {
        let budget = MemoryWorkBudget()
        let preparation = NativeCleanupGate()
        let helper = NativeExchangeFixture(budget: budget, boundary: { mode in
            if mode == boundary {
                await preparation.wait()
                try Task.checkCancellation()
            }
        }) { _ in Issue.record("Cancelled preparation cannot run media tools"); throw NativeQualityHostError() }
        let transport = NativeInterruptionFixture(status: 204)
        let runner = NativeQualityJobRunner(client: SidecarClient(transport: transport), tools: tools, helper: helper,
            scratchRoot: root, settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 100_000_000),
            memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000_000 },
            workspace: { _, _ in nil },
            ramFactory: fixtureRamFactory(attached: { Issue.record("Cancelled preparation cannot allocate RAM") }), mountParent: root, availableScratch: { _ in 100_000 })
        let assignment = try #require(Assignment.decode(NativeQualityAssignmentTests.wire()))
        let task = Task {
            let result = await runner.execute(assignment, pairing: pairing) { _ in }
            #expect(Task.isCancelled)
            return result
        }
        if boundary == "requirements" || boundary == "recover" {
            try await waitFor("the outer job to reach \(boundary)") { preparation.waiting }
            task.cancel(); preparation.finish()
        }
        let result = await task.value
        guard case .released = result else { Issue.record("Preparation cancellation needs an acknowledged interruption"); return }
        #expect(transport.lastPath?.hasSuffix("/interrupted") == true)
        #expect(!helper.requests.contains("run") && budget.reservedBytes == 0)
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
            scratchRoot: root, memoryBudget: budget, runtime: try helper.runtime(), availableMemory: { 100_000 },
            workspace: { _, _ in NativeDiskCapacity(volume: "lease-fixture", availableBytes: 100_000) })
        let outcome = await runner.execute(try #require(Assignment.decode(NativeQualityAssignmentTests.wire())), pairing: pairing) { _ in }
        guard case .leaseLost = outcome else { Issue.record("Expected authoritative lease loss before native run"); return }
        #expect(helper.requests == ["recover", "recover", "requirements", "scope", "recover"])
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

private func fixtureRamFactory(attached: @escaping @Sendable () -> Void = {}, fail: Bool = false) -> NativeRamDiskFactory {
    NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
        attached()
        if fail { throw NativeQualityHostError() }
        return RunnerRamAllocation(identity: NativeRamAllocationIdentity(bootSession: boot, owner: owner.owner,
            registryEntryId: 123, backingEntryId: 120, device: "/dev/disk9", allocatedBytes: bytes))
    })
}

private final class RunnerRamAllocation: NativeRamFactoryAllocating, @unchecked Sendable {
    let identity: NativeRamAllocationIdentity
    init(identity: NativeRamAllocationIdentity) { self.identity = identity }
    func format(control: NativeRamFactoryControl) -> Bool { control.isPermitted }
    func mount(at parent: URL, control: NativeRamFactoryControl) -> NativeRamDiskCreation? {
        let root = parent.appendingPathComponent(identity.owner.uuidString)
        let mounted = NativeRamDiskIdentity(bootSession: identity.bootSession, registryEntryId: identity.registryEntryId,
            backingEntryId: identity.backingEntryId, volumeId: UUID(), device: identity.device, mountPoint: root.path, rootIdentity: "darwin:1:2")
        return NativeRamDiskCreation(disk: RamDisk(mountPoint: root, device: identity.device), identity: mounted)
    }
    func endCreation() {}
}

private final class RunnerAttachBarrier: @unchecked Sendable {
    private let condition = NSCondition()
    private var started = false
    private var released = false
    var entered: Bool { condition.withLock { started } }
    func enter() {
        condition.lock(); defer { condition.unlock() }
        started = true
        while !released { condition.wait() }
    }
    func release() { condition.withLock { released = true; condition.broadcast() } }
}

private final class RunnerWorkspaceSamples: @unchecked Sendable {
    private let gate = NSLock()
    private var count = 0
    func next() -> Int { gate.withLock { count += 1; return count } }
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

private final class NativeInterruptionFixture: HTTPTransport, @unchecked Sendable {
    private let gate = NSLock()
    private var path: String?
    var lastPath: String? { gate.withLock { path } }
    let status: Int
    let cancelCapacity: Bool
    init(status: Int, cancelCapacity: Bool = false) { self.status = status; self.cancelCapacity = cancelCapacity }
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        // URLSession cancels requests inherited from a cancelled task; a permissive stub hid it.
        try Task.checkCancellation()
        gate.withLock { path = request.url!.path }
        if cancelCapacity && request.url!.path.hasSuffix("/capacity-unavailable") {
            withUnsafeCurrentTask { $0?.cancel() }
            throw CancellationError()
        }
        return (Data(), HTTPURLResponse(url: request.url!, statusCode: request.url!.path.hasSuffix("/renew") ? 204 : status,
            httpVersion: nil, headerFields: nil)!)
    }
    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse { try await send(request).1 }
    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) { try await send(request) }
}
