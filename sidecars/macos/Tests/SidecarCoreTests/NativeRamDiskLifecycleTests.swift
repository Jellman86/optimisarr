import Foundation
import Testing
@testable import SidecarCore

@Suite("Native RAM retirement bounds")
struct NativeRamDiskLifecycleTests {
    private let identity = NativeRamDiskIdentity(bootSession: UUID(), registryEntryId: 123, backingEntryId: 120,
        volumeId: UUID(), device: "/dev/disk9", mountPoint: "/Volumes/OptimisarrWork-fixture", rootIdentity: "darwin:1:2")

    @Test("a timed-out OS operation retains its single-flight slot until it actually finishes")
    func blockedOSOperation() async throws {
        let entered = LockedRamCalls(), barrier = DispatchSemaphore(value: 0), deadline = ControlledRamDeadline()
        let service = NativeRamDiskLifecycle(timeout: .milliseconds(30), retire: { _, permitted in
            entered.increment()
            barrier.wait()
            return permitted()
        }, scheduleTimeout: deadline.schedule)
        let task = Task { await service.retire(identity) }
        defer { barrier.signal() }
        try await waitFor("the OS operation and its deadline to start") { entered.count == 1 && deadline.isScheduled }
        let started = Date()
        deadline.expire()
        #expect(await !task.value)
        #expect(Date().timeIntervalSince(started) < 2)
        #expect(entered.count == 1)
        #expect(await !service.retire(identity))
        #expect(entered.count == 1)
        barrier.signal()
        try await waitFor("the late OS operation to leave its slot") { !service.hasInFlightOperation(identity) }
        #expect(entered.count == 1)
    }

    @Test("the real deadline bounds queued or running OS work without treating either as retired")
    func realDeadlineBoundsQueuedOrRunningWork() async throws {
        let calls = LockedRamCalls(), barrier = DispatchSemaphore(value: 0), completion = LockedRamResult()
        let service = NativeRamDiskLifecycle(timeout: .milliseconds(30), retire: { _, permitted in
            calls.increment(); barrier.wait(); return permitted()
        })
        let task = Task { completion.store(await service.retire(identity)) }
        defer { task.cancel(); barrier.signal() }
        try await waitFor("the real deadline to return while OS work remains unreleased") { completion.value != nil }
        #expect(completion.value == false)
        barrier.signal()
        await task.value
        try await waitFor("queued or running OS work to finish") { !service.hasInFlightOperation(identity) }
        #expect(calls.count <= 1)
    }

    @Test("cancellation returns without admitting an overlapping OS operation")
    func cancelledOSOperation() async throws {
        let entered = LockedRamCalls(), barrier = DispatchSemaphore(value: 0)
        let service = NativeRamDiskLifecycle(timeout: .seconds(5), retire: { _, permitted in
            entered.increment(); barrier.wait(); return permitted()
        })
        defer { barrier.signal() }
        let task = Task { await service.retire(identity) }
        try await waitFor("the OS operation to start") { entered.count == 1 }
        task.cancel()
        #expect(await !task.value)
        #expect(await !service.retire(identity))
        #expect(entered.count == 1)
        barrier.signal()
        try await waitFor("cancelled OS work to finish") { !service.hasInFlightOperation(identity) }
    }

    @Test("separate lifecycle instances share the timed-out physical allocation slot")
    func crossInstanceSingleFlight() async throws {
        let calls = LockedRamCalls(), barrier = DispatchSemaphore(value: 0), deadline = ControlledRamDeadline()
        let first = NativeRamDiskLifecycle(timeout: .milliseconds(30), retire: { _, permitted in
            calls.increment(); barrier.wait(); return permitted()
        }, scheduleTimeout: deadline.schedule)
        let second = NativeRamDiskLifecycle(retire: { _, _ in calls.increment(); return true })
        let task = Task { await first.retire(identity) }
        defer { barrier.signal() }
        try await waitFor("the first retirement and its deadline to start") { calls.count == 1 && deadline.isScheduled }
        deadline.expire()
        #expect(await !task.value)
        #expect(await !second.retire(identity))
        #expect(calls.count == 1)
        barrier.signal()
        try await waitFor("the shared physical slot to be released") { !first.hasInFlightOperation(identity) }
        #expect(await second.retire(identity))
        #expect(calls.count == 2)
    }

