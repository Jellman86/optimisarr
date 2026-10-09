import Foundation
import CryptoKit

public struct DiagnosticConsent: Codable, Sendable {
    public let sessionId: String
    public let serverTimeUtc: Date
    public let expiresAt: Date?
    public let scopedJobId: Int?
    public var recording: Bool? = nil
    public var isRecording: Bool { recording ?? true }
}
public struct LocalDiagnosticEvent: Codable, Sendable {
    public let sequence: Int64
    public let occurredAt: Date
    public let leaseId: String
    public let jobId: Int
    public let reasonCode: String
    public let stage: String?
    public let encodedSeconds: Double?
    public let offsetBytes: Int64?
    public let httpStatus: Int?
    public let ffmpegSha256: String?
    public let ffprobeSha256: String?
    public var measurementFfmpegSha256: String? = nil
}
public struct LocalDiagnosticBatch: Codable, Sendable {
    public let schemaVersion: Int
    public let sessionId: String
    public let instanceId: String
    public let events: [LocalDiagnosticEvent]
    public var final: Bool = false
    public var droppedEvents: Int64 = 0
}

/// Local records require renewed server consent. A disconnected capture expires after 90 seconds.
public actor DiagnosticJournal {
    public static let shared = DiagnosticJournal(directory: defaultDirectory)
    public static var defaultDirectory: URL {
        if let path = ProcessInfo.processInfo.environment["OPTIMISARR_DIAGNOSTIC_DIR"] { return URL(fileURLWithPath: path, isDirectory: true) }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OptimisarrSidecar/Diagnostics", isDirectory: true)
    }
    public static let fileName = "diagnostic-events.json"
    private struct Entry: Codable { let sessionId: String; let instanceId: String; let event: LocalDiagnosticEvent; var acknowledged: Bool }
    private struct Export: Codable { let schemaVersion: Int; let collection: String; let maximumEntries: Int; let entries: [Entry]; var droppedBySession: [String: Int64]? = nil }
    private let directory: URL?
    private let instance = UUID().uuidString
    private var consent: DiagnosticConsent?
    private var deadline = Date.distantPast
    private var entries: [Entry] = []
    private var droppedBySession: [String: Int64] = [:]
    private var leases: [String: (job: Int, stage: String?)] = [:]
    private var sequence: Int64 = 0
    private var loaded = false
    private var toolHashes: (String?, String?)?
    private var toolsRecorded: Set<String> = []
    private static let reasons: Set<String> = ["Worker.AssignmentReceived", "Worker.StageChanged", "Worker.LeaseReleased", "Worker.VerificationAcknowledged", "Worker.TransferOffset", "Worker.TransferAcknowledged", "Worker.RequestFailed", "Worker.ToolsIdentified"]
    public init(directory: URL? = nil) { self.directory = directory }
    public func apply(_ value: DiagnosticConsent?, now: Date = Date()) {
        load()
        if let value, value.isRecording, consent?.sessionId != value.sessionId || consent?.isRecording != true {
            for key in Array(leases.keys) { if let lease = leases[key] { leases[key] = (lease.job, nil) } }
            toolsRecorded.removeAll(); toolHashes = nil
        }
        consent = value
        deadline = now.addingTimeInterval(value.map { min(90, max(0, $0.expiresAt?.timeIntervalSince($0.serverTimeUtc) ?? 90)) } ?? 0)
        let count = entries.count; prune(now)
        if count != entries.count { persist() }
    }
    public func assignment(leaseId: String, jobId: Int) {
        if leases.count >= 2048, let key = leases.keys.first { leases.removeValue(forKey: key); toolsRecorded.remove(key) }
        leases[leaseId] = (jobId, nil)
        record(leaseId: leaseId, reason: "Worker.AssignmentReceived")
        recordTools(leaseId: leaseId, jobId: jobId)
    }
    private func recordTools(leaseId: String, jobId: Int) {
        if consent?.isRecording == true && Date() < deadline && (consent?.scopedJobId == nil || consent?.scopedJobId == jobId) && toolsRecorded.insert(leaseId).inserted {
            if toolHashes == nil {
                let tools = Bundle.main.resourceURL
                let env = ProcessInfo.processInfo.environment
                toolHashes = (Self.hash(env["OPTIMISARR_FFMPEG"].map { URL(fileURLWithPath: $0) } ?? tools?.appendingPathComponent("ffmpeg")),
                              Self.hash(env["OPTIMISARR_FFPROBE"].map { URL(fileURLWithPath: $0) } ?? tools?.appendingPathComponent("ffprobe")))
            }
            record(leaseId: leaseId, reason: "Worker.ToolsIdentified", ffmpeg: toolHashes?.0, ffprobe: toolHashes?.1)
        }
    }
    public func stage(leaseId: String, stage: String?, seconds: Double?, status: Int) {
        guard let lease = leases[leaseId] else { return }
        recordTools(leaseId: leaseId, jobId: lease.job)
        if lease.stage == stage && (200..<300).contains(status) { return }
        leases[leaseId] = (lease.job, stage)
        record(leaseId: leaseId, reason: (200..<300).contains(status) ? "Worker.StageChanged" : "Worker.RequestFailed", stage: stage, seconds: seconds, httpStatus: status)
    }
    public func record(leaseId: String, reason: String, stage: String? = nil, seconds: Double? = nil,
                       offset: Int64? = nil, httpStatus: Int? = nil, ffmpeg: String? = nil, ffprobe: String? = nil) {
        let now = Date()
        guard let consent, consent.isRecording, now < deadline, let lease = leases[leaseId], Self.reasons.contains(reason),
              consent.scopedJobId == nil || consent.scopedJobId == lease.job else { return }
        sequence += 1
        let event = LocalDiagnosticEvent(sequence: sequence, occurredAt: now, leaseId: leaseId, jobId: lease.job, reasonCode: reason,
            stage: ["FetchingSource", "Encoding", "Measuring", "Delivering"].contains(stage ?? "") ? stage : nil,
            encodedSeconds: seconds.flatMap { $0.isFinite && $0 >= 0 && $0 <= 1_000_000 ? $0 : nil },
            offsetBytes: offset.flatMap { $0 >= 0 ? $0 : nil }, httpStatus: httpStatus.flatMap { (100...599).contains($0) ? $0 : nil },
            ffmpegSha256: Self.safeHash(ffmpeg), ffprobeSha256: Self.safeHash(ffprobe), measurementFfmpegSha256: Self.safeHash(ffmpeg))
        entries.append(Entry(sessionId: consent.sessionId, instanceId: instance, event: event, acknowledged: false))
        prune(now); persist()
    }
    public var droppedEvents: Int64 { consent.map { droppedBySession[$0.sessionId, default: 0] } ?? 0 }
    public func pending() -> LocalDiagnosticBatch? {
        guard let consent, (!consent.isRecording || Date() < deadline), let first = entries.first(where: { $0.sessionId == consent.sessionId && !$0.acknowledged }) else { return nil }
        return LocalDiagnosticBatch(schemaVersion: 1, sessionId: consent.sessionId, instanceId: first.instanceId,
            events: Array(entries.filter { $0.sessionId == consent.sessionId && $0.instanceId == first.instanceId && !$0.acknowledged }.prefix(100).map(\.event)), droppedEvents: droppedEvents)
    }
    public func acknowledge(_ batch: LocalDiagnosticBatch, through: Int64) {
        for i in entries.indices where entries[i].sessionId == batch.sessionId && entries[i].instanceId == batch.instanceId && entries[i].event.sequence <= through { entries[i].acknowledged = true }
        persist()
    }
    public func export() throws -> Data {
        load(); let count = entries.count; prune(Date())
        if count != entries.count { persist() }
        return try serialize()
    }
    private func serialize() throws -> Data {
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        return try encoder.encode(Export(schemaVersion: 1, collection: "LocalSidecar", maximumEntries: 2048, entries: entries, droppedBySession: droppedBySession))
    }
    private func prune(_ now: Date) {
        for entry in entries where entry.event.occurredAt < now.addingTimeInterval(-7 * 86400) { countDropped(entry) }
        entries.removeAll { $0.event.occurredAt < now.addingTimeInterval(-7 * 86400) }
        while entries.count > 2048 { countDropped(entries.removeFirst()) }
    }
    private func countDropped(_ entry: Entry) {
        guard !entry.acknowledged else { return }
        if droppedBySession.count >= 20, droppedBySession[entry.sessionId] == nil, let key = droppedBySession.keys.first { droppedBySession.removeValue(forKey: key) }
        droppedBySession[entry.sessionId] = min(1_000_000_000, droppedBySession[entry.sessionId, default: 0] + 1)
    }
    private func load() {
        guard !loaded else { return }; loaded = true
        guard let directory else { return }
        let path = directory.appendingPathComponent(Self.fileName)
        guard let attributes = try? path.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]) else { return }
        guard (attributes.fileSize ?? Int.max) <= 1024 * 1024,
              (attributes.contentModificationDate ?? .distantPast) >= Date().addingTimeInterval(-7 * 86400) else {
            try? FileManager.default.removeItem(at: path); return
        }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        if let bytes = try? Data(contentsOf: path), let saved = try? decoder.decode(Export.self, from: bytes), saved.schemaVersion == 1 {
            entries = Array(saved.entries.compactMap { entry -> Entry? in
                let e = entry.event
                guard Self.reasons.contains(e.reasonCode), UUID(uuidString: entry.sessionId) != nil,
                      UUID(uuidString: entry.instanceId) != nil, UUID(uuidString: e.leaseId) != nil,
                      e.sequence > 0, e.jobId > 0 else { return nil }
                let event = LocalDiagnosticEvent(sequence: e.sequence, occurredAt: e.occurredAt, leaseId: e.leaseId,
                    jobId: e.jobId, reasonCode: e.reasonCode,
                    stage: ["FetchingSource", "Encoding", "Measuring", "Delivering"].contains(e.stage ?? "") ? e.stage : nil,
                    encodedSeconds: e.encodedSeconds.flatMap { $0.isFinite && (0...1_000_000).contains($0) ? $0 : nil },
                    offsetBytes: e.offsetBytes.flatMap { $0 >= 0 ? $0 : nil },
                    httpStatus: e.httpStatus.flatMap { (100...599).contains($0) ? $0 : nil },
                    ffmpegSha256: Self.safeHash(e.ffmpegSha256), ffprobeSha256: Self.safeHash(e.ffprobeSha256), measurementFfmpegSha256: Self.safeHash(e.measurementFfmpegSha256))
                return Entry(sessionId: entry.sessionId, instanceId: entry.instanceId, event: event, acknowledged: entry.acknowledged)
            }.suffix(2048))
            for (key, value) in saved.droppedBySession ?? [:] where UUID(uuidString: key) != nil && (0...1_000_000_000).contains(value) {
                if droppedBySession.count < 20 { droppedBySession[key] = value }
            }
        }
    }
    private func persist() {
        guard let directory else { return }
        do {
            let file = directory.appendingPathComponent(Self.fileName)
            if entries.isEmpty { if FileManager.default.fileExists(atPath: file.path) { try FileManager.default.removeItem(at: file) }; return }
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            var data = try serialize()
            while data.count > 1024 * 1024 && !entries.isEmpty { countDropped(entries.removeFirst()); data = try serialize() }
            try data.write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
        } catch { }
    }
    private static func safeHash(_ value: String?) -> String? {
        guard let value, value.utf8.count == 64, value.utf8.allSatisfy({ (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }) else { return nil }
        return value.lowercased()
    }
    private static func hash(_ path: URL?) -> String? {
        guard let path, let stream = try? FileHandle(forReadingFrom: path) else { return nil }
        defer { try? stream.close() }
        var hash = SHA256()
        do {
            while let data = try stream.read(upToCount: 1024 * 1024), !data.isEmpty { hash.update(data: data) }
            return hash.finalize().map { String(format: "%02x", $0) }.joined()
        } catch { return nil }
    }
}
