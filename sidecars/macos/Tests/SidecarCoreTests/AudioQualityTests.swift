import Foundation
import Testing
@testable import SidecarCore

@Suite("Audio quality reporting")
struct AudioQualityTests {
    @Test("near-start soundtrack windows trim on the picture timeline without input seeking")
    func soundtrackPreroll() {
        for (start, lead, trim) in [(0.0, 0.007, "0.007"), (0.04, 0.007, "0.047"),
                                    (0.0, 0.021, "0.021"), (0.0, 0.0, "0")] {
            for file in ["reference-opus.mkv", "candidate-opus.mkv"] {
                let args = AudioQualityAssessment.decodeArguments(input: URL(fileURLWithPath: "/" + file),
                    output: URL(fileURLWithPath: "/decoded.raw"), window: .init(startSeconds: start, durationSeconds: 30),
                    audioIndex: 0, lead: lead, soundtrack: true)
                #expect(!args.contains("-ss"))
                #expect(args[args.firstIndex(of: "-af")! + 1] == "aresample=48000,atrim=start=\(trim),asetpts=PTS-STARTPTS,atrim=end_sample=1440000")
                #expect(args[args.firstIndex(of: "-t")! + 1] == "30.1")
                #expect(!args.joined(separator: ",").contains("apad"))
            }
        }
    }

