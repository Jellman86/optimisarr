import Foundation
import Testing
@testable import SidecarCore

struct ShutdownCountdownTests {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func idleNeedsConfirmedDrainThenCountsDown() {
        var plan = ShutdownCountdown()
        plan.arm()
        let unconfirmed = plan.evaluate(at: now, ready: false, activeJobs: 0)
        let started = plan.evaluate(at: now, ready: true, activeJobs: 0)
        #expect(!unconfirmed)
        #expect(!started)
        #expect(plan.secondsRemaining(at: now) == 60)
        let due = plan.evaluate(at: now.addingTimeInterval(60), ready: true, activeJobs: 0)
        let began = plan.begin()
        let repeated = plan.evaluate(at: now.addingTimeInterval(61), ready: true, activeJobs: 0)
        #expect(due)
        #expect(began)
        #expect(!repeated)
    }

    @Test func heldJobAndFailedJobAcknowledgementDelayCountdown() {
        var plan = ShutdownCountdown()
        plan.arm()
        let busy = plan.evaluate(at: now, ready: false, activeJobs: 1)
        #expect(!busy)
        #expect(plan.detail.contains("acknowledgement"))
        let released = plan.evaluate(at: now.addingTimeInterval(10), ready: true, activeJobs: 0)
        #expect(!released)
        #expect(plan.secondsRemaining(at: now.addingTimeInterval(10)) == 60)
    }

    @Test func disconnectResetsCountdownAndCancelStopsIt() {
        var plan = ShutdownCountdown()
        plan.arm()
        let started = plan.evaluate(at: now, ready: true, activeJobs: 0)
        let disconnected = plan.evaluate(at: now.addingTimeInterval(30), ready: false,
                                         activeJobs: 0, serverUnreachable: true)
        let disconnectedDetail = plan.detail
        let reconnected = plan.evaluate(at: now.addingTimeInterval(61), ready: true, activeJobs: 0)
        #expect(!started)
        #expect(!disconnected)
        #expect(disconnectedDetail.contains("Server unreachable"))
        #expect(!reconnected)
        #expect(plan.secondsRemaining(at: now.addingTimeInterval(61)) == 60)
        plan.cancel()
        let afterCancel = plan.evaluate(at: now.addingTimeInterval(200), ready: true, activeJobs: 0)
        #expect(!afterCancel)
    }

    @Test func deniedPermissionDoesNotRetry() {
        var plan = ShutdownCountdown()
        plan.arm()
        let started = plan.evaluate(at: now, ready: true, activeJobs: 0)
        let due = plan.evaluate(at: now.addingTimeInterval(60), ready: true, activeJobs: 0)
        let began = plan.begin()
        #expect(!started)
        #expect(due)
        #expect(began)
        plan.fail("macOS denied shutdown permission")
        #expect(plan.detail.contains("denied"))
        let repeated = plan.evaluate(at: now.addingTimeInterval(120), ready: true, activeJobs: 0)
        #expect(!repeated)
    }

    @Test func unacknowledgedResultBlocksShutdown() {
        var plan = ShutdownCountdown()
        plan.arm()
        let blocked = plan.evaluate(at: now, ready: false, activeJobs: 0, unconfirmed: true)
        #expect(!blocked)
        #expect(plan.detail.contains("not acknowledged"))
        #expect(plan.secondsRemaining(at: now) == nil)
    }

    @Test func cancelDuringFinalServerCheckPreventsShutdown() {
        var plan = ShutdownCountdown()
        plan.arm()
        _ = plan.evaluate(at: now, ready: true, activeJobs: 0)
        let due = plan.evaluate(at: now.addingTimeInterval(60), ready: true, activeJobs: 0)
        #expect(due)
        #expect(plan.canCancel)
        plan.cancel()
        let began = plan.begin()
        #expect(!began)
    }
}
