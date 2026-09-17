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
    private static let stepTimeout: TimeInterval = 20

    /// Runs a command and returns its trimmed output, or nil if it failed or took too long.
    private static func run(_ path: String, _ arguments: [String]) -> String? {
        let process = Process()
        let pipe = Pipe()
        pipe.sealFromOtherChildren()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch {
            SidecarLog.storage.error("\(path, privacy: .public) could not be launched")
            return nil
        }

        // Read on another thread so a child that holds the pipe open cannot block the wait, and
        // wait with a deadline so nothing here can hang the job that called it.
        let finished = DispatchSemaphore(value: 0)
        let box = OutputBox()
        DispatchQueue.global(qos: .utility).async {
            box.data = pipe.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            finished.signal()
        }

        guard finished.wait(timeout: .now() + stepTimeout) == .success else {
            SidecarLog.storage.error(
                "\(path, privacy: .public) did not finish within \(Int(stepTimeout))s; giving up on it")
            process.terminate()
            return nil
        }

        guard process.terminationStatus == 0 else {
            SidecarLog.storage.error(
                "\(path, privacy: .public) exited \(process.terminationStatus)")
            return nil
        }
        return String(decoding: box.data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Creates a volume of at least this many bytes. Nil if the Mac would not give it one, which
    /// is treated as "use a disk instead" rather than as a failure worth stopping a job for.
    public static func create(bytes: Int64) -> RamDisk? {
        // hdiutil counts 512-byte sectors. A little headroom covers the filesystem's own overhead,
        // which would otherwise make a volume sized exactly to the job too small to hold it.
        let withOverhead = Int64(Double(bytes) * 1.05) + 32 * 1024 * 1024
        let sectors = withOverhead / 512

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

    /// Ejects it. Forced, because a job that has just failed may still hold a file handle and the
    /// alternative to forcing is leaking the memory until reboot.
    public func destroy() {
        _ = Self.run("/usr/bin/hdiutil", ["detach", device, "-force"])
    }

    /// Ejects anything left behind by a previous run that did not exit cleanly.
    public static func sweepStrays() {
        guard let volumes = try? FileManager.default.contentsOfDirectory(atPath: "/Volumes") else { return }
        for volume in volumes where volume.hasPrefix(volumePrefix) {
            _ = run("/usr/sbin/diskutil", ["eject", "/Volumes/\(volume)"])
        }
    }
}
