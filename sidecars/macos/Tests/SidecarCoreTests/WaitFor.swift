import Foundation
import Testing

/// Polls a condition instead of sleeping a fixed time.
///
/// The session's loop runs on its own tasks, so a test that sleeps "long enough" is really betting
/// on how loaded the machine is. That bet was lost on a CI runner on 2026-09-13: a 200 ms wait
/// expired before the first claim had even gone out, and a test that passes in milliseconds
/// locally failed. Polling makes the fast case fast and the slow case correct.
@MainActor
func waitFor(
    timeout: TimeInterval = 5,
    _ description: @autoclosure () -> String = "the expected state",
    _ condition: @MainActor () -> Bool
) async throws {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if condition() { return }
        try await Task.sleep(nanoseconds: 5_000_000)
    }
    Issue.record("Timed out after \(timeout)s waiting for \(description()).")
}
