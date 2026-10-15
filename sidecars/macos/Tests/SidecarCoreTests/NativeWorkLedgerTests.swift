import Foundation
import Testing
@testable import SidecarCore

@Suite("Native work ledger")
struct NativeWorkLedgerTests {
    private let boot = UUID()
    private func disk() -> NativeWorkObligation {
        NativeWorkObligation(owner: NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID()), bootSession: boot,
            root: "/previous/working/folder", scope: NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4"),
            nativeBytes: 512 * 1024 * 1024, scratchBytes: 100_000, diskVolume: "disk1", ramBytes: 0)
    }
    private func ram() -> NativeWorkObligation {
        var record = disk()
        record.root = "/Volumes/OptimisarrWork-fixture"
        record.diskVolume = nil
        record.ramBytes = 64 * 1024 * 1024
        record.ramIdentity = NativeRamDiskIdentity(bootSession: boot, registryEntryId: 123456, volumeId: UUID(),
            device: "/dev/disk9", mountPoint: record.root!, rootIdentity: record.scope!.root)
        return record
    }

    @Test("checksummed round trips retain previous actual roots and full obligations")
    func roundTrip() throws {
        var first = disk()
        first.nativeBytes = 9_007_199_254_740_993
        let records = [first, ram()]
        let decoded = try NativeWorkLedger.decode(NativeWorkLedger.encode(records))
        #expect(decoded == records)
        #expect(decoded[0].nativeBytes == 9_007_199_254_740_993)
        #expect(decoded[1].ramIdentity?.mountPoint == "/Volumes/OptimisarrWork-fixture")
    }

    @Test("writer retirement and receipt consumption do not release RAM or native accounting")
    func independentAccounting() throws {
        var record = ram()
        record.phase = .running; record.receipt = .required
        #expect(record.holdsResources)
        record.phase = .writersRetired; record.receipt = .consumed
        #expect(record.holdsResources && !record.ramRetired)
        #expect(try NativeWorkLedger.decode(NativeWorkLedger.encode([record]))[0].holdsResources)
        record.ramRetired = true
        record.phase = .accountingReleased
        #expect(!record.holdsResources)
        #expect(try NativeWorkLedger.decode(NativeWorkLedger.encode([record])) == [record])
    }

    @Test("admission intent can retain an uncertain RAM allocation before its physical identity exists")
    func admissionIntent() throws {
        var record = disk()
        record.phase = .admitting; record.root = nil; record.scope = nil; record.diskVolume = nil
        record.ramBytes = 64 * 1024 * 1024; record.ramCreationAttempted = true
        #expect(record.holdsResources)
        #expect(try NativeWorkLedger.decode(NativeWorkLedger.encode([record])) == [record])
        record.phase = .running; record.receipt = .required
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.encode([record]) }
    }

    @Test("an attempted RAM allocation with unknown identity cannot be declared retired")
    func uncertainRamRetirement() {
        var record = disk()
        record.root = nil; record.scope = nil; record.diskVolume = nil
        record.ramBytes = 64 * 1024 * 1024; record.ramCreationAttempted = true
        record.ramRetired = true; record.phase = .accountingReleased
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.encode([record]) }
    }

    @Test("RAM cannot be retired while its required receipt is still on the volume")
    func prematureRamEjection() {
        var record = ram()
        record.phase = .writersRetired; record.receipt = .required; record.ramRetired = true
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.encode([record]) }
    }

    @Test("invalid phase, owner, mount or overflow cannot silently drop an obligation")
    func invalidStates() {
        var noScope = disk(); noScope.scope = nil
        var noReceipt = disk(); noReceipt.phase = .running
        var premature = ram(); premature.phase = .accountingReleased
        var changedMount = ram(); changedMount.ramIdentity = NativeRamDiskIdentity(bootSession: boot,
            registryEntryId: 123456, volumeId: UUID(), device: "/dev/disk9", mountPoint: "/different", rootIdentity: "darwin:1:2")
        var overflow = ram(); overflow.nativeBytes = Int64.max
        var negative = disk(); negative.scratchBytes = -1
        for record in [noScope, noReceipt, premature, changedMount, overflow, negative] {
            #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.encode([record]) }
        }
        let duplicate = disk()
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.encode([duplicate, duplicate]) }
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.encode((0..<17).map { _ in disk() }) }
    }

    @Test("corruption, unsupported schema and ambiguous JSON never become an empty ledger")
    func corruptLedger() throws {
        let encoded = try NativeWorkLedger.encode([disk()])
        var object = try #require(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        object["checksum"] = String(repeating: "0", count: 64)
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.decode(JSONSerialization.data(withJSONObject: object)) }
        object = try #require(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        object["schemaVersion"] = 99
        #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.decode(JSONSerialization.data(withJSONObject: object)) }
        for data in [Data(), Data("{}".utf8), Data("{\"schemaVersion\":1,\"schemaVersion\":1}".utf8), encoded.dropLast()] {
            #expect(throws: NativeWorkLedgerError.self) { try NativeWorkLedger.decode(Data(data)) }
        }
    }
}
