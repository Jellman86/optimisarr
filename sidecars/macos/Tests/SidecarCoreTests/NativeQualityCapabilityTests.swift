import Foundation
import Testing
@testable import SidecarCore

@Suite("Native quality capability proof")
struct NativeQualityCapabilityTests {
    static let revision = String(repeating: "a", count: 40)
    static let proof = Data("{\"imageEncoders\":[\"mjpeg\"],\"imageMetadataSupported\":true,\"audioMetricRevision\":\"\(revision)\",\"imageMetricRevision\":\"\(revision)\"}".utf8)
    static let tools = NativeQualityTools(host: URL(fileURLWithPath: "/fixture/helper"), ffmpeg: URL(fileURLWithPath: "/fixture/ffmpeg"),
        imageMetric: URL(fileURLWithPath: "/fixture/image"), audioMetric: URL(fileURLWithPath: "/fixture/audio"), metadataTool: URL(fileURLWithPath: "/fixture/exiftool"))

    @Test("proof comes from the shared helper without a pairing, assignment or memory grant")
    func privateProof() async throws {
        let helper = CapabilityHelper(proof: Self.proof)
        let prober = NativeQualityCapabilityProber(tools: Self.tools, helper: helper)
        let proof = try #require(await prober.probe(scratchRoot: URL(fileURLWithPath: "/fixture/scratch")))
        #expect(proof.imageEncoders == ["mjpeg"])
        #expect(proof.imageMetadataSupported)
        #expect(proof.audioMetricRevision == Self.revision)
        #expect(proof.availableMemoryBytes == nil)
        let request = try #require(helper.request)
        let body = try #require(JSONSerialization.jsonObject(with: request) as? [String: Any])
        #expect(body["mode"] as? String == "capabilities")
        #expect(body["ffmpegPath"] as? String == "/fixture/ffmpeg")
        #expect(body["metadataTool"] as? String == "/fixture/exiftool")
        #expect(body["scratchRoot"] as? String == "/fixture/scratch")
        #expect(body["pairing"] == nil && body["assignment"] == nil && body["grantedMemoryBytes"] == nil)
    }

    @Test("missing, refused or malformed native proof never advertises a capability")
    func unavailable() async {
        for raw in [Data("{}".utf8), Data("{\"imageEncoders\":[\"mjpeg\"],\"imageMetadataSupported\":1}".utf8),
                    Data("{\"imageEncoders\":[\"mjpeg\"],\"audioMetricRevision\":\"old\"}".utf8),
                    Data("{\"imageEncoders\":[\"bad-name\"]}".utf8),
                    Data("{\"imageEncoders\":[],\"ImageEncoders\":[\"mjpeg\"]}".utf8),
                    Data("{\"imageEncoders\":[],\"imageEncoders\":[\"mjpeg\"]}".utf8),
                    Data("{\"imageEncoders\":[],\"availableMemoryBytes\":999}".utf8),
                    Data("{\"imageEncoders\":[],\"scratchUsesMemory\":false}".utf8)] {
            let helper = CapabilityHelper(proof: raw)
            #expect(await NativeQualityCapabilityProber(tools: Self.tools, helper: helper).probe(scratchRoot: URL(fileURLWithPath: "/fixture")) == nil)
        }
        let refused = CapabilityHelper(proof: nil)
        #expect(await NativeQualityCapabilityProber(tools: Self.tools, helper: refused).probe(scratchRoot: URL(fileURLWithPath: "/fixture")) == nil)
    }

    @Test("image-only proof participates in the ordinary capability collector")
    func ordinaryCollector() async {
        let native = NativeQualityCapabilityProber(tools: Self.tools, helper: CapabilityHelper(proof: Self.proof))
        let prober = CapabilityProber(ffmpeg: Self.tools.ffmpeg, runner: ScriptedRunner(["encoders": (0, "")]), native: native)
        let caps = await prober.probe(name: "Fixture", maxConcurrency: 2)
        #expect(caps.perceptual?.imageEncoders == ["mjpeg"])
        #expect(caps.videoEncoders.isEmpty && caps.audioEncoders.isEmpty)
        #expect(caps.maxConcurrency == 2)
        #expect(WorkerProtocol.maximum == 13)
    }

    @Test("pairing and heartbeat send typed proof and current capacity")
    func wireProof() async throws {
        var proof = try #require(WorkerPerceptualCapabilities(proof: Self.proof))
        proof.availableMemoryBytes = 1234; proof.scratchUsesMemory = false
        let caps = SidecarCapabilities(name: "Fixture", perceptual: proof)
        let transport = StubTransport(json: ["workerId": 7, "credential": "fixture", "protocolVersion": 10, "heartbeatIntervalSeconds": 30])
        let client = SidecarClient(transport: transport)
        _ = try await client.pair(serverAddress: "localhost:8787", pin: "fixture", capabilities: caps)
        var sent = try #require(transport.sentJSON["perceptual"] as? [String: Any])
        #expect(sent["imageEncoders"] as? [String] == ["mjpeg"])
        #expect(sent["availableMemoryBytes"] as? Int64 == 1234)
        _ = try await client.heartbeat(serverAddress: "localhost:8787", credential: "fixture", freeScratchBytes: 99, maxConcurrency: 1, capabilities: caps)
        sent = try #require(transport.sentJSON["perceptual"] as? [String: Any])
        #expect(sent["audioMetricRevision"] as? String == Self.revision)
        #expect(sent["scratchUsesMemory"] as? Bool == false)
    }

