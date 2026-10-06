import Foundation
import Testing
@testable import SidecarCore

@Suite("Soundtrack quality")
struct SoundtrackQualityTests {
    @Test("multiple tracks hash once at each boundary and changes discard every score", arguments: [false, true])
    func sharedHashes(change: Bool) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let counter = SoundtrackHashCounter(change: change)
        let report = try await SoundtrackQualityAssessment(runner: PreparedSoundtrackRunner(), hash: counter.hash).measure(
            ffmpeg: root.appendingPathComponent("ffmpeg"), ffprobe: root.appendingPathComponent("ffprobe"),
            metric: root.appendingPathComponent("metric"), source: root.appendingPathComponent("source"),
            candidate: root.appendingPathComponent("candidate"), sourceProbe: probe(["eng", "fra"]),
            candidateProbe: probe(["eng", "fra"]), request: .init(removedSourceAudioIndexes: []), healthy: true, scratch: root)
        #expect(counter.counts.values.allSatisfy { $0 == 2 })
        #expect(counter.counts.count == 5)
        if change {
            #expect(report.tracks.isEmpty)
            #expect(report.unavailableReason?.contains("changed") == true)
        } else {
            #expect(report.tracks.count == 2)
            #expect(report.tracks.allSatisfy { $0.report.evidence?.assessment.measured == true })
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
    }
    func probe(_ languages: [String], channels: Int = 2) -> String {
        let tracks = languages.map { language in
            #"{"codec_type":"audio","channels":\#(channels),"sample_rate":"48000","duration":"3","start_time":"0","tags":{"language":"\#(language)","title":"Main"}}"#
        }.joined(separator: ",")
        return #"{"streams":[{"codec_type":"video","start_time":"0"},\#(tracks)],"format":{"duration":"3","start_time":"0"}}"#
    }

    @Test("selected tracks retain source and output positions after removal")
    func mapping() throws {
        let pairs = try SoundtrackQualityAssessment.plan(sourceProbe: probe(["fra", "eng"]),
            candidateProbe: probe(["eng"]), request: .init(removedSourceAudioIndexes: [0]))
        #expect(pairs.count == 1)
        #expect(pairs[0].sourceAudioIndex == 1)
        #expect(pairs[0].candidateAudioIndex == 0)
        #expect(pairs[0].language == "eng")
        #expect(AudioQualityAssessment.profile(probe(["eng"])) == nil)
        #expect(AudioQualityAssessment.profile(probe(["eng"]), audioIndex: 0)?.channels == 2)
    }

    @Test("missing tracks surround and malformed removal contracts fail")
    func unsupported() {
        #expect(throws: (any Error).self) { try SoundtrackQualityAssessment.plan(sourceProbe: probe(["eng", "fra"]),
            candidateProbe: probe(["eng"]), request: .init(removedSourceAudioIndexes: [])) }
        #expect(throws: (any Error).self) { try SoundtrackQualityAssessment.plan(sourceProbe: probe(["eng"], channels: 6),
            candidateProbe: probe(["eng"]), request: .init(removedSourceAudioIndexes: [])) }
        #expect(throws: (any Error).self) { try SoundtrackQualityAssessment.plan(sourceProbe: probe(["eng"]),
            candidateProbe: probe(["eng"]), request: .init(removedSourceAudioIndexes: [2])) }
    }
}

private final class SoundtrackHashCounter: @unchecked Sendable {
    private let lock = NSLock()
    private let change: Bool
    private var values: [String: Int] = [:]
    init(change: Bool) { self.change = change }
    var counts: [String: Int] { lock.withLock { values } }
    func hash(_ url: URL) -> String {
        lock.withLock {
            values[url.lastPathComponent, default: 0] += 1
            return String(repeating: change && url.lastPathComponent == "candidate" && values["candidate"] == 2 ? "b" : "a", count: 64)
        }
    }
}
private struct PreparedSoundtrackRunner: TranscodeRunner {
    func run(_ executable: URL, _ arguments: [String], progress: @escaping @Sendable (Double) -> Void) async throws -> (exitCode: Int32, stderr: String) {
        if executable.lastPathComponent == "ffmpeg" {
            try Data(repeating: 0, count: 139200 * 8).write(to: URL(fileURLWithPath: arguments.last!))
        } else {
            let report = #"{"schema":1,"metric":"zimtohrli","revision":"f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3","sampleRate":48000,"channels":2,"fullScaleSineDb":78.3,"frames":139200,"distances":[0.01,0.02]}"#
            try Data(report.utf8).write(to: URL(fileURLWithPath: arguments.last!))
        }
        return (0, "")
    }
}

@Suite("Live soundtrack contract acceptance")
struct LiveSoundtrackQualityTests {
    @Test("native full verification measures every retained track and cleans scratch")
    func liveContract() async throws {
        guard let manifest = ProcessInfo.processInfo.environment["OPTIMISARR_SOUNDTRACK_LIVE"] else { return }
        struct Fixture: Decodable { let ffmpeg: String, ffprobe: String, source: String, candidate: String, scratch: String, report: String, sourceHash: String, candidateHash: String, id: String, removed: [Int] }
        let fixture = try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: URL(fileURLWithPath: manifest)))
        let scratch = URL(fileURLWithPath: fixture.scratch)
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        let contract = FullVerificationContract(version: 3, id: fixture.id, measureAudio: false,
            soundtrackQuality: .init(removedSourceAudioIndexes: fixture.removed))
        let result = try await FullVerification().measure(contract: contract,
            ffmpeg: URL(fileURLWithPath: fixture.ffmpeg), ffprobe: URL(fileURLWithPath: fixture.ffprobe),
            source: URL(fileURLWithPath: fixture.source), candidate: URL(fileURLWithPath: fixture.candidate),
            scratch: scratch, sourceHash: fixture.sourceHash, candidateHash: fixture.candidateHash)
        #expect(result.error == nil, "\(result.error ?? "")")
        #expect(result.soundtrackQuality?.unavailableReason == nil)
        #expect(result.soundtrackQuality?.tracks.count == (fixture.removed.isEmpty ? 2 : 1))
        for track in result.soundtrackQuality?.tracks ?? [] {
            #expect(track.report.evidence?.assessment.measured == true, "\(track.report.unavailableReason ?? "No evidence")")
            #expect(track.report.evidence?.preparation == "audio-f32le-48k-video-timeline-v1")
        }
        #expect(try FileManager.default.contentsOfDirectory(atPath: scratch.path).isEmpty)
        try JSONEncoder().encode(result).write(to: URL(fileURLWithPath: fixture.report), options: .withoutOverwriting)
    }
}
