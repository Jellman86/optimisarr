import Foundation
import Testing
@testable import SidecarCore

@Suite("Native retirement contract")
struct NativeRetirementContractTests {
    private let scope = #"{"root":"darwin:1:2","control":"darwin:1:3","gate":"darwin:1:4"}"#

    @Test("version two recovery accepts bounded counters without retirement or media authority")
    func scopedRecoveryFrames() throws {
        let recovery = #"{"removed":0,"busy":0,"deferred":0,"invalid":0,"scanLimited":false}"#
        var frames = NativeQualityFrames(expectedJobId: nil, expectedVersion: 2)
        _ = try frames.append(Data("{\"version\":2,\"type\":\"recovered\",\"recovery\":\(recovery)}\n".utf8))
        #expect(try frames.finish(exitCode: 0).version == 2)
        for extra: [String: Any] in [["nativeResourcesRetired": true], ["accountingScope": [:]], ["jobId": 12]] {
            var body: [String: Any] = ["version": 2, "type": "recovered", "recovery": try JSONSerialization.jsonObject(with: Data(recovery.utf8))]
            body.merge(extra) { _, new in new }
            let encoded = try JSONSerialization.data(withJSONObject: body) + Data([10])
            #expect(throws: NativeQualityHostError.self) {
                var invalid = NativeQualityFrames(expectedJobId: nil, expectedVersion: 2)
                _ = try invalid.append(encoded)
            }
        }
    }

    @Test("version two is explicit and old helper output cannot acknowledge an accounted request")
    func versionFence() throws {
        #expect(throws: NativeQualityHostError.self) {
            var frames = NativeQualityFrames(expectedJobId: nil, expectedVersion: 2)
            _ = try frames.append(Data(#"{"version":1,"type":"scope","accountingScope":{}}"#.utf8) + Data([10]))
        }
        var frames = NativeQualityFrames(expectedJobId: nil, expectedVersion: 2)
        _ = try frames.append(Data("{\"version\":2,\"type\":\"scope\",\"accountingScope\":\(scope)}\n".utf8))
        let reply = try frames.finish(exitCode: 0)
        #expect(reply.version == 2 && reply.accountingScope?.root == "darwin:1:2")
        #expect(throws: NativeQualityHostError.self) { try frames.finish(exitCode: 1) }
    }

    @Test("retirement requires matching job and exclusive positive evidence")
    func retirement() throws {
        for (retired, busy) in [(true, false), (false, true), (false, false)] {
            var frames = NativeQualityFrames(expectedJobId: 12, expectedVersion: 2)
            _ = try frames.append(Data("{\"version\":2,\"type\":\"retired\",\"jobId\":12,\"retirement\":{\"retired\":\(retired),\"busy\":\(busy)}}\n".utf8))
            let reply = try frames.finish(exitCode: 0)
            #expect(reply.retirement == NativeScratchRetirement(retired: retired, busy: busy))
        }
    }

    @Test("consumption distinguishes removal and replayed absence without certifying retirement")
    func consumption() throws {
        for (consumed, absent) in [(true, false), (false, true), (false, false)] {
            var frames = NativeQualityFrames(expectedJobId: 12, expectedVersion: 2)
            _ = try frames.append(Data("{\"version\":2,\"type\":\"forgotten\",\"jobId\":12,\"consumption\":{\"consumed\":\(consumed),\"alreadyAbsent\":\(absent)}}\n".utf8))
            let reply = try frames.finish(exitCode: 0)
            #expect(reply.consumption == NativeScratchConsumption(consumed: consumed, alreadyAbsent: absent))
            #expect(reply.retirement == nil)
        }
    }

    @Test("delivery success stays distinct from writer retirement")
    func completed() throws {
        for retired in [true, false] {
            var frames = NativeQualityFrames(expectedJobId: 12, expectedVersion: 2)
            _ = try frames.append(Data("{\"version\":2,\"type\":\"completed\",\"jobId\":12,\"outcome\":{\"jobId\":12,\"delivered\":true},\"nativeWorkspaceCreated\":true,\"nativeResourcesRetired\":\(retired)}\n".utf8))
            let reply = try frames.finish(exitCode: 0)
            #expect(reply.delivered && reply.nativeWorkspaceCreated == true && reply.nativeResourcesRetired == retired)
        }
    }

    @Test("missing, contradictory, wrong-job or authority-bearing proof cannot release resources")
    func invalidProofs() {
        for body in [
            #"{"version":2,"type":"retired","jobId":99,"retirement":{"retired":true,"busy":false}}"#,
            #"{"version":2,"type":"retired","jobId":12,"retirement":{"retired":true,"busy":true}}"#,
            #"{"version":2,"type":"retired","jobId":12,"retirement":{"retired":true}}"#,
            #"{"version":2,"type":"retired","jobId":12,"retirement":{"retired":true,"busy":false},"nativeMemoryBytes":1}"#,
            #"{"version":2,"type":"forgotten","jobId":12,"consumption":{"consumed":true,"alreadyAbsent":true}}"#,
            #"{"version":2,"type":"completed","jobId":12,"outcome":{"jobId":12,"delivered":true}}"#,
            #"{"version":2,"type":"completed","jobId":12,"outcome":{"jobId":12,"delivered":true},"nativeWorkspaceCreated":false,"nativeResourcesRetired":true}"#
        ] {
            #expect(throws: NativeQualityHostError.self) {
                var frames = NativeQualityFrames(expectedJobId: 12, expectedVersion: 2)
                _ = try frames.append(Data(body.utf8) + Data([10]))
                _ = try frames.finish(exitCode: 0)
            }
        }
        for invalidScope in [#"{}"#, #"{"root":"","control":"x","gate":"y"}"#,
                             #"{"root":"a b","control":"x","gate":"y"}"#] {
            #expect(throws: NativeQualityHostError.self) {
                var frames = NativeQualityFrames(expectedJobId: nil, expectedVersion: 2)
                _ = try frames.append(Data("{\"version\":2,\"type\":\"scope\",\"accountingScope\":\(invalidScope)}\n".utf8))
            }
        }
    }

    @Test("accounted assignment requests bind the owner and physical scope under version two")
    func accountedRequests() throws {
        let native = try #require(NativeQualityAssignment(data: NativeQualityAssignmentTests.wire()))
        let owner = NativeAccountedOwner(owner: UUID(), jobId: native.jobId, leaseId: try #require(UUID(uuidString: native.leaseId)))
        let physical = NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4")
        let tools = NativeQualityCapabilityTests.tools
        let root = URL(fileURLWithPath: "/fixture/work")
        let pairing = StoredPairing(serverAddress: "http://localhost", credential: String(repeating: "a", count: 43), workerId: 1)
        let data = try native.request(mode: "run", tools: tools, scratchRoot: root, pairing: pairing,
            grantedMemoryBytes: 100_000, version: 2, owner: owner, accountingScope: physical)
        let object = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        #expect(object["version"] as? Int == 2 && object["accountedOwner"] is [String: Any])
        #expect(object["accountingScope"] is [String: Any])
        #expect(throws: NativeQualityHostError.self) {
            try native.request(mode: "run", tools: tools, scratchRoot: root, pairing: pairing,
                grantedMemoryBytes: 100_000, owner: owner, accountingScope: physical)
        }
        #expect(throws: NativeQualityHostError.self) {
            try native.request(mode: "run", tools: tools, scratchRoot: root, pairing: pairing,
                grantedMemoryBytes: 100_000, version: 2)
        }
        #expect(throws: NativeQualityHostError.self) {
            try native.request(mode: "run", tools: tools, scratchRoot: root, pairing: pairing,
                grantedMemoryBytes: 100_000, version: 2,
                owner: NativeAccountedOwner(owner: owner.owner, jobId: owner.jobId + 1, leaseId: owner.leaseId), accountingScope: physical)
        }
        #expect(throws: NativeQualityHostError.self) {
            try native.request(mode: "requirements", tools: tools, scratchRoot: root, version: 2, owner: owner, accountingScope: physical)
        }
        let requirements = try native.request(mode: "requirements", tools: tools, scratchRoot: root, version: 2)
        #expect((try JSONSerialization.jsonObject(with: requirements) as? [String: Any])?["version"] as? Int == 2)
    }
}
