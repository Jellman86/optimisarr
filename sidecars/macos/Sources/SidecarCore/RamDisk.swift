import Darwin
import Foundation

/// A RAM-backed volume, created for one job and destroyed when it ends.
///
/// Created per job rather than once at launch so the Mac only carries the memory while there is
/// work to justify it, and sized to the job so an idle sidecar holds none at all.
///
/// **Destroying it matters more than creating it.** A RAM disk that outlives its job holds real
/// memory until the Mac reboots, and nothing in the interface would show it. Every exit path
/// therefore ejects, and `SidecarSettings` also sweeps any strays left by a crash at launch.
/// Carries the child's output back from the reading thread.
private final class OutputBox: @unchecked Sendable {
    var data = Data()
    var exceeded = false
    var reachedEOF = false
}

private final class CommandDrainLifetime: @unchecked Sendable {
    private let lock = NSLock()
    private var stopped = false
    private var sources: [Int32: any DispatchSourceProtocol] = [:]
    let deadline: DispatchTime

    init(timeout: TimeInterval) { deadline = .now() + timeout }
    func add(_ source: any DispatchSourceProtocol, key: Int32) {
        let cancelled = lock.withLock {
            source.activate()
            if stopped { return true }
            sources[key] = source
            return false
        }
        // Activation precedes cancellation, including a deadline reached during setup.
        if cancelled { source.cancel() }
    }
    func end(_ key: Int32) { lock.withLock { sources.removeValue(forKey: key) }?.cancel() }
    func stop() {
        let active = lock.withLock {
            stopped = true
            let active = Array(sources.values)
            sources.removeAll()
            return active
        }
        for source in active { source.cancel() }
    }
    var shouldStop: Bool { lock.withLock { stopped || DispatchTime.now() >= deadline } }
}

public struct RamDisk: Sendable {
    public let mountPoint: URL
    public let device: String

    /// Volume names all share this prefix so a stray from a previous run can be recognised.
    public static let volumePrefix = "OptimisarrWork-"

