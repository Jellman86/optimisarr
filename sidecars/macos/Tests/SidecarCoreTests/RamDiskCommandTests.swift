import Darwin
import Foundation
import Testing
@testable import SidecarCore

@Suite("RAM disk command lifecycle")
struct RamDiskCommandTests {
    @Test("an explicitly inherited input survives exec without closing the caller's handle")
    func inheritedInput() throws {
        let path = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data("original device pin fixture".utf8).write(to: path)
        defer { try? FileManager.default.removeItem(at: path) }
        let input = try FileHandle(forReadingFrom: path); defer { try? input.close() }
        #expect(fcntl(input.fileDescriptor, F_SETFD, FD_CLOEXEC) == 0)
        #expect(RamDisk.run("/bin/sh", ["-c", "exec /bin/cat"], standardInput: input) == "original device pin fixture")
        #expect(fcntl(input.fileDescriptor, F_GETFD) >= 0)
    }
    @Test("short commands complete from background tasks without waiting on another thread's run loop")
    func repeatedCommands() async {
        await withTaskGroup(of: Void.self) { group in
            for _ in 0..<20 {
                group.addTask {
                    let output = await withCheckedContinuation { continuation in
                        DispatchQueue.global().async {
                            continuation.resume(returning:
                                RamDisk.run("/usr/bin/printf", ["device\\n"], timeout: 2))
                        }
                    }
                    #expect(output == "device")
                }
            }
        }
    }

    @Test("failed commands cannot be mistaken for a created device")
    func failure() {
        #expect(RamDisk.run("/usr/bin/false", []) == nil)
        #expect(RamDisk.run("/nonexistent/optimisarr-command", []) == nil)
    }

    @Test("a stalled command returns within a bounded time")
    func timeout() {
        let clock = ContinuousClock()
        let start = clock.now
        #expect(RamDisk.run("/bin/sleep", ["10"], timeout: 0.05) == nil)
        #expect(clock.now - start < .seconds(3))
    }

    @Test("inherited stderr cannot keep pipe readers alive past the command deadline")
    func inheritedStderr() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let release = root.appendingPathComponent("release")
        try #require(mkfifo(release.path, 0o600) == 0)
        defer {
            // The descendant blocks in the FIFO until explicitly released, without a polling
            // process or interpreter startup inside the command's one-second deadline.
            for _ in 0..<100 {
                let writer = Darwin.open(release.path, O_WRONLY | O_NONBLOCK | O_CLOEXEC)
                if writer >= 0 {
                    let sealed = fcntl(writer, F_SETNOSIGPIPE, 1) == 0
                    #expect(sealed)
                    if sealed { _ = Data("release\n".utf8).withUnsafeBytes { Darwin.write(writer, $0.baseAddress, $0.count) } }
                    Darwin.close(writer)
                    break
                }
                usleep(10_000)
            }
            for _ in 0..<100 where !FileManager.default.fileExists(atPath: root.appendingPathComponent("done").path) {
                usleep(10_000)
            }
            try? FileManager.default.removeItem(at: root)
        }
        let script = root.appendingPathComponent("hold.sh")
        try """
        (
            exec 1>&-
            exec 3<> "$1/release"
            printf ready > "$1/ready"
            IFS= read -r -t 10 released <&3
            : > "$1/done"
        ) &
        exit 0
        """.write(to: script, atomically: true, encoding: .utf8)
        let stopped = ReaderCount()
        let clock = ContinuousClock(), start = clock.now
        #expect(RamDisk.run("/bin/sh", [script.path, root.path], timeout: 1,
                            onReaderStopped: { stopped.increment() }) == nil)
        #expect(clock.now - start < .seconds(3))
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("ready").path))
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent("done").path))
        #expect(stopped.value == 2)
    }

    @Test("oversized OS-tool output is bounded and stops its owned producer")
    func outputLimit() {
        let clock = ContinuousClock(), start = clock.now
        #expect(RamDisk.run("/usr/bin/yes", [], timeout: 0.5, maximumOutputBytes: 1024) == nil)
        #expect(clock.now - start < .seconds(3))
    }
}

private final class ReaderCount: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    func increment() { lock.withLock { count += 1 } }
    var value: Int { lock.withLock { count } }
}
