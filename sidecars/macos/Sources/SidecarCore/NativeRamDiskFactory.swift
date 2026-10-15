import Darwin
import Foundation

public struct NativeRamDiskCreation: Sendable {
    public let disk: RamDisk
    public let identity: NativeRamDiskIdentity
    private let directory: NativeRamMountDirectory?

    init(disk: RamDisk, identity: NativeRamDiskIdentity, directory: NativeRamMountDirectory? = nil) {
        self.disk = disk; self.identity = identity; self.directory = directory
    }
}

protocol NativeRamFactoryAllocating: AnyObject, Sendable {
    var identity: NativeRamAllocationIdentity { get }
    func format(control: NativeRamFactoryControl) -> Bool
    func mount(at parent: URL, control: NativeRamFactoryControl) -> NativeRamDiskCreation?
    func endCreation()
}

/// The caller may stop waiting while an owned command or a durability callback is still
/// unresolved. Its actual pipeline retains the original pin and exclusion lease until done.
public final class NativeRamDiskFactory: @unchecked Sendable {
    typealias Backend = @Sendable (NativeAccountedOwner, UUID, Int64, NativeRamFactoryControl) throws -> any NativeRamFactoryAllocating
    private let backend: Backend
    private let timeout: TimeInterval
    private let queue = DispatchQueue(label: "uk.optimisarr.native-ram-creation", qos: .utility)

    public convenience init(timeout: Duration = .seconds(30)) {
        self.init(timeout: timeout, backend: MacNativeRamFactoryAllocation.attach)
    }

    init(timeout: Duration = .seconds(30), backend: @escaping Backend) {
        let parts = timeout.components
        self.timeout = max(0.001, min(60, Double(parts.seconds) + Double(parts.attoseconds) / 1e18))
        self.backend = backend
    }

    // Only registry-backed creation may use this path. Serialized receipts cannot construct
    // the backend's live allocation, which is the sole authority to format and mount.
    func create(owner: NativeAccountedOwner, bootSession: UUID, allocatedBytes: Int64, mountParent: URL,
                allocated: @escaping @Sendable (NativeRamAllocationIdentity, @Sendable () -> Bool) async throws -> Void,
                mounted: @escaping @Sendable (NativeRamDiskIdentity, @Sendable () -> Bool) async throws -> Void) async -> NativeRamDiskCreation? {
        guard !Task.isCancelled, owner.isValid, bootSession != NativeWorkLedger.zero,
              allocatedBytes > 0, allocatedBytes % 512 == 0, mountParent.isFileURL,
              NativeWorkLedger.validPath(mountParent.path) else { return nil }
        let attempt = NativeRamAttempt<NativeRamDiskCreation?>(fallback: nil)
        let control = NativeRamFactoryControl(permitted: { attempt.isPermitted })
        let cancel: @Sendable () -> Void = {
            // Spawn can itself be unresolved. Revoke the independent attempt immediately;
            // stopping its owned process may wait for the serialized launch to return.
            attempt.cancel()
            DispatchQueue.global(qos: .utility).async { control.stop() }
        }
        return await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<NativeRamDiskCreation?, Never>) in
                attempt.install(continuation)
                let timer = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos: .utility))
                timer.schedule(deadline: .now() + timeout)
                timer.setEventHandler(handler: cancel)
                timer.activate()
                Task.detached {
                    let result = await self.pipeline(owner: owner, bootSession: bootSession, bytes: allocatedBytes,
                        parent: mountParent, control: control, allocated: allocated, mounted: mounted)
                    timer.setEventHandler {}; timer.cancel()
                    control.stop()
                    attempt.finish(result)
                }
            }
        } onCancel: { cancel() }
    }

    private func pipeline(owner: NativeAccountedOwner, bootSession: UUID, bytes: Int64, parent: URL,
                          control: NativeRamFactoryControl,
                          allocated: @Sendable (NativeRamAllocationIdentity, @Sendable () -> Bool) async throws -> Void,
                          mounted: @Sendable (NativeRamDiskIdentity, @Sendable () -> Bool) async throws -> Void) async -> NativeRamDiskCreation? {
        do {
            guard control.isPermitted,
                  let allocation = await perform({ try? self.backend(owner, bootSession, bytes, control) }),
                  control.isPermitted, allocation.identity.isValid, allocation.identity.owner == owner.owner,
                  allocation.identity.bootSession == bootSession, allocation.identity.allocatedBytes == bytes else { return nil }
            try await allocated(allocation.identity, { control.isPermitted })
            guard control.isPermitted, await perform({ allocation.format(control: control) }), control.isPermitted else { return nil }
            guard let result = await perform({ allocation.mount(at: parent, control: control) }), control.isPermitted,
                  result.identity.isValid, allocation.identity.matches(result.identity),
                  result.disk.device == result.identity.device, result.disk.mountPoint.path == result.identity.mountPoint else { return nil }
            try await mounted(result.identity, { control.isPermitted })
            guard control.isPermitted else { return nil }
            // All effects and callbacks have completed, so no creation child can reopen the
            // node after this explicit close and before recovery's fresh retirement pin.
            allocation.endCreation()
            return result
        } catch { return nil }
    }

    private func perform<Value: Sendable>(_ work: @escaping @Sendable () -> Value) async -> Value {
        await withCheckedContinuation { continuation in
            queue.async { continuation.resume(returning: work()) }
        }
    }
}

final class NativeRamFactoryControl: @unchecked Sendable {
    private let gate = NSLock()
    private var permitted = true
    private var process: Process?
    private let callerPermits: @Sendable () -> Bool
    init(permitted: @escaping @Sendable () -> Bool = { true }) { callerPermits = permitted }
    var isPermitted: Bool { callerPermits() && gate.withLock { permitted } }
    func launch(_ process: Process) throws {
        try gate.withLock {
            guard permitted, callerPermits() else { throw CancellationError() }
            try process.run()
            self.process = process
        }
    }
    func clear(_ process: Process) { gate.withLock { if self.process === process { self.process = nil } } }
    func stop() {
        let active = gate.withLock { permitted = false; return process }
        if let active, active.isRunning { active.terminate() }
    }
}

enum NativeRamPinnedCommand {
    private final class Child: @unchecked Sendable {
        private let gate = NSLock()
        private var process: Process?
        func set(_ process: Process) { gate.withLock { self.process = process } }
        var value: Process? { gate.withLock { process } }
    }
    static func run(_ path: String, _ arguments: [String], input: FileHandle? = nil,
                    control: NativeRamFactoryControl, timeout: TimeInterval = 20,
                    beforeLaunch: (@Sendable () -> Void)? = nil) -> String? {
        guard control.isPermitted else { return nil }
        let child = Child()
        defer {
            if let process = child.value {
                // This runs only on the creation queue. A caller timeout cannot certify
                // process exit; a failed kill must retain the device pin and physical slot.
                while process.isRunning { usleep(10_000) }
                control.clear(process)
            }
        }
        return RamDisk.run(path, arguments, timeout: timeout, standardInput: input,
            launchProcess: { process in child.set(process); beforeLaunch?(); try control.launch(process) })
    }
}