    /// How long any one step may take. Creating a volume is a fraction of a second; a step that
    /// has not finished in this long is wedged, and letting it block would hold the worker's only
    /// job slot for ever — which is exactly what happened on 2026-09-13, leaving a sidecar that
    /// checked in cheerfully and never asked for work again.
    /// Runs a command and returns its trimmed output, or nil if it failed or took too long.
    static func run(_ path: String, _ arguments: [String], timeout: TimeInterval = 20,
                    maximumOutputBytes: Int = 1024 * 1024,
                    standardInput: FileHandle? = nil,
                    launchProcess: (@Sendable (Process) throws -> Void)? = nil,
                    onReaderStopped: (@Sendable () -> Void)? = nil) -> String? {
        guard maximumOutputBytes > 0, timeout.isFinite, timeout > 0, timeout <= 3600 else { return nil }
        let lifetime = CommandDrainLifetime(timeout: timeout)
        defer { lifetime.stop() }
        let process = Process()
        let pipe = Pipe(), errors = Pipe()
        pipe.sealFromOtherChildren()
        errors.sealFromOtherChildren()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = errors
        if let standardInput { process.standardInput = standardInput }
        let finished = DispatchGroup()
        finished.enter()
        process.terminationHandler = { _ in finished.leave() }
        do {
            if let launchProcess { try launchProcess(process) }
            else { try process.run() }
        } catch {
            process.terminationHandler = nil
            finished.leave()
            SidecarLog.storage.error("\(path, privacy: .public) could not be launched")
            return nil
        }

        // Nonblocking readiness handlers leave shared threads idle while descendants retain a
        // pipe. Cancellation handlers exclusively close descriptors; cross-thread FileHandle
        // closure cannot safely interrupt Foundation's blocking read locks.
        // The termination callback avoids waitUntilExit's cross-thread run-loop dependency.
        let box = OutputBox(), errorBox = OutputBox()
        let drainQueue = DispatchQueue(label: "uk.optimisarr.ram-command-output", qos: .utility)
        func read(_ pipe: Pipe, into output: OutputBox) {
            finished.enter()
            let fd = pipe.fileHandleForReading.fileDescriptor
            let flags = fcntl(fd, F_GETFL)
            guard flags >= 0, fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 else {
                try? pipe.fileHandleForReading.close()
                onReaderStopped?()
                finished.leave()
                lifetime.stop()
                if process.isRunning { process.terminate() }
                return
            }
            let source = DispatchSource.makeReadSource(fileDescriptor: fd, queue: drainQueue)
            source.setCancelHandler {
                try? pipe.fileHandleForReading.close()
                onReaderStopped?()
                finished.leave()
            }
            source.setEventHandler {
                var bytes = [UInt8](repeating: 0, count: 8192)
                while !lifetime.shouldStop {
                    let count = bytes.withUnsafeMutableBytes { Darwin.read(fd, $0.baseAddress, $0.count) }
                    if count == 0 {
                        output.reachedEOF = true
                        lifetime.end(fd)
                        return
                    }
                    if count > 0 {
                        guard count <= maximumOutputBytes - output.data.count else {
                            output.exceeded = true
                            lifetime.stop()
                            if process.isRunning { process.terminate() }
                            return
                        }
                        output.data.append(contentsOf: bytes.prefix(count))
                        continue
                    }
                    if errno == EINTR { continue }
                    if errno == EAGAIN || errno == EWOULDBLOCK { return }
                    lifetime.stop()
                    if process.isRunning { process.terminate() }
                    return
                }
                lifetime.stop()
            }
            lifetime.add(source, key: fd)
        }
        read(pipe, into: box)
        read(errors, into: errorBox)
        let timer = DispatchSource.makeTimerSource(queue: drainQueue)
        timer.schedule(deadline: lifetime.deadline)
        timer.setEventHandler { lifetime.stop() }
        lifetime.add(timer, key: -1)

        guard finished.wait(timeout: lifetime.deadline) == .success else {
            SidecarLog.storage.error(
                "\(path, privacy: .public) did not finish within \(timeout)s; giving up on it")
            lifetime.stop()
            if process.isRunning { process.terminate() }
            if finished.wait(timeout: .now() + 1) == .timedOut, process.isRunning {
                kill(process.processIdentifier, SIGKILL)
                _ = finished.wait(timeout: .now() + 1)
            }
            return nil
        }

        guard process.terminationStatus == 0, box.reachedEOF, errorBox.reachedEOF,
              !box.exceeded, !errorBox.exceeded else {
            SidecarLog.storage.error(
                "\(path, privacy: .public) exited \(process.terminationStatus)")
            return nil
        }
        return String(decoding: box.data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func allocationBytes(for bytes: Int64) -> Int64? {
        guard bytes > 0 else { return nil }
        let overhead = bytes / 20 + 32 * 1024 * 1024
        let total = bytes.addingReportingOverflow(overhead)
        guard !total.overflow else { return nil }
        let rounded = total.partialValue.addingReportingOverflow(511)
        guard !rounded.overflow else { return nil }
        return rounded.partialValue / 512 * 512
    }

    /// Creates a volume of at least this many bytes. Nil if the Mac would not give it one, which
    /// is treated as "use a disk instead" rather than as a failure worth stopping a job for.
    public static func create(bytes: Int64) -> RamDisk? {
        guard let allocated = allocationBytes(for: bytes) else { return nil }
        let sectors = allocated / 512

        guard let device = run("/usr/bin/hdiutil", ["attach", "-nomount", "ram://\(sectors)"]) else {
            return nil
        }

        let name = "\(volumePrefix)\(UUID().uuidString.prefix(8))"
        guard run("/sbin/newfs_hfs", ["-v", name, device]) != nil else {
            _ = run("/usr/bin/hdiutil", ["detach", device, "-force"])
            return nil
        }

        // -nobrowse keeps it out of the Finder sidebar: it is machinery, not somewhere to put
        // files. The mount point is read back rather than assumed, because macOS appends a suffix
        // if that name is somehow already taken.
        guard run("/usr/sbin/diskutil", ["mount", "-nobrowse", device]) != nil,
              let info = run("/usr/sbin/diskutil", ["info", device]),
              let line = info.split(separator: "\n").first(where: { $0.contains("Mount Point:") }),
              case let path = line.split(separator: ":", maxSplits: 1)[1]
                  .trimmingCharacters(in: .whitespaces),
              !path.isEmpty
        else {
            _ = run("/usr/bin/hdiutil", ["detach", device, "-force"])
            return nil
        }
        let mountPoint = URL(fileURLWithPath: path)

        return RamDisk(mountPoint: mountPoint, device: device)
    }

    /// A surviving writer must veto ejection. Callers retain the RAM reservation when
    /// detaching fails, so an interrupted native helper cannot lose its working volume.
    @discardableResult
    public func destroy() -> Bool {
        Self.run("/usr/bin/hdiutil", ["detach", device]) != nil
    }

    /// Ejects anything left behind by a previous run that did not exit cleanly.
    static func strayVolumeNames(in volumes: [String], ownedVolumeNames: Set<String>?) -> [String] {
        volumes.filter { $0.hasPrefix(volumePrefix) && (ownedVolumeNames?.contains($0) ?? true) }
    }

    public static func sweepStrays(ownedVolumeNames: Set<String>? = nil) {
        guard let volumes = try? FileManager.default.contentsOfDirectory(atPath: "/Volumes") else { return }
        for volume in strayVolumeNames(in: volumes, ownedVolumeNames: ownedVolumeNames) {
            _ = run("/usr/sbin/diskutil", ["eject", "/Volumes/\(volume)"])
        }
    }
}
