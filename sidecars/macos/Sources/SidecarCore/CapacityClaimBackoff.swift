import Foundation

/// A worker-wide pause after an acknowledged resource refusal; heartbeats remain independent.
public final class CapacityClaimBackoff: @unchecked Sendable {
    private let gate = NSLock()
    private let uptime: @Sendable () -> TimeInterval
    private var until: TimeInterval = 0
    public init(uptime: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) { self.uptime = uptime }
    public var isWaiting: Bool { gate.withLock { uptime() < until } }
    public func block() { gate.withLock { until = uptime() + 30 } }
}
