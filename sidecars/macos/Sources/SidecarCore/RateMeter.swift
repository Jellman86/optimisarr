import Foundation

/// Turns a running byte count into a readable transfer rate.
///
/// Progress arrives in bursts — one report per completed 64 MB range or chunk — so the naive
/// "bytes since last report divided by the time since last report" swings wildly between a huge
/// number and zero. Smoothing gives a figure someone can actually read, and, more importantly,
/// one they can compare against what they expect their network to do.
public struct RateMeter: Sendable, Equatable {
    /// Weight given to the newest sample. Low enough to settle, high enough that a transfer
    /// genuinely slowing down shows it within a few reports.
    private static let smoothing = 0.4

    private var lastBytes: Int64?
    private var lastAt: Date?
    private var smoothed: Double?

    public init() {}

    /// Feeds in the total transferred so far. Returns bytes per second, or nil while there is not
    /// yet enough to say.
    public mutating func observe(_ bytes: Int64, at now: Date) -> Double? {
        defer {
            lastBytes = bytes
            lastAt = now
        }

        guard let lastBytes, let lastAt else { return nil }

        let elapsed = now.timeIntervalSince(lastAt)
        let moved = bytes - lastBytes
        // A report that carries no new bytes, or arrives at the same instant, says nothing about
        // speed. Reporting zero for it would drag the average down for no reason.
        guard elapsed > 0.05, moved > 0 else { return smoothed }

        let sample = Double(moved) / elapsed
        smoothed = smoothed.map { $0 + (sample - $0) * Self.smoothing } ?? sample
        return smoothed
    }

    /// Starts again, for the next stage or the next job.
    public mutating func reset() {
        self = RateMeter()
    }
}