    @Test("late-starting soundtracks count samples after the start trim without a timestamp end")
    func lateSoundtrackSamples() throws {
        for start in ["0.009", "0.022"] {
            let probe = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"\#(start)","duration":"8","channels":2,"sample_rate":"48000"}],"format":{"start_time":"0"}}"#
            let profile = try #require(AudioQualityAssessment.profile(probe, audioIndex: 0))
            #expect(profile.containerLeadSeconds == 0)
            let window = try #require(AudioQualityAssessment.plan(profile.durationSeconds, soundtrack: true).first)
            for file in ["reference-late.mkv", "candidate-late.mkv"] {
                let args = AudioQualityAssessment.decodeArguments(input: URL(fileURLWithPath: "/" + file),
                    output: URL(fileURLWithPath: "/decoded.raw"), window: window,
                    audioIndex: 0, lead: profile.containerLeadSeconds, soundtrack: true)
                #expect(!args.contains("-ss"))
                #expect(args[args.firstIndex(of: "-af")! + 1] == "aresample=48000,atrim=start=0,asetpts=PTS-STARTPTS,atrim=end_sample=379200")
            }
        }
    }

    @Test("later soundtrack seeks retain the demuxer origin and picture lead")
    func soundtrackSeekOrigin() {
        let args = AudioQualityAssessment.decodeArguments(input: URL(fileURLWithPath: "/source.mkv"),
            output: URL(fileURLWithPath: "/decoded.raw"), window: .init(startSeconds: 22.5, durationSeconds: 3),
            audioIndex: 0, lead: 0.021, soundtrack: true)
        let seek = args.firstIndex(of: "-ss")!
        #expect(seek < args.firstIndex(of: "-i")!)
        #expect(args[seek + 1] == "22.521")
        #expect(args[args.firstIndex(of: "-af")! + 1] == "aresample=48000,atrim=end_sample=144000")
    }

    @Test("FLAC statistics use the exact sample count and still refuse truncation")
    func flacStatistics() throws {
        for tag in ["DURATION", "DURATION-eng"] {
            let source = flacStatisticsProbe(tag)
            let reference = try #require(AudioQualityAssessment.profile(source, audioIndex: 0))
            #expect(abs(reference.durationSeconds - 4321280 / 48000.0) < 1e-8)
            func candidate(_ duration: String) -> String {
                #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0","channels":2,"sample_rate":"48000","duration":"\#(duration)","tags":{"language":"eng"}}],"format":{"start_time":"0"}}"#
            }
            let plan = try SoundtrackQualityAssessment.plan(sourceProbe: source, candidateProbe: candidate("90.048"), request: .init(removedSourceAudioIndexes: []))
            #expect(plan.count == 1)
            #expect(throws: (any Error).self) {
                try SoundtrackQualityAssessment.plan(sourceProbe: source, candidateProbe: candidate("89.915"), request: .init(removedSourceAudioIndexes: []))
            }
        }
    }

    @Test("targeted STREAMINFO ignores font attachments and retains exact profiles")
    func targetedFlacStreamInfo() throws {
        let original = flacStatisticsProbe("DURATION-eng")
        var root = try JSONSerialization.jsonObject(with: Data(original.utf8)) as! [String: Any]
        var streams = root["streams"] as! [[String: Any]]
        streams[1]["index"] = 2
        let selected = String(data: try JSONSerialization.data(withJSONObject: ["streams": [streams[1]]]), encoding: .utf8)!
        streams[1].removeValue(forKey: "extradata")
        streams.insert(["codec_type": "attachment", "codec_name": "ttf", "index": 1, "extradata_size": 2 * 1024 * 1024], at: 1)
        root["streams"] = streams
        let metadata = String(data: try JSONSerialization.data(withJSONObject: root), encoding: .utf8)!
        #expect(FlacStreamInfoProbe.requiredAudioIndexes(metadata) == [0])
        #expect(FlacStreamInfoProbe.requiredAudioIndexes(metadata, writtenByFfmpeg: true).isEmpty)
        let enriched = FlacStreamInfoProbe.withStreamInfo(metadata, audioIndex: 0, trackProbe: selected)
        #expect(enriched.utf8.count < 4096)
        let actual = try #require(AudioQualityAssessment.profile(enriched, audioIndex: 0))
        let expected = try #require(AudioQualityAssessment.profile(original, audioIndex: 0))
        let encoder = JSONEncoder()
        encoder.outputFormatting = .sortedKeys
        #expect(try encoder.encode(actual) == encoder.encode(expected))
        for invalid in [String(repeating: "x", count: 16385), original, selected.replacingOccurrences(of: "flac", with: "ttf")] {
            #expect(AudioQualityAssessment.profile(FlacStreamInfoProbe.withStreamInfo(metadata, audioIndex: 0, trackProbe: invalid), audioIndex: 0) == nil)
        }
        let args = FlacStreamInfoProbe.arguments(file: URL(fileURLWithPath: "/movie.mkv"), audioIndex: 0)
        #expect(args[args.firstIndex(of: "-select_streams")! + 1] == "a:0")
        #expect(args.contains("-show_data"))
    }

    @Test("FLAC counts cannot override stream durations or conflicting statistics")
    func conflictingFlacStatistics() {
        let json = flacStatisticsProbe("DURATION-eng")
        #expect(AudioQualityAssessment.profile(json.replacingOccurrences(of: #""sample_rate""#, with: #""duration":"91","sample_rate""#), audioIndex: 0)?.durationSeconds == 91)
        for invalid in [json.replacingOccurrences(of: "29.941", with: "28.941"),
                        json.replacingOccurrences(of: "02f0 0041", with: "02f0 0000").replacingOccurrences(of: "f000 0000", with: "0000 0000"),
                        json.replacingOccurrences(of: "0bb8 02f0", with: "0ac4 42f0"),
                        json.replacingOccurrences(of: "1000 1000", with: "1000 0000"),
                        json.replacingOccurrences(of: "extradata", with: "unavailable")] {
            #expect(AudioQualityAssessment.profile(invalid, audioIndex: 0) == nil)
        }
        #expect(AudioQualityAssessment.profile(json, audioIndex: 0, writtenByFfmpeg: true) == nil)
        #expect(AudioQualityAssessment.profile(flacStatisticsProbe("DURATION").replacingOccurrences(of: "29.941", with: "30.100"), audioIndex: 0, writtenByFfmpeg: true)?.durationSeconds == 90.1)
        #expect(abs((AudioQualityAssessment.profile(json.replacingOccurrences(of: "29.941", with: "30.000"), audioIndex: 0)?.durationSeconds ?? 0) - 4321280 / 48000.0) < 1e-8)
    }

    private func flacStatisticsProbe(_ tag: String) -> String {
        #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","codec_name":"flac","start_time":"0","channels":2,"sample_rate":"48000","extradata":"\n00000000: 1000 1000 0000 0000 0000 0bb8 02f0 0041  ...............A\n00000010: f000 0000 0000 0000 0000 0000 0000 0000  ................\n00000020: 0000                                     ..\n","tags":{"language":"eng","\#(tag)":"00:01:29.941000000","_STATISTICS_WRITING_APP-eng":"mkvpropedit v95"}}],"format":{"start_time":"0","duration":"91","tags":{"encoder":"Lavf62"}}}"#
    }

    @Test func audioWindowSeeksPreserveDecoderContextAndTrimExactSamples() {
        for start in [0.0, 1.25, 22.5, 86370] {
            let args = AudioQualityAssessment.decodeArguments(input: URL(fileURLWithPath: "/source.m4a"),
                output: URL(fileURLWithPath: "/decoded.raw"), window: .init(startSeconds: start, durationSeconds: 3),
                audioIndex: 0, lead: 0, soundtrack: false)
            let input = args.firstIndex(of: "-i")!
            let seeks = args.indices.filter { args[$0] == "-ss" }
            #expect(seeks.count == (start == 0 ? 0 : start > 10 ? 2 : 1))
            if start > 10 { #expect(seeks[0] < input); #expect(args[seeks[0] + 1] == AudioQualityAssessment.secondsText(start - 10)) }
            if start > 0 { #expect(seeks.last! > input); #expect(args[seeks.last! + 1] == AudioQualityAssessment.secondsText(min(start, 10))) }
            #expect(!args.contains("apad"))
        }
    }

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

    @Test("mkvmerge language-suffixed statistics give the track length")
    func suffixedStatistics() {
        let json = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0","channels":2,"sample_rate":"48000","tags":{"language":"ita","DURATION-eng":"00:42:48.640000000","_STATISTICS_WRITING_APP-eng":"mkvmerge v51"}}],"format":{"start_time":"0","duration":"2568.661","tags":{"encoder":"libebml v1.4.1"}}}"#
        #expect(abs((AudioQualityAssessment.profile(json, audioIndex: 0)?.durationSeconds ?? 0) - 2568.64) < 1e-6)
        let propedited = json.replacingOccurrences(of: "libebml v1.4.1", with: "Lavf58.29.100").replacingOccurrences(of: "mkvmerge v51", with: "mkvpropedit v28.2.0")
        #expect(abs((AudioQualityAssessment.profile(propedited, audioIndex: 0)?.durationSeconds ?? 0) - 2568.64) < 1e-6)
        #expect(AudioQualityAssessment.profile(propedited.replacingOccurrences(of: #""start_time":"0","channels""#, with: #""start_time":"0.5","channels""#), audioIndex: 0) == nil)
        #expect(AudioQualityAssessment.profile(propedited, audioIndex: 0, writtenByFfmpeg: true) == nil)
    }

    @Test("an output this FFmpeg wrote reads its own end tag despite copied statistics")
    func ownOutputDuration() {
        let json = #"{"streams":[{"codec_type":"video","start_time":"0"},{"codec_type":"audio","start_time":"0.019","channels":2,"sample_rate":"48000","tags":{"DURATION":"00:11:48.199000000","DURATION-eng":"00:11:48.139000000","_STATISTICS_WRITING_APP":"mkvmerge v9.6.0"}}],"format":{"start_time":"0","duration":"708.285","tags":{"encoder":"Lavf62.3.103"}}}"#
        #expect(AudioQualityAssessment.profile(json, audioIndex: 0) == nil)
        #expect(abs((AudioQualityAssessment.profile(json, audioIndex: 0, writtenByFfmpeg: true)?.durationSeconds ?? 0) - 708.18) < 1e-6)
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
