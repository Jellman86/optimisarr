import Foundation
import Testing
@testable import SidecarCore

@Suite("Native RAM retirement bounds")
struct NativeRamDiskLifecycleTests {
    private let identity = NativeRamDiskIdentity(bootSession: UUID(), registryEntryId: 123, backingEntryId: 120,
        volumeId: UUID(), device: "/dev/disk9", mountPoint: "/Volumes/OptimisarrWork-fixture", rootIdentity: "darwin:1:2")

    @Test("a timed-out OS operation retains its single-flight slot until it actually finishes")
    func blockedOSOperation() async throws {
        let entered = LockedRamCalls(), barrier = DispatchSemaphore(value: 0)
        let service = NativeRamDiskLifecycle(timeout: .milliseconds(30), retire: { _, permitted in
            entered.increment()
            barrier.wait()
            return permitted()
        })
        let started = Date()
        #expect(await !service.retire(identity))
        #expect(Date().timeIntervalSince(started) < 2)
        #expect(entered.count == 1)
        #expect(await !service.retire(identity))
        #expect(entered.count == 1)
        barrier.signal()
        try await waitFor("the late OS operation to leave its slot") { !service.hasInFlightOperation(identity) }
        #expect(entered.count == 1)
    }

    @Test("cancellation returns without admitting an overlapping OS operation")
    func cancelledOSOperation() async throws {
        let entered = LockedRamCalls(), barrier = DispatchSemaphore(value: 0)
        let service = NativeRamDiskLifecycle(timeout: .seconds(5), retire: { _, permitted in
            entered.increment(); barrier.wait(); return permitted()
        })
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
        let calls = LockedRamCalls(), barrier = DispatchSemaphore(value: 0)
        let first = NativeRamDiskLifecycle(timeout: .milliseconds(30), retire: { _, permitted in
            calls.increment(); barrier.wait(); return permitted()
        })
        let second = NativeRamDiskLifecycle(retire: { _, _ in calls.increment(); return true })
        #expect(await !first.retire(identity))
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
}

private final class LockedRamCalls: @unchecked Sendable {
    private let gate = NSLock()
    private var value = 0
    var count: Int { gate.withLock { value } }
    func increment() { gate.withLock { value += 1 } }
}