    @Test("invalid registry iteration cannot establish absence and releases acquired matches")
    func invalidRegistryIterator() throws {
        for objects: [UInt32] in [[0, 0], [42, 0], [42, 47]] {
            var offset = 0
            var released: [UInt32] = []
            #expect(throws: NativeRegistryIterator.Unconfirmed.self) {
                try NativeRegistryIterator.readSingle(next: { defer { offset += 1 }; return objects[offset] },
                    isValid: { false }, release: { released.append($0) })
            }
            #expect(released.sorted() == objects.filter { $0 != 0 }.sorted())
        }
        #expect(try NativeRegistryIterator.readSingle(next: { 0 }, isValid: { true }, release: { _ in }) == nil)
    }

    @Test("fallback scanning distinguishes a present original, complete absence and an unknown partial scan")
    func fallbackScan() throws {
        for target: UInt32 in [42, 999] {
            var offset = 0, released: [UInt32] = []
            let objects: [UInt32] = [40, 42, 43, 0]
            let found = try NativeRegistryIterator.readSingle(next: { defer { offset += 1 }; return objects[offset] },
                isValid: { true }, release: { released.append($0) }, accepts: { $0 == target })
            #expect(found == (target == 42 ? 42 : nil))
            #expect(released.sorted() == objects.filter { $0 != 0 && $0 != found }.sorted())
        }
        for failsRead in [false, true] {
            var released: [UInt32] = []
            #expect(throws: NativeRegistryIterator.Unconfirmed.self) {
                try NativeRegistryIterator.readSingle(next: { 42 }, isValid: { true }, release: { released.append($0) },
                    accepts: { _ in if failsRead { throw NativeRegistryIterator.Unconfirmed() }; return false }, maximum: 2)
            }
            #expect(released == (failsRead ? [42] : [42, 42]))
        }
    }

    @Test("pre-cancelled and invalid identities cannot invoke OS retirement")
    func invalidAndCancelled() async {
        let calls = LockedRamCalls()
        let service = NativeRamDiskLifecycle(retire: { _, _ in calls.increment(); return true })
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return await service.retire(identity)
        }
        #expect(await !task.value)
        let invalid = NativeRamDiskIdentity(bootSession: identity.bootSession, registryEntryId: 123, backingEntryId: 0,
            volumeId: identity.volumeId, device: identity.device, mountPoint: identity.mountPoint, rootIdentity: identity.rootIdentity)
        #expect(await !service.retire(invalid))
        #expect(calls.count == 0)
    }

    @Test("partial and mounted receipts share one physical retirement slot across lifecycle instances")
    func partialAndMountedSingleFlight() async throws {
        let calls = LockedRamCalls(), barrier = DispatchSemaphore(value: 0), deadline = ControlledRamDeadline()
        let allocation = NativeRamAllocationIdentity(bootSession: identity.bootSession, owner: UUID(),
            registryEntryId: identity.registryEntryId, backingEntryId: identity.backingEntryId,
            device: identity.device, allocatedBytes: 512)
        let first = NativeRamDiskLifecycle(timeout: .milliseconds(30), retire: { _, _ in false }, retireAllocation: { _, permitted in
            calls.increment(); barrier.wait(); return permitted()
        }, scheduleTimeout: deadline.schedule)
        let second = NativeRamDiskLifecycle(retire: { _, _ in calls.increment(); return true })
        let task = Task { await first.retire(allocation) }
        defer { barrier.signal() }
        try await waitFor("the partial retirement and its deadline to start") { calls.count == 1 && deadline.isScheduled }
        deadline.expire()
        #expect(await !task.value)
        #expect(await !second.retire(identity))
        #expect(calls.count == 1)
        barrier.signal()
        try await waitFor("the partial allocation's OS operation to finish") { !first.hasInFlightOperation(identity) }
        #expect(await second.retire(identity))
        #expect(calls.count == 2)
    }
}

private final class ControlledRamDeadline: @unchecked Sendable {
    private let gate = NSLock()
    private var pending: (@Sendable () -> Void)?
    var isScheduled: Bool { gate.withLock { pending != nil } }
    func schedule(_ delay: TimeInterval, _ expire: @escaping @Sendable () -> Void) {
        gate.withLock { pending = expire }
    }
    func expire() {
        let action = gate.withLock { let action = pending; pending = nil; return action }
        action?()
    }
}

private final class LockedRamResult: @unchecked Sendable {
    private let gate = NSLock()
    private var result: Bool?
    var value: Bool? { gate.withLock { result } }
    func store(_ result: Bool) { gate.withLock { self.result = result } }
}

private final class LockedRamCalls: @unchecked Sendable {
    private let gate = NSLock()
    private var value = 0
    var count: Int { gate.withLock { value } }
    func increment() { gate.withLock { value += 1 } }
}
