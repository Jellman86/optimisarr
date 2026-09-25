import Foundation
import Testing
@testable import SidecarCore

/// Working in memory is the setting most able to make a Mac unusable, and the one most likely to
/// be asked for on a job that cannot possibly fit. Both halves are worth pinning down.
@Suite("Work location")
struct WorkLocationTests {
    private let sixteenGB: Int64 = 16 * 1024 * 1024 * 1024
    private let fourGB: Int64 = 4 * 1024 * 1024 * 1024

    @Test("the default budget is a quarter of memory")
    func defaultBudget() {
        // A RAM disk is not memory macOS can reclaim under pressure; it is a mounted volume
        // holding real pages.
        #expect(WorkLocationPolicy.defaultBudget(physicalBytes: sixteenGB) == fourGB)
    }

    @Test("a job that fits runs in memory")
    func smallJobRunsInMemory() {
        let resolved = WorkLocationPolicy.resolve(
            preference: .memory, requiredBytes: 2 * 1024 * 1024 * 1024, memoryBudget: fourGB)

        #expect(resolved == .memory)
    }

    @Test("a job too big for the budget runs on disk rather than being refused")
    func largeJobFallsBackToDisk() {
        // Refusing would lose the work over a preference, and the job would come straight back to
        // be offered again. A film and its candidate are commonly well past any sane budget.
        let resolved = WorkLocationPolicy.resolve(
            preference: .memory, requiredBytes: 9 * 1024 * 1024 * 1024, memoryBudget: fourGB)

        #expect(resolved == .applicationSupport)
    }

    @Test("a job of unknown size runs on disk, because the budget cannot be checked")
    func unknownSizeFallsBack() {
        let resolved = WorkLocationPolicy.resolve(
            preference: .memory, requiredBytes: 0, memoryBudget: fourGB)

        #expect(resolved == .applicationSupport)
    }

    @Test("the fallback says why, so an ignored setting is not a silent one")
    func fallbackIsExplained() {
        let reason = WorkLocationPolicy.fallbackReason(
            preference: .memory, requiredBytes: 9 * 1024 * 1024 * 1024, memoryBudget: fourGB)

        #expect(reason?.contains("more than the") == true)
        #expect(reason?.contains("on disk") == true)
    }

    @Test("nothing is explained when the setting did apply, or was never memory")
    func noReasonWhenNothingChanged() {
        #expect(WorkLocationPolicy.fallbackReason(
            preference: .memory, requiredBytes: 1024, memoryBudget: fourGB) == nil)
        #expect(WorkLocationPolicy.fallbackReason(
            preference: .applicationSupport, requiredBytes: 9 * 1024 * 1024 * 1024, memoryBudget: fourGB) == nil)
    }

    @Test("a chosen budget is held inside what the machine can give up")
    func budgetIsClamped() {
        #expect(WorkLocationPolicy.clampBudget(sixteenGB, physicalBytes: sixteenGB) == sixteenGB / 2)
        #expect(WorkLocationPolicy.clampBudget(1024, physicalBytes: sixteenGB)
            == Int64(Double(sixteenGB) * WorkLocationPolicy.minimumBudgetFraction))
    }

    @Test("a chosen folder is used as given")
    func folderIsHonoured() {
        let folder = URL(fileURLWithPath: "/Volumes/Scratch")
        let resolved = WorkLocationPolicy.resolve(
            preference: .folder(folder), requiredBytes: 9 * 1024 * 1024 * 1024, memoryBudget: fourGB)

        #expect(resolved == .folder(folder))
    }
}
