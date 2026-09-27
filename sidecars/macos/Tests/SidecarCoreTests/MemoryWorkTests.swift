import Foundation
import Testing
@testable import SidecarCore

@Suite("Shared RAM storage")
struct MemoryWorkTests {
    @Test("candidate space follows its limit and accounts for filesystem overhead")
    func sizing() throws {
        let plan = try #require(MemoryWorkPlan.make(sourceBytes: 100_000_000, maximumCandidateBytes: 90_000_000))
        #expect(plan.workingBytes >= 190_000_000)
        #expect(plan.allocatedBytes > plan.workingBytes)
        #expect(plan.allocatedBytes % 512 == 0)
    }

    @Test("unbounded, invalid and overflowing sizes cannot allocate RAM")
    func invalidSizes() {
        #expect(MemoryWorkPlan.make(sourceBytes: 100, maximumCandidateBytes: nil) == nil)
        #expect(MemoryWorkPlan.make(sourceBytes: 100, maximumCandidateBytes: 0) == nil)
        #expect(MemoryWorkPlan.make(sourceBytes: -1, maximumCandidateBytes: 100) == nil)
        #expect(MemoryWorkPlan.make(sourceBytes: Int64.max, maximumCandidateBytes: 100) == nil)
        #expect(RamDisk.allocationBytes(for: Int64.max) == nil)
    }

    @Test("simultaneous reservations share one limit and release is idempotent")
    func concurrentReservations() async {
        let budget = MemoryWorkBudget()
        let reservations = await withTaskGroup(of: UUID?.self, returning: [UUID].self) { group in
            for _ in 0..<20 { group.addTask { budget.reserve(bytes: 30, limit: 100) } }
            var held: [UUID] = []
            for await token in group { if let token { held.append(token) } }
            return held
        }
        #expect(reservations.count == 3)
        #expect(budget.reservedBytes == 90)
        for token in reservations { budget.release(token); budget.release(token) }
        #expect(budget.reservedBytes == 0)
        #expect(budget.reserve(bytes: 100, limit: 100) != nil)
        #expect(budget.reserve(bytes: 1, limit: 50) == nil)
    }
}