    @Test("fresh capacity subtracts app reservations and follows the chosen disk")
    func liveResources() throws {
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget()
        let held = try #require(memory.reserve(nativeBytes: 80, ramBytes: 20, ramLimit: 1000, availableMemory: 1000))
        let diskHeld = try #require(disk.reserve(volume: "chosen", bytes: 120, available: 1000))
        defer { memory.release(held); disk.release(diskHeld) }
        let settings = SettingsSnapshot(workLocation: .folder(URL(fileURLWithPath: "/chosen")))
        let resources = NativeWorkerResources(settings: settings, memoryBudget: memory, diskBudget: disk, availableMemory: { 800 }, workspace: { root in
            #expect(root.path == "/chosen")
            return NativeDiskCapacity(volume: "chosen", availableBytes: 900)
        })
        let proof = try #require(WorkerPerceptualCapabilities(proof: Self.proof))
        let caps = SidecarCapabilities(name: "Fixture", perceptual: proof)
        let refreshed = resources.refresh(caps)
        #expect(refreshed.freeScratchBytes == 780)
        #expect(refreshed.perceptual?.availableMemoryBytes == 700)
        #expect(refreshed.perceptual?.scratchUsesMemory == false)
        #expect(refreshed.perceptual?.imageMetricRevision == Self.revision)
    }

    @Test("unknown resource samples clear previous capacity while preserving tool proof")
    func unknownResources() throws {
        var proof = try #require(WorkerPerceptualCapabilities(proof: Self.proof))
        proof.availableMemoryBytes = 999; proof.scratchUsesMemory = true
        let caps = SidecarCapabilities(name: "Fixture", freeScratchBytes: 999, perceptual: proof)
        let resources = NativeWorkerResources(availableMemory: { nil }, workspace: { _ in nil })
        let refreshed = resources.refresh(caps)
        #expect(refreshed.freeScratchBytes == 0)
        #expect(refreshed.perceptual?.availableMemoryBytes == nil)
        #expect(refreshed.perceptual?.scratchUsesMemory == nil)
        #expect(refreshed.perceptual?.imageEncoders == ["mjpeg"])
    }

    @Test("timed-out qualification waits for helper cancellation and returns no proof")
    func timedOut() async {
        let helper = HangingCapabilityHelper()
        let result = await NativeQualityCapabilityProber(tools: Self.tools, helper: helper, timeout: .milliseconds(20))
            .probe(scratchRoot: URL(fileURLWithPath: "/fixture"))
        #expect(result == nil)
        #expect(helper.stopped)
    }

    @Test("RAM preference advertises the admitted disk fallback and capacity never becomes negative")
    func conservativeFallback() throws {
        let memory = MemoryWorkBudget(), disk = DiskWorkBudget()
        let memoryHeld = try #require(memory.reserve(nativeBytes: 900, ramBytes: 0, ramLimit: 0, availableMemory: 1000))
        let diskHeld = try #require(disk.reserve(volume: "shared", bytes: 900, available: 1000))
        defer { memory.release(memoryHeld); disk.release(diskHeld) }
        let settings = SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 1000)
        let resources = NativeWorkerResources(settings: settings, scratchRoot: URL(fileURLWithPath: "/fallback"),
            memoryBudget: memory, diskBudget: disk, availableMemory: { 500 }, workspace: { root in
                #expect(root.path == "/fallback")
                return NativeDiskCapacity(volume: "shared", availableBytes: 500)
            })
        let proof = try #require(WorkerPerceptualCapabilities(proof: Self.proof))
        let caps = resources.refresh(SidecarCapabilities(name: "Fixture", perceptual: proof))
        #expect(caps.freeScratchBytes == 0)
        #expect(caps.perceptual?.availableMemoryBytes == 0)
        #expect(caps.perceptual?.scratchUsesMemory == false)
    }
}

private final class HangingCapabilityHelper: NativeQualityExchanging, @unchecked Sendable {
    private let gate = NSLock()
    private var ended = false
    var stopped: Bool { gate.withLock { ended } }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        defer { gate.withLock { ended = true } }
        try await Task.sleep(for: .seconds(60))
        throw NativeQualityHostError()
    }
}

private final class CapabilityHelper: NativeQualityExchanging, @unchecked Sendable {
    private let gate = NSLock()
    private let proof: Data?
    private var stored: Data?
    var request: Data? { gate.withLock { stored } }
    init(proof: Data?) { self.proof = proof }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        #expect(expectedJobId == nil)
        gate.withLock { stored = request }
        return NativeQualityReply(type: proof == nil ? "refused" : "capabilities", jobId: nil, delivered: false, capacityBlocked: false, capabilities: proof)
    }
}
