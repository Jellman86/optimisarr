import CryptoKit
import Foundation

public enum NativeWorkLedgerError: Error, Sendable {
    case invalidData
    case unsupportedSchema
    case invalidObligation
    case overflow
}

public struct NativeRamDiskIdentity: Codable, Equatable, Sendable {
    public let bootSession: UUID
    public let registryEntryId: UInt64
    public let backingEntryId: UInt64
    public let volumeId: UUID
    public let device: String
    public let mountPoint: String
    public let rootIdentity: String

    var isValid: Bool {
        bootSession != NativeWorkLedger.zero && volumeId != NativeWorkLedger.zero && registryEntryId > 0
            && backingEntryId > 0 && backingEntryId != registryEntryId
            && device.hasPrefix("/dev/disk") && !device.dropFirst(9).isEmpty
            && device.dropFirst(9).allSatisfy({ $0.isASCII && $0.isNumber })
            && NativeWorkLedger.validPath(mountPoint) && !rootIdentity.isEmpty && rootIdentity.utf8.count <= 256
    }
}

public struct NativeWorkObligation: Codable, Equatable, Sendable {
    public enum Phase: String, Codable, Sendable {
        case admitting, reserved, running, writersRetired, accountingReleased
    }
    public enum Receipt: String, Codable, Sendable {
        case notRequired, required, consumed
    }

    public let owner: NativeAccountedOwner
    public let bootSession: UUID
    public var root: String?
    public var scope: NativeScratchScope?
    public var nativeBytes: Int64
    public var scratchBytes: Int64
    public var diskVolume: String?
    public var ramBytes: Int64
    public var ramIdentity: NativeRamDiskIdentity?
    public var ramCreationAttempted = false
    public var ramRetired = false
    public var phase: Phase = .reserved
    public var receipt: Receipt = .notRequired

    public var holdsResources: Bool { phase != .accountingReleased }

    var isValid: Bool {
        guard owner.isValid, bootSession != NativeWorkLedger.zero, nativeBytes > 0, scratchBytes > 0, ramBytes >= 0,
              !nativeBytes.addingReportingOverflow(ramBytes).overflow,
              root == nil || NativeWorkLedger.validPath(root!), scope == nil || scope!.isValid,
              diskVolume == nil || !diskVolume!.isEmpty && diskVolume!.utf8.count <= 512 else { return false }
        if ramBytes == 0 {
            guard ramIdentity == nil, !ramCreationAttempted, !ramRetired else { return false }
        } else {
            guard diskVolume == nil, !(ramRetired && receipt == .required) else { return false }
            if let ramIdentity {
                guard ramIdentity.isValid, ramIdentity.bootSession == bootSession,
                      ramIdentity.mountPoint == root, ramIdentity.rootIdentity == scope?.root else { return false }
            }
        }
        switch phase {
        case .admitting:
            return receipt == .notRequired && !ramRetired
        case .reserved, .running:
            return root != nil && scope != nil && !ramRetired
                && (ramBytes > 0 ? ramIdentity != nil : diskVolume != nil)
                && receipt == (phase == .running ? .required : .notRequired)
        case .writersRetired:
            return receipt == .notRequired || root != nil && scope != nil
                && (ramBytes == 0 || ramIdentity != nil)
        case .accountingReleased:
            return (ramBytes == 0 || ramRetired && (ramIdentity != nil || !ramCreationAttempted))
                && (receipt == .notRequired || root != nil && scope != nil)
        }
    }
}

/// The payload is hashed as bytes, so future Foundation JSON formatting cannot erase obligations.
public enum NativeWorkLedger {
    public static let maximumBytes = 256 * 1024
    public static let maximumObligations = 16
    static let zero = UUID(uuid: (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0))
    private struct Payload: Codable { let schemaVersion: Int; let obligations: [NativeWorkObligation] }
    private struct Envelope: Codable { let schemaVersion: Int; let payload: Data; let checksum: String }

    public static func encode(_ obligations: [NativeWorkObligation]) throws -> Data {
        try validate(obligations)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let payload = try encoder.encode(Payload(schemaVersion: 2, obligations: obligations))
        let data = try encoder.encode(Envelope(schemaVersion: 2, payload: payload, checksum: checksum(payload)))
        guard data.count <= maximumBytes else { throw NativeWorkLedgerError.invalidData }
        return data
    }

    public static func decode(_ data: Data) throws -> [NativeWorkObligation] {
        do {
            guard !data.isEmpty, data.count <= maximumBytes, NativeWireJSON.hasUniqueProperties(data),
                  let outer = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  Set(outer.keys) == Set(["schemaVersion", "payload", "checksum"]) else { throw NativeWorkLedgerError.invalidData }
            let envelope = try JSONDecoder().decode(Envelope.self, from: data)
            guard envelope.schemaVersion == 2 else { throw NativeWorkLedgerError.unsupportedSchema }
            guard !envelope.payload.isEmpty, envelope.payload.count <= maximumBytes,
                  envelope.checksum == checksum(envelope.payload), NativeWireJSON.hasUniqueProperties(envelope.payload),
                  validShape(envelope.payload) else { throw NativeWorkLedgerError.invalidData }
            let payload = try JSONDecoder().decode(Payload.self, from: envelope.payload)
            guard payload.schemaVersion == 2 else { throw NativeWorkLedgerError.unsupportedSchema }
            try validate(payload.obligations)
            return payload.obligations
        } catch let error as NativeWorkLedgerError { throw error }
        catch { throw NativeWorkLedgerError.invalidData }
    }

    private static func validate(_ obligations: [NativeWorkObligation]) throws {
        guard obligations.count <= maximumObligations,
              Set(obligations.map(\.owner.owner)).count == obligations.count,
              obligations.allSatisfy(\.isValid) else { throw NativeWorkLedgerError.invalidObligation }
        var memory: Int64 = 0
        var disk: [String: Int64] = [:]
        for record in obligations where record.holdsResources {
            let total = record.nativeBytes.addingReportingOverflow(record.ramBytes)
            let next = memory.addingReportingOverflow(total.partialValue)
            guard !total.overflow, !next.overflow else { throw NativeWorkLedgerError.overflow }
            memory = next.partialValue
            if let volume = record.diskVolume {
                let nextDisk = disk[volume, default: 0].addingReportingOverflow(record.scratchBytes)
                guard !nextDisk.overflow else { throw NativeWorkLedgerError.overflow }
                disk[volume] = nextDisk.partialValue
            }
        }
    }

    static func validPath(_ path: String) -> Bool { path.hasPrefix("/") && path.utf8.count <= 4096 && !path.contains("\0") }
    private static func checksum(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }

    private static func validShape(_ data: Data) -> Bool {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              Set(object.keys) == Set(["schemaVersion", "obligations"]),
              let records = object["obligations"] as? [[String: Any]] else { return false }
        let known = Set(["owner", "bootSession", "root", "scope", "nativeBytes", "scratchBytes", "diskVolume", "ramBytes",
            "ramIdentity", "ramCreationAttempted", "ramRetired", "phase", "receipt"])
        let nested = [("owner", Set(["owner", "jobId", "leaseId"])), ("scope", Set(["root", "control", "gate"])),
            ("ramIdentity", Set(["bootSession", "registryEntryId", "backingEntryId", "volumeId", "device", "mountPoint", "rootIdentity"]))]
        return records.allSatisfy { record in
            Set(record.keys).isSubset(of: known) && nested.allSatisfy { key, keys in
                guard let value = record[key], !(value is NSNull) else { return true }
                guard let fields = value as? [String: Any] else { return false }
                return Set(fields.keys) == keys
            }
        }
    }
}
