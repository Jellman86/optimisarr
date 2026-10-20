import Darwin
import Foundation
import Testing
@testable import SidecarCore

@Suite("Native resource retirement service")
struct NativeResourceRetirementServiceTests {
    private let tools = NativeQualityCapabilityTests.tools
    private let root = URL(fileURLWithPath: "/private/fixture/work")
    private let scope = NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4")
    private let owner = NativeAccountedOwner(owner: UUID(), jobId: 12, leaseId: UUID())

    @Test("scope preparation and targeted retirement carry no credential, grant or media command")
    func privateRequests() async throws {
        let helper = RetirementExchangeFixture()
        let service = NativeResourceRetirementService(tools: tools, helper: helper)
        #expect(try await service.prepareScope(root: root) == scope)
        #expect(try await service.retire(root: root, owner: owner, scope: scope).retired)
        #expect(try await service.consume(root: root, owner: owner, scope: scope).alreadyAbsent)
        #expect(helper.requests.count == 3)
        for data in helper.requests {
            let body = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
            #expect(body["version"] as? Int == 2 && body["scratchRoot"] as? String == root.path)
            #expect(body["pairing"] == nil && body["assignment"] == nil && body["grantedMemoryBytes"] == nil)
            #expect(body["imageMetric"] == nil && body["audioMetric"] == nil && body["metadataTool"] == nil)
            if body["mode"] as? String == "scope" {
                #expect(body["accountedOwner"] == nil && body["accountingScope"] == nil)
            } else {
                let identity = try #require(body["accountedOwner"] as? [String: Any])
                #expect(identity["owner"] as? String == owner.owner.uuidString)
                #expect(identity["leaseId"] as? String == owner.leaseId.uuidString)
                #expect(identity["jobId"] as? Int == owner.jobId)
                #expect(body["accountingScope"] is [String: Any])
            }
        }
    }

    @Test("old helper replies never establish writer retirement or acknowledge consumption")
    func noDowngrade() async {
        let helper = RetirementExchangeFixture(version: 1)
        let service = NativeResourceRetirementService(tools: tools, helper: helper)
        await #expect(throws: NativeQualityHostError.self) { try await service.prepareScope(root: root) }
        await #expect(throws: NativeQualityHostError.self) { try await service.retire(root: root, owner: owner, scope: scope) }
        await #expect(throws: NativeQualityHostError.self) { try await service.consume(root: root, owner: owner, scope: scope) }
    }

    @Test("invalid root, scope or owner refuses before launching the helper")
    func invalidIdentity() async {
        let helper = RetirementExchangeFixture()
        let service = NativeResourceRetirementService(tools: tools, helper: helper)
        await #expect(throws: NativeQualityHostError.self) { try await service.prepareScope(root: URL(string: "https://example.com/work")!) }
        await #expect(throws: NativeQualityHostError.self) {
            try await service.retire(root: root, owner: owner, scope: NativeScratchScope(root: "", control: "x", gate: "y"))
        }
        await #expect(throws: NativeQualityHostError.self) {
            try await service.consume(root: root, owner: NativeAccountedOwner(owner: owner.owner, jobId: 0, leaseId: owner.leaseId), scope: scope)
        }
        #expect(helper.requests.isEmpty)
    }

    @Test("retirement timeouts cancel the owned helper before returning")
    func boundedRecovery() async {
        let helper = RetirementExchangeFixture(hang: true)
        let service = NativeResourceRetirementService(tools: tools, helper: helper, timeout: .milliseconds(20))
        await #expect(throws: NativeQualityHostError.self) { try await service.retire(root: root, owner: owner, scope: scope) }
        #expect(helper.stopped && helper.requests.count == 1)
    }

    @Test("a real helper ignoring SIGTERM cannot strand retirement past its owned shutdown grace", arguments: [false, true])
    func realUncooperativeHelper(closesOutput: Bool) async throws {
        let fixture = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: fixture, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: fixture) }
        let ready = fixture.appendingPathComponent("ignoring-termination")
        let executable = fixture.appendingPathComponent("native-helper")
        let barrier = fixture.appendingPathComponent("blocked-open")
        try #require(mkfifo(barrier.path, 0o600) == 0)
        func quoted(_ path: String) -> String { "'" + path.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let source = """
        #!/bin/sh
        trap '' TERM
        IFS= read -r request
        if \(closesOutput ? "true" : "false"); then
            printf '%s\\n' '{"version":2,"type":"scope","accountingScope":{"root":"darwin:1:2","control":"darwin:1:3","gate":"darwin:1:4"}}'
            exec 1>&-
        fi
        printf ready > \(quoted(ready.path))
        exec 3<> \(quoted(barrier.path))
        IFS= read -r -t 10 released <&3
        """
        try Data(source.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
        let service = NativeResourceRetirementService(tools: NativeQualityTools(host: executable,
            ffmpeg: fixture.appendingPathComponent("missing-ffmpeg")), timeout: .seconds(3))
        let started = Date()
        let task = Task { try await service.prepareScope(root: fixture.appendingPathComponent("work")) }
        try await waitFor("the real helper to install its termination handler") { FileManager.default.fileExists(atPath: ready.path) }
        await #expect(throws: NativeQualityHostError.self) { try await task.value }
        #expect(Date().timeIntervalSince(started) < 6)
    }
}

private final class RetirementExchangeFixture: NativeQualityExchanging, @unchecked Sendable {
    private let gate = NSLock()
    private var frames: [Data] = []
    private var ended = false
    let version: Int
    let hang: Bool
    init(version: Int = 2, hang: Bool = false) { self.version = version; self.hang = hang }
    var requests: [Data] { gate.withLock { frames } }
    var stopped: Bool { gate.withLock { ended } }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        gate.withLock { frames.append(request) }
        defer { gate.withLock { ended = true } }
        if hang { try await Task.sleep(for: .seconds(30)); throw NativeQualityHostError() }
        let json = try #require(JSONSerialization.jsonObject(with: request) as? [String: Any])
        switch json["mode"] as? String {
        case "scope":
            #expect(expectedJobId == nil)
            return NativeQualityReply(type: "scope", jobId: nil, delivered: false, capacityBlocked: false, capabilities: nil,
                version: version, accountingScope: NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4"))
        case "retire":
            return NativeQualityReply(type: "retired", jobId: expectedJobId, delivered: false, capacityBlocked: false, capabilities: nil,
                version: version, retirement: NativeScratchRetirement(retired: true, busy: false))
        case "forget":
            return NativeQualityReply(type: "forgotten", jobId: expectedJobId, delivered: false, capacityBlocked: false, capabilities: nil,
                version: version, consumption: NativeScratchConsumption(consumed: false, alreadyAbsent: true))
        default: throw NativeQualityHostError()
        }
    }
}
