import Foundation
import Testing
@testable import SidecarCore

@Suite("Opt-in diagnostic journal")
struct DiagnosticJournalTests {
    @Test("off by default, scoped, stage deduplicated and stopped on expired consent")
    func consent() async throws {
        let journal = DiagnosticJournal(); let lease = UUID().uuidString
        await journal.assignment(leaseId: lease, jobId: 42)
        #expect(await journal.pending() == nil)
        let now = Date(); let consent = DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: 42)
        await journal.apply(consent)
        await journal.stage(leaseId: lease, stage: "Encoding", seconds: 1, status: 200)
        await journal.stage(leaseId: lease, stage: "Encoding", seconds: 2, status: 200)
        let batch = try #require(await journal.pending()); #expect(batch.events.filter { $0.reasonCode == "Worker.StageChanged" }.count == 1)
        await journal.acknowledge(batch, through: batch.events.map(\.sequence).max() ?? 0)
        #expect(await journal.pending() == nil)
        await journal.apply(DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(-1), scopedJobId: nil))
        await journal.record(leaseId: lease, reason: "Worker.LeaseReleased")
        #expect(await journal.pending() == nil)
    }
    @Test("pending records survive restart but recording needs a fresh check-in")
    func restart() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let journal = DiagnosticJournal(directory: root); let now = Date()
        let consent = DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: nil)
        await journal.apply(consent); let lease = UUID().uuidString
        await journal.assignment(leaseId: lease, jobId: 42)
        await journal.record(leaseId: lease, reason: "Worker.StageChanged", stage: "Bearer secret-token", seconds: .nan)
        let recovered = DiagnosticJournal(directory: root)
        #expect(await recovered.pending() == nil)
        await recovered.apply(consent)
        #expect(await recovered.pending() != nil)
        let text = String(decoding: try await recovered.export(), as: UTF8.self)
        #expect(!text.contains("secret-token")); #expect(!text.contains("NaN"))
    }
    @Test("expired local records leave no file while capture is off")
    func retention() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let journal = DiagnosticJournal(directory: root); let now = Date()
        await journal.apply(DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: nil))
        await journal.assignment(leaseId: UUID().uuidString, jobId: 42)
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent(DiagnosticJournal.fileName).path))
        await journal.apply(nil, now: now.addingTimeInterval(8 * 86400))
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent(DiagnosticJournal.fileName).path))
    }
    @Test("local recovery revalidates every exported string")
    func recoverySanitization() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let journal = DiagnosticJournal(directory: root); let now = Date()
        await journal.apply(DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: nil))
        await journal.assignment(leaseId: UUID().uuidString, jobId: 42)
        let path = root.appendingPathComponent(DiagnosticJournal.fileName)
        var saved = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: path)) as? [String: Any])
        var entries = try #require(saved["entries"] as? [[String: Any]])
        var event = try #require(entries[0]["event"] as? [String: Any])
        event["stage"] = "Bearer private-secret"; event["ffmpegSha256"] = "/private-secret/ffmpeg"
        entries[0]["event"] = event; saved["entries"] = entries
        try JSONSerialization.data(withJSONObject: saved).write(to: path)
        let recovered = DiagnosticJournal(directory: root)
        let text = String(decoding: try await recovered.export(), as: UTF8.self)
        #expect(!text.contains("private-secret"))
    }
    @Test("capture enabled during a job records its stage and tools")
    func midJobCapture() async throws {
        let journal = DiagnosticJournal(); let lease = UUID().uuidString; let now = Date()
        await journal.assignment(leaseId: lease, jobId: 42)
        await journal.stage(leaseId: lease, stage: "Encoding", seconds: 1, status: 200)
        await journal.apply(DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: nil))
        await journal.stage(leaseId: lease, stage: "Encoding", seconds: 2, status: 200)
        let batch = try #require(await journal.pending())
        #expect(batch.events.contains { $0.reasonCode == "Worker.StageChanged" })
        #expect(batch.events.contains { $0.reasonCode == "Worker.ToolsIdentified" })
    }
    @Test("rotation explicitly reports unmirrored records lost to local bounds")
    func rotation() async throws {
        let journal = DiagnosticJournal(); let now = Date(); let lease = UUID().uuidString
        await journal.apply(DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: nil))
        await journal.assignment(leaseId: lease, jobId: 42)
        for _ in 0..<3000 { await journal.record(leaseId: lease, reason: "Worker.RequestFailed", httpStatus: 503) }
        let batch = try #require(await journal.pending()); #expect(batch.events.count == 100); #expect(batch.droppedEvents > 0)
        #expect(try await journal.export().count <= 1024 * 1024)
    }
    @Test("upload offset and chunk acknowledgements retain the server byte counts")
    func observedTransferOffsets() async throws {
        let journal = DiagnosticJournal(); let lease = UUID().uuidString; let now = Date()
        await journal.apply(DiagnosticConsent(sessionId: UUID().uuidString, serverTimeUtc: now, expiresAt: now.addingTimeInterval(3600), scopedJobId: nil))
        await journal.assignment(leaseId: lease, jobId: 42)
        let transport = StubTransport(json: ["bytes": 7])
        let client = SidecarClient(transport: transport, diagnostics: journal)
        _ = try await client.uploadOffset(serverAddress: "http://example.test", credential: "private-credential", leaseId: lease)
        transport.body = try JSONSerialization.data(withJSONObject: ["bytes": 11])
        _ = try await client.uploadChunk(serverAddress: "http://example.test", credential: "private-credential", leaseId: lease, offset: 7, chunk: Data([1, 2, 3, 4]))
        let batch = try #require(await journal.pending())
        #expect(batch.events.contains { $0.reasonCode == "Worker.TransferOffset" && $0.offsetBytes == 7 })
        #expect(batch.events.contains { $0.reasonCode == "Worker.TransferAcknowledged" && $0.offsetBytes == 11 })
    }

}
