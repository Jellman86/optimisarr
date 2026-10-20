import Foundation
import Testing
@testable import SidecarCore

@Suite("Native RAM creation ordering")
struct NativeRamDiskFactoryTests {
    @Test("cancellation at the command launch boundary starts no formatter")
    func cancelledBeforeLaunch() async throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: path) }
        let events = RamFactoryEvents(), barrier = DispatchSemaphore(value: 0), control = NativeRamFactoryControl()
        defer { barrier.signal() }
        let task = Task.detached {
            NativeRamPinnedCommand.run("/usr/bin/touch", [path.path], control: control, beforeLaunch: {
                events.add("before-launch"); barrier.wait()
            })
        }
        try await waitFor("the command launch boundary") { events.calls.contains("before-launch") }
        control.stop(); barrier.signal()
        #expect(await task.value == nil)
        #expect(!FileManager.default.fileExists(atPath: path.path))
    }
    @Test("a cancelled allocation callback cannot authorize formatting after its bounded caller returns")
    func cancelledAllocationCallback() async throws {
        let events = RamFactoryEvents(), barrier = AsyncStream<Void>.makeStream()
        defer { barrier.continuation.finish() }
        let owner = NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), boot = UUID()
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            events.add("attach")
            return FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events)
        })
        let run = Task {
            await factory.create(owner: owner, bootSession: boot, allocatedBytes: 512,
                mountParent: URL(fileURLWithPath: "/fixture"), allocated: { _, _ in
                    events.add("allocation-callback")
                    for await _ in barrier.stream { break }
                    events.add("allocation-callback-ended")
                }, mounted: { _, _ in events.add("mounted-callback") })
        }
        try await waitFor("the allocation persistence callback") { events.calls.contains("allocation-callback") }
        run.cancel()
        #expect(await run.value == nil)
        #expect(!events.calls.contains("format") && !events.calls.contains("end-creation"))
        barrier.continuation.yield(())
        try await waitFor("the cancelled creation allocation to close") { events.calls.contains("deinit") }
        #expect(!events.calls.contains("format") && !events.calls.contains("mount"))
    }

    @Test("a timed-out formatter retains creation authority until the actual operation returns")
    func timedOutFormatter() async throws {
        let events = RamFactoryEvents(), barrier = DispatchSemaphore(value: 0)
        let owner = NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), boot = UUID()
        let factory = NativeRamDiskFactory(timeout: .seconds(1), backend: { owner, boot, bytes, _ in
            FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events, formatBarrier: barrier)
        })
        let result = await factory.create(owner: owner, bootSession: boot, allocatedBytes: 512,
            mountParent: URL(fileURLWithPath: "/fixture"), allocated: { _, _ in }, mounted: { _, _ in })
        #expect(result == nil)
        #expect(events.calls == ["format"])
        barrier.signal()
        try await waitFor("the late formatter to end") { events.calls.contains("deinit") }
        #expect(!events.calls.contains("mount") && !events.calls.contains("end-creation"))
    }

    @Test("failure to persist a mounted identity keeps creation unresolved without starting another effect")
    func failedMountedCallback() async throws {
        let events = RamFactoryEvents(), owner = NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID())
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events)
        })
        #expect(await factory.create(owner: owner, bootSession: UUID(), allocatedBytes: 512,
            mountParent: URL(fileURLWithPath: "/fixture"), allocated: { _, _ in events.add("persist-allocation") },
            mounted: { _, _ in events.add("persist-mounted-failed"); throw NativeWorkRegistryError.unavailable }) == nil)
        try await waitFor("the failed creation to close") { events.calls.contains("deinit") }
        #expect(events.calls == ["persist-allocation", "format", "mount", "persist-mounted-failed", "deinit"])
    }

    @Test("cancellation during mounted persistence retains its live allocation until the callback ends")
    func cancelledMountedCallback() async throws {
        let events = RamFactoryEvents(), barrier = AsyncStream<Void>.makeStream()
        defer { barrier.continuation.finish() }
        let owner = NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), boot = UUID()
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events)
        })
        let run = Task {
            await factory.create(owner: owner, bootSession: boot, allocatedBytes: 512,
                mountParent: URL(fileURLWithPath: "/fixture"), allocated: { _, _ in }, mounted: { _, permitted in
                    events.add("mounted-callback")
                    for await _ in barrier.stream { break }
                    #expect(!permitted())
                })
        }
        try await waitFor("the mounted persistence callback") { events.calls.contains("mounted-callback") }
        run.cancel()
        #expect(await run.value == nil)
        #expect(!events.calls.contains("end-creation") && !events.calls.contains("deinit"))
        barrier.continuation.yield(())
        try await waitFor("the mounted persistence callback to end") { events.calls.contains("deinit") }
        #expect(!events.calls.contains("end-creation"))
    }

    @Test("an owned command ignoring TERM retains its allocation after the caller cancels", arguments: ["format", "mount"])
    func uncooperativeCreationCommand(_ stage: String) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        let ready = root.appendingPathComponent("ready"), fifo = root.appendingPathComponent("gate")
        try #require(mkfifo(fifo.path, 0o600) == 0)
        defer { try? FileManager.default.removeItem(at: root) }
        let script = root.appendingPathComponent("owned-tool.sh")
        try """
        trap '' TERM
        exec 3<> "$1"
        printf '%s' "$$" > "$2"
        IFS= read -r -t 10 release <&3
        """.write(to: script, atomically: true, encoding: .utf8)
        let events = RamFactoryEvents(), owner = NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), boot = UUID()
        let factory = NativeRamDiskFactory(backend: { owner, boot, bytes, _ in
            FixtureRamAllocation(owner: owner.owner, boot: boot, bytes: bytes, events: events, command: { current, control in
                guard current == stage else { return true }
                return NativeRamPinnedCommand.run("/bin/sh", [script.path, fifo.path, ready.path], control: control, timeout: 2) != nil
            })
        })
        let run = Task { await factory.create(owner: owner, bootSession: boot, allocatedBytes: 512,
            mountParent: root, allocated: { _, _ in }, mounted: { _, _ in }) }
        defer { run.cancel() }
        try await waitFor("the owned creation command to become ready") { FileManager.default.fileExists(atPath: ready.path) }
        let pid = try #require(Int32(String(contentsOf: ready, encoding: .utf8)))
        let identity = NativeRamAllocationIdentity(bootSession: boot, owner: owner.owner, registryEntryId: 123,
            backingEntryId: 120, device: "/dev/disk9", allocatedBytes: 512)
        let lifecycle = NativeRamDiskLifecycle(retire: { _, _ in true }, retireAllocation: { _, _ in true })
        run.cancel()
        #expect(await run.value == nil)
        #expect(kill(pid, 0) == 0)
        #expect(await !lifecycle.retire(identity))
        try await waitFor("the owned command to be reaped and release its original allocation") { events.calls.contains("deinit") }
        #expect(kill(pid, 0) != 0)
        #expect(await lifecycle.retire(identity))
        #expect(!events.calls.contains("end-creation"))
    }
}

