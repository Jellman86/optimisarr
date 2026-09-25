import Foundation

/// The current settings, readable from any thread.
///
/// `SidecarSettings` is `@MainActor` because the options panel observes it, but the job runner
/// reads these values from a background task while a job starts. Bridging that with
/// `MainActor.assumeIsolated` traps rather than blocks — it asserts an isolation that is not
/// there, and the app dies with `EXC_BREAKPOINT` in `_dispatch_assert_queue_fail`. That is exactly
/// what shipped in 0.1.5, which crashed the moment a job was claimed.
///
/// So the main actor *pushes* into this box whenever a setting changes, and the runner reads it
/// under a lock. The same shape as `PreviewGate`, for the same reason.
public final class SettingsSnapshot: @unchecked Sendable {
    private let lock = NSLock()
    private var location: WorkLocation
    private var budget: Int64

    public init(
        workLocation: WorkLocation = .applicationSupport,
        memoryBudgetBytes: Int64 = 0
    ) {
        self.location = workLocation
        self.budget = memoryBudgetBytes
    }

    public var workLocation: WorkLocation { lock.withLock { location } }
    public var memoryBudgetBytes: Int64 { lock.withLock { budget } }

    public func update(workLocation: WorkLocation, memoryBudgetBytes: Int64) {
        lock.withLock {
            location = workLocation
            budget = memoryBudgetBytes
        }
    }
}
