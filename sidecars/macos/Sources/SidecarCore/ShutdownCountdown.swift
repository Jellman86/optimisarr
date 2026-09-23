import Foundation

/// An in-memory shutdown request. The process never restores it after a restart.
public struct ShutdownCountdown: Equatable, Sendable {
    public private(set) var armed = false
    public private(set) var detail = ""
    public var canCancel: Bool { armed && (!initiated || failure != nil) }
    private var readySince: Date?
    private var initiated = false
    private var failure: String?

    public init() {}

    public func secondsRemaining(at now: Date) -> Int? {
        guard armed, !initiated, let readySince else { return nil }
        return max(0, Int(ceil(readySince.addingTimeInterval(60).timeIntervalSince(now))))
    }

    public mutating func arm() {
        guard !armed else { return }
        armed = true
        readySince = nil
        initiated = false
        failure = nil
        detail = "Waiting for the server to confirm draining"
    }

    public mutating func cancel() {
        guard canCancel else { return }
        self = Self()
    }

    /// Returns true once, when the operating-system request may begin.
    public mutating func evaluate(at now: Date, ready: Bool, activeJobs: Int,
                                  unconfirmed: Bool = false, serverUnreachable: Bool = false) -> Bool {
        guard armed, !initiated, failure == nil else { return false }
        if serverUnreachable {
            readySince = nil
            detail = "Server unreachable; shutdown is blocked until check-ins recover" +
                (activeJobs > 0 ? " and held work is acknowledged." : ".")
            return false
        }
        if activeJobs > 0 {
            readySince = nil
            detail = "Waiting for \(activeJobs) job\(activeJobs == 1 ? "" : "s") to finish verification, return, and acknowledgement"
            return false
        }
        if unconfirmed {
            readySince = nil
            detail = "A job result was not acknowledged by the server. Inspect diagnostics; shutdown is blocked."
            return false
        }
        guard ready else {
            readySince = nil
            detail = "Waiting for the server to confirm draining"
            return false
        }
        if readySince == nil { readySince = now }
        let remaining = secondsRemaining(at: now) ?? 60
        detail = "No jobs held. Shutting down in \(remaining) seconds; cancel at any time."
        guard remaining == 0 else { return false }
        detail = "Confirming the server before shutdown; cancel is still available."
        return true
    }

    public mutating func begin() -> Bool {
        guard canCancel, failure == nil, readySince != nil else { return false }
        initiated = true
        detail = "Asking macOS to shut down"
        return true
    }

    public mutating func deferShutdown(_ reason: String) {
        guard canCancel else { return }
        readySince = nil
        detail = reason
    }

    public mutating func fail(_ reason: String) {
        failure = reason
        detail = reason
    }
}