final class RamFactoryEvents: @unchecked Sendable {
    private let gate = NSLock()
    private var history: [String] = []
    var calls: [String] { gate.withLock { history } }
    func add(_ call: String) { gate.withLock { history.append(call) } }
}

final class FixtureRamAllocation: NativeRamFactoryAllocating, @unchecked Sendable {
    let identity: NativeRamAllocationIdentity
    private let events: RamFactoryEvents
    private let formatBarrier: DispatchSemaphore?
    private let observe: @Sendable (String) -> Void
    private let lease: NativeRamCreationLease?
    private let command: @Sendable (String, NativeRamFactoryControl) -> Bool
    init(owner: UUID, boot: UUID, bytes: Int64, events: RamFactoryEvents, formatBarrier: DispatchSemaphore? = nil,
         observe: @escaping @Sendable (String) -> Void = { _ in },
         command: @escaping @Sendable (String, NativeRamFactoryControl) -> Bool = { _, _ in true }) {
        identity = NativeRamAllocationIdentity(bootSession: boot, owner: owner, registryEntryId: 123,
            backingEntryId: 120, device: "/dev/disk9", allocatedBytes: bytes)
        self.events = events; self.formatBarrier = formatBarrier; self.observe = observe
        lease = NativeRamDiskLifecycle.creationLease(identity)
        self.command = command
    }
    deinit { events.add("deinit") }
    func format(control: NativeRamFactoryControl) -> Bool {
        events.add("format"); observe("format"); formatBarrier?.wait(); return control.isPermitted && command("format", control)
    }
    func mount(at parent: URL, control: NativeRamFactoryControl) -> NativeRamDiskCreation? {
        events.add("mount")
        observe("mount")
        guard command("mount", control) else { return nil }
        let disk = RamDisk(mountPoint: parent.appendingPathComponent(identity.owner.uuidString), device: identity.device)
        return NativeRamDiskCreation(disk: disk, identity: NativeRamDiskIdentity(bootSession: identity.bootSession,
            registryEntryId: identity.registryEntryId, backingEntryId: identity.backingEntryId, volumeId: UUID(),
            device: identity.device, mountPoint: disk.mountPoint.path, rootIdentity: "darwin:1:2"))
    }
    func endCreation() { events.add("end-creation"); lease?.close() }
}
