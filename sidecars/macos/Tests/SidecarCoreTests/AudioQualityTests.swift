import Foundation
import Testing
@testable import SidecarCore

@Suite("Audio quality reporting")
struct AudioQualityTests {

    @Test("Matroska duration tags use the track end and tiny seeks stay in fixed point")
    func trackEndsAndSeeks() {
        let probe = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"-0.021","channels":2,"sample_rate":"48000","tags":{"DURATION":"00:00:11.520000000"}}],"format":{"duration":"12","start_time":"-0.021"}}"#
        #expect(abs((AudioQualityAssessment.profile(probe, audioIndex: 0)?.durationSeconds ?? 0) - 11.541) < 1e-8)
        #expect(AudioQualityAssessment.secondsText(0.0000111) == "0.0000111")
        let windows = AudioQualityAssessment.plan(600, soundtrack: true)
        #expect(windows.count == 3)
        #expect(abs(windows.last!.startSeconds + 30 - 599.9) < 1e-8)
    }

    @Test("mkvmerge elapsed duration and ffmpeg end timestamp agree for delayed tracks")
    func writerDurations() {
        for (writer, end) in [("libebml", "00:01:40.000000000"), ("Lavf62", "00:01:40.500000000")] {
            let json = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0.5","channels":2,"sample_rate":"48000","tags":{"DURATION":"\#(end)","_STATISTICS_WRITING_APP":"mkvmerge v95"}}],"format":{"start_time":"0","duration":"101","tags":{"encoder":"\#(writer)"}}}"#
            let probe = writer.hasPrefix("Lavf") ? json.replacingOccurrences(of: ",\"_STATISTICS_WRITING_APP\":\"mkvmerge v95\"", with: "") : json
            #expect(AudioQualityAssessment.profile(probe, audioIndex: 0)?.durationSeconds == 100)
        }
    }

    @Test("conflicting Matroska writers do not guess delayed track duration")
    func conflictingWriters() {
        for (statistics, end) in [("mkvmerge v95", "00:01:40.500000000"), ("mkvpropedit v95", "00:01:40.000000000")] {
            let json = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0.5","channels":2,"sample_rate":"48000","tags":{"DURATION":"\#(end)","_STATISTICS_WRITING_APP":"\#(statistics)"}}],"format":{"start_time":"0","duration":"101","tags":{"encoder":"Lavf62"}}}"#
            #expect(AudioQualityAssessment.profile(json, audioIndex: 0) == nil)
        }
    }

    @Test("older audio profiles decode with a zero container lead")
    func oldProfile() throws {
        let json = #"{"durationSeconds":3,"channels":2,"sampleRate":48000,"channelLayout":"stereo"}"#
        #expect(try JSONDecoder().decode(AudioQualityProfile.self, from: Data(json.utf8)).containerLeadSeconds == 0)
    }
    @Test("sample windows match the shared worker contract")
    func windows() {
        #expect(AudioQualityAssessment.plan(0.5).isEmpty)
        #expect(AudioQualityAssessment.plan(.nan).isEmpty)
        #expect(AudioQualityAssessment.plan(60).map(\.durationSeconds) == [30, 30])
        #expect(AudioQualityAssessment.plan(120).map(\.startSeconds) == [0, 45, 90])
        #expect(AudioQualityAssessment.plan(19.173878).count == 1)
    }
    @Test("legacy contracts keep reporting off")
    func legacyContract() throws {
        let contract = try JSONDecoder().decode(FullVerificationContract.self,
            from: Data(#"{"version":2,"id":"test","measureAudio":false}"#.utf8))
        #expect(contract.measureAudioQuality != true)
    }
    @Test("unsupported tracks and channel changes are refused")
    func profiles() {
        let mono = #"{"streams":[{"codec_type":"audio","channels":1,"sample_rate":"48000","duration":"2","channel_layout":"mono"}],"format":{"duration":"2"}}"#
        #expect(AudioQualityAssessment.profile(mono)?.channels == 1)
        #expect(AudioQualityAssessment.profile(mono.replacingOccurrences(of: "\"channels\":1", with: "\"channels\":6")) == nil)
        #expect(AudioQualityAssessment.profile(mono.replacingOccurrences(of: "\"mono\"", with: "\"stereo\"")) == nil)
    }
    @Test("native evidence requires the pinned model and complete channel counts")
    func nativeEvidence() {
        let json = #"{"schema":1,"metric":"zimtohrli","revision":"f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3","sampleRate":48000,"channels":2,"fullScaleSineDb":78.3,"frames":48000,"distances":[0.01,0.02]}"#
        #expect(AudioQualityAssessment.parse(json, channels: 2, seconds: 1)?.channelDistances == [0.01, 0.02])
        #expect(AudioQualityAssessment.parse(json, channels: 1, seconds: 1) == nil)
        #expect(AudioQualityAssessment.parse(json, channels: 2, seconds: 2) == nil)
        #expect(AudioQualityAssessment.parse(json.replacingOccurrences(of: "zimtohrli", with: "other"), channels: 2, seconds: 1) == nil)
    }
}

@Suite("Live audio quality acceptance")
struct LiveAudioQualityTests {
    @Test("real pinned tools score licensed audio and remove every owned scratch file")
    func liveAssessment() async throws {
        guard let manifest = ProcessInfo.processInfo.environment["OPTIMISARR_AUDIO_QUALITY_LIVE"] else { return }
        let data = try Data(contentsOf: URL(fileURLWithPath: manifest))
        struct Fixture: Decodable { let ffmpeg: String, ffprobe: String, metric: String, source: String, candidate: String, scratch: String, report: String }
        let fixture = try JSONDecoder().decode(Fixture.self, from: data)
        let source = URL(fileURLWithPath: fixture.source), candidate = URL(fileURLWithPath: fixture.candidate)
        let root = URL(fileURLWithPath: fixture.scratch)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        func probe(_ file: URL, _ name: String) async throws -> String {
            let output = root.appendingPathComponent(name + ".json")
            defer { try? FileManager.default.removeItem(at: output) }
            let process = try await ProcessTranscodeRunner().run(URL(fileURLWithPath: fixture.ffprobe),
                ["-v", "error", "-show_format", "-show_streams", "-of", "json", "-o", output.path, file.path]) { _ in }
            #expect(process.exitCode == 0)
            return try String(contentsOf: output, encoding: .utf8)
        }
        let result = try await AudioQualityAssessment().measure(ffmpeg: URL(fileURLWithPath: fixture.ffmpeg),
            ffprobe: URL(fileURLWithPath: fixture.ffprobe), metric: URL(fileURLWithPath: fixture.metric),
            source: source, candidate: candidate, sourceProbe: try await probe(source, "source"),
            candidateProbe: try await probe(candidate, "candidate"), scratch: root)
        #expect(result.assessment.measured, "\(result.assessment.error ?? "No measurement")")
        #expect(result.assessment.windows.count > 0)
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path).isEmpty)
        try JSONEncoder().encode(result).write(to: URL(fileURLWithPath: fixture.report), options: .withoutOverwriting)
    }
}
