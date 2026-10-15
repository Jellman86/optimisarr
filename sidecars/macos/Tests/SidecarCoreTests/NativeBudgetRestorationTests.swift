import Foundation
import Testing
@testable import SidecarCore

@Suite("Native obligation restoration")
struct NativeBudgetRestorationTests {
    @Test("a persisted obligation is restored without a new availability grant and fences fresh work")
    func restoreWithoutAdmission() throws {
        let memory = MemoryWorkBudget()
        let disk = DiskWorkBudget()
        let owner = UUID()
        #expect(memory.restoreObligation(owner, nativeBytes: 80, ramBytes: 40))
        #expect(disk.restoreObligation(owner, volume: "original-disk", bytes: 90))
        #expect(memory.reservedBytes == 120 && memory.reservedRamBytes == 40)
        #expect(memory.available(100) == 0)
        #expect(memory.reserve(nativeBytes: 1, ramBytes: 0, ramLimit: 10, availableMemory: 100) == nil)
        #expect(disk.available(volume: "original-disk", liveBytes: 100) == 10)
        #expect(disk.reserve(volume: "original-disk", bytes: 11, available: 100) == nil)
        #expect(disk.reserve(volume: "another-disk", bytes: 11, available: 100) != nil)
        memory.release(owner); disk.release(owner)
        memory.release(owner); disk.release(owner)
        #expect(memory.reservedBytes == 0)
        #expect(disk.available(volume: "original-disk", liveBytes: 100) == 100)
    }

    @Test("restoring the same owner is idempotent but conflicting obligations cannot replace held charges")
    func idempotenceAndConflict() {
        let memory = MemoryWorkBudget()
        let disk = DiskWorkBudget()
        let owner = UUID()
        for _ in 0..<3 {
            #expect(memory.restoreObligation(owner, nativeBytes: 50, ramBytes: 20))
            #expect(disk.restoreObligation(owner, volume: "original", bytes: 60))
        }
        #expect(!memory.restoreObligation(owner, nativeBytes: 50, ramBytes: 21))
        #expect(!disk.restoreObligation(owner, volume: "changed", bytes: 60))
        #expect(!disk.restoreObligation(owner, volume: "original", bytes: 61))
        #expect(memory.reservedBytes == 70 && memory.reservedRamBytes == 20)
        #expect(disk.available(volume: "original", liveBytes: 100) == 40)
    }

    @Test("malformed or overflowing restoration refuses before changing the ledger")
    func overflow() {
        let memory = MemoryWorkBudget()
        let disk = DiskWorkBudget()
        #expect(!memory.restoreObligation(UUID(), nativeBytes: Int64.max, ramBytes: 1))
        #expect(!memory.restoreObligation(UUID(), nativeBytes: -1, ramBytes: 10))
        #expect(!memory.restoreObligation(UUID(), nativeBytes: 0, ramBytes: 0))
        #expect(!disk.restoreObligation(UUID(), volume: "", bytes: 10))
        #expect(memory.restoreObligation(UUID(), nativeBytes: Int64.max, ramBytes: 0))
        #expect(!memory.restoreObligation(UUID(), nativeBytes: 1, ramBytes: 0))
        #expect(disk.restoreObligation(UUID(), volume: "original", bytes: Int64.max))
        #expect(!disk.restoreObligation(UUID(), volume: "original", bytes: 1))
        #expect(memory.reservedBytes == Int64.max)
        #expect(disk.available(volume: "original", liveBytes: Int64.max) == 0)
    }
}
