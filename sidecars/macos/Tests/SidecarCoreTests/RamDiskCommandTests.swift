import Darwin
import Foundation
import Testing
@testable import SidecarCore

@Suite("RAM disk command lifecycle")
struct RamDiskCommandTests {
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
        defer {
            FileManager.default.createFile(atPath: release.path, contents: Data())
            // The fixture's descendant exits naturally; it remains alive during the assertion.
            for _ in 0..<100 where !FileManager.default.fileExists(atPath: root.appendingPathComponent("done").path) {
                usleep(10_000)
            }
            try? FileManager.default.removeItem(at: root)
        }
        let script = root.appendingPathComponent("hold.py")
        try """
        import os, pathlib, sys, time
        root = pathlib.Path(sys.argv[1])
        if os.fork():
            os._exit(0)
        os.close(1)
        (root / "ready").write_text(str(os.getpid()))
        deadline = time.monotonic() + 10
        while not (root / "release").exists() and time.monotonic() < deadline:
            time.sleep(0.01)
        (root / "done").touch()
        os._exit(0)
        """.write(to: script, atomically: true, encoding: .utf8)
        let stopped = ReaderCount()
        let clock = ContinuousClock(), start = clock.now
        #expect(RamDisk.run("/usr/bin/python3", [script.path, root.path], timeout: 1,
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
