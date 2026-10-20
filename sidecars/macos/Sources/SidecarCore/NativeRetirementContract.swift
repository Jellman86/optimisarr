import Foundation

public struct NativeScratchScope: Codable, Equatable, Sendable {
    public let root: String
    public let control: String
    public let gate: String

    var isValid: Bool {
        [root, control, gate].allSatisfy { value in
            !value.isEmpty && value.utf8.count <= 256 && value.utf8.allSatisfy { $0 > 32 && $0 < 127 }
        }
    }
}

public struct NativeAccountedOwner: Codable, Equatable, Sendable {
    public let owner: UUID
    public let jobId: Int
    public let leaseId: UUID

    var isValid: Bool { jobId > 0 && owner != Self.zero && leaseId != Self.zero }
    private static let zero = UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
}

public struct NativeScratchRetirement: Codable, Equatable, Sendable {
    public let retired: Bool
    public let busy: Bool
    var isValid: Bool { !retired || !busy }
}

/// Absence can acknowledge consumption after persisted retirement; it cannot retire writers.
public struct NativeScratchConsumption: Codable, Equatable, Sendable {
    public let consumed: Bool
    public let alreadyAbsent: Bool
    var isValid: Bool { !consumed || !alreadyAbsent }
}
