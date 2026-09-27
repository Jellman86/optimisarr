import Foundation

struct MemoryWorkPlan: Equatable, Sendable {
    let workingBytes: Int64
    let allocatedBytes: Int64

    static func make(sourceBytes: Int64, maximumCandidateBytes: Int64?) -> Self? {
        guard sourceBytes > 0, let maximumCandidateBytes, maximumCandidateBytes > 0 else { return nil }
        let media = sourceBytes.addingReportingOverflow(maximumCandidateBytes)
        guard !media.overflow else { return nil }
        // Measurement logs and the final encoder writes also need room. The entire volume,
        // including this allowance and filesystem overhead, counts against the shared budget.
        let working = media.partialValue.addingReportingOverflow(64 * 1024 * 1024)
        guard !working.overflow, let allocated = RamDisk.allocationBytes(for: working.partialValue) else { return nil }
        return Self(workingBytes: working.partialValue, allocatedBytes: allocated)
    }
}

/// One ledger for every job in this process, including jobs whose volume is still being created.
public final class MemoryWorkBudget: @unchecked Sendable {
    public static let shared = MemoryWorkBudget()
    private let lock = NSLock()
    private var reservations: [UUID: Int64] = [:]
    public init() {}
    public var reservedBytes: Int64 { lock.withLock { reservations.values.reduce(0, +) } }

    func reserve(bytes: Int64, limit: Int64) -> UUID? {
        lock.withLock {
            let used = reservations.values.reduce(0, +)
            guard bytes > 0, limit >= used, bytes <= limit - used else { return nil }
            let token = UUID()
            reservations[token] = bytes
            return token
        }
    }

    func release(_ token: UUID) { _ = lock.withLock { reservations.removeValue(forKey: token) } }
}

public struct WorkStorage: Sendable, Equatable {
    public let inMemory: Bool
    public let path: String
    public let fallbackReason: String?
    public init(inMemory: Bool, path: String, fallbackReason: String? = nil) {
        self.inMemory = inMemory; self.path = path; self.fallbackReason = fallbackReason
    }
    public var summary: String { inMemory ? "Working in RAM" : "Working on disk" }
}
