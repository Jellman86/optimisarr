import Foundation
import Testing
@testable import SidecarCore

@Suite("Transfer rate")
struct RateMeterTests {
    private let start = Date(timeIntervalSince1970: 1_000_000)

    @Test("the first report cannot say a rate, because there is nothing to compare against")
    func needsTwoSamples() {
        var meter = RateMeter()

        #expect(meter.observe(0, at: start) == nil)
    }

    @Test("a steady transfer reports its real rate")
    func steadyRate() {
        var meter = RateMeter()
        _ = meter.observe(0, at: start)
        let rate = meter.observe(10_000_000, at: start.addingTimeInterval(1))

        #expect(rate == 10_000_000)
    }

    @Test("a change is smoothed rather than jumped to, so the number can be read")
    func smoothsChanges() {
        // Progress arrives one completed 64 MB range at a time, so raw deltas swing between a
        // huge number and zero. A figure that flickers is not information.
        var meter = RateMeter()
        _ = meter.observe(0, at: start)
        _ = meter.observe(10_000_000, at: start.addingTimeInterval(1))
        let rate = meter.observe(30_000_000, at: start.addingTimeInterval(2))

        // Between the old rate and the new sample, not at either.
        #expect(rate! > 10_000_000)
        #expect(rate! < 20_000_000)
    }

    @Test("a report carrying no new bytes leaves the rate alone")
    func idleReportsDoNotDragItDown() {
        // A renewal tick can repeat the same byte count. Treating that as "zero bytes per second"
        // would report a stalled transfer that is running perfectly well.
        var meter = RateMeter()
        _ = meter.observe(0, at: start)
        let moving = meter.observe(10_000_000, at: start.addingTimeInterval(1))
        let repeated = meter.observe(10_000_000, at: start.addingTimeInterval(2))

        #expect(repeated == moving)
    }

    @Test("resetting forgets the previous stage")
    func resets() {
        var meter = RateMeter()
        _ = meter.observe(0, at: start)
        _ = meter.observe(10_000_000, at: start.addingTimeInterval(1))
        meter.reset()

        #expect(meter.observe(500, at: start.addingTimeInterval(2)) == nil)
    }
}
