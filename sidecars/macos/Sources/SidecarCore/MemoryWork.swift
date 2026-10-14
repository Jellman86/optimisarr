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
    private struct Allocation {
        let nativeBytes: Int64
        let ramBytes: Int64
        var total: Int64 { nativeBytes + ramBytes }
    }
    private var reservations: [UUID: Allocation] = [:]
    public init() {}
    public var reservedBytes: Int64 { lock.withLock { reservations.values.reduce(0) { $0 + $1.total } } }
    public var reservedRamBytes: Int64 { lock.withLock { reservations.values.reduce(0) { $0 + $1.ramBytes } } }

    func reserve(nativeBytes: Int64, ramBytes: Int64, ramLimit: Int64, availableMemory: Int64?) -> UUID? {
        lock.withLock {
            let total = nativeBytes.addingReportingOverflow(ramBytes)
            let used = reservations.values.reduce(0) { $0 + $1.total }
            let ramUsed = reservations.values.reduce(0) { $0 + $1.ramBytes }
            guard nativeBytes >= 0, ramBytes >= 0, !total.overflow, total.partialValue > 0,
                  let availableMemory, availableMemory >= used, total.partialValue <= availableMemory - used,
                  ramBytes == 0 || ramLimit >= ramUsed && ramBytes <= ramLimit - ramUsed else { return nil }
            let token = UUID()
            reservations[token] = Allocation(nativeBytes: nativeBytes, ramBytes: ramBytes)
            return token
        }
    }

    func release(_ token: UUID, retainRam: Bool = false) {
        lock.withLock {
            guard retainRam, let held = reservations[token], held.ramBytes > 0 else {
                reservations.removeValue(forKey: token); return
            }
            reservations[token] = Allocation(nativeBytes: 0, ramBytes: held.ramBytes)
        }
    }
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
