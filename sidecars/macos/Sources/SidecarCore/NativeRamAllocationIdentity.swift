import Foundation

/// Recovery authority for an allocation that may not yet have a filesystem. This receipt
/// never authorizes formatting: that requires the factory's still-live original device pin.
public struct NativeRamAllocationIdentity: Codable, Equatable, Sendable {
    public let bootSession: UUID
    public let owner: UUID
    public let registryEntryId: UInt64
    public let backingEntryId: UInt64
    public let device: String
    public let allocatedBytes: Int64

    var isValid: Bool {
        bootSession != NativeWorkLedger.zero && owner != NativeWorkLedger.zero
            && registryEntryId > 0 && backingEntryId > 0 && registryEntryId != backingEntryId
            && allocatedBytes > 0 && allocatedBytes % 512 == 0
            && device.hasPrefix("/dev/disk") && !device.dropFirst(9).isEmpty
            && device.dropFirst(9).allSatisfy { $0.isASCII && $0.isNumber }
    }

    func matches(_ mounted: NativeRamDiskIdentity) -> Bool {
        bootSession == mounted.bootSession && registryEntryId == mounted.registryEntryId
            && backingEntryId == mounted.backingEntryId && device == mounted.device
    }
}
