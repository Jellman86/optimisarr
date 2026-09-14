import Foundation
import Testing
@testable import SidecarCore

@Suite("CPU load")
struct CpuMonitorTests {
    private func ticks(user: UInt64 = 0, system: UInt64 = 0, idle: UInt64 = 0, nice: UInt64 = 0)
        -> CpuTicks
    {
        CpuTicks(user: user, system: system, idle: idle, nice: nice)
    }

    @Test("busy time as a fraction of the interval, not of all time since boot")
    func fractionOfTheInterval() {
        // A machine long since booted, busy for three of the last four ticks. Reporting against the
        // cumulative totals instead would have it near idle for ever.
        let before = ticks(user: 1_000, system: 500, idle: 8_500)
        let after = ticks(user: 1_003, system: 500, idle: 8_501)
        #expect(CpuLoadCalculator.busyFraction(from: before, to: after) == 0.75)
    }

    @Test("nice time counts as busy")
    func niceIsBusy() {
        #expect(CpuLoadCalculator.busyFraction(
            from: ticks(), to: ticks(idle: 2, nice: 2)) == 0.5)
    }

    @Test("cannot say, rather than idle, when there is nothing to compare")
    func refusesToInvent() {
        // Each of these would produce a plausible-looking 0%, which reads as an idle Mac — a
        // different claim from "no answer", and the wrong one to put in front of someone deciding
        // whether the machine can take more work.
        #expect(CpuLoadCalculator.busyFraction(from: ticks(idle: 10), to: ticks(idle: 10)) == nil)
        // Counters going backwards: the machine slept, or the values wrapped.
        #expect(CpuLoadCalculator.busyFraction(from: ticks(idle: 10), to: ticks(idle: 4)) == nil)
        #expect(CpuLoadCalculator.busyFraction(from: ticks(user: 9, idle: 1), to: ticks(user: 2, idle: 9)) == nil)
    }

    @Test("a fully busy and a fully idle machine both read correctly")
    func extremes() {
        #expect(CpuLoadCalculator.busyFraction(from: ticks(), to: ticks(user: 10)) == 1)
        #expect(CpuLoadCalculator.busyFraction(from: ticks(), to: ticks(idle: 10)) == 0)
    }

    @Test("the first sample has nothing to compare against and says so")
    func firstSampleIsSilent() {
        let readings = Queue([ticks(user: 10, idle: 90), ticks(user: 30, idle: 170)])
        let monitor = CpuMonitor(read: { readings.next() })

        #expect(monitor.sample() == nil)     // one reading is not a measurement
        #expect(monitor.sample() == 0.2)     // 20 busy ticks of the 100 that passed
    }

    @Test("a machine whose ticks cannot be read reports nothing")
    func unreadable() {
        let monitor = CpuMonitor(read: { nil })
        #expect(monitor.sample() == nil)
    }
}


/// A thread-safe queue of scripted readings, so the monitor's `@Sendable` reader can be driven
/// from a test.
private final class Queue<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var items: [T]

    init(_ items: [T]) { self.items = items }

    func next() -> T? {
        lock.withLock { items.isEmpty ? nil : items.removeFirst() }
    }
}
