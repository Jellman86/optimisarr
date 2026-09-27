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
}
