import Foundation

public struct SoundtrackQualityRequest: Codable, Sendable, Equatable {
    public let removedSourceAudioIndexes: [Int]
}
public struct SoundtrackQualityPair: Codable, Sendable {
    let sourceAudioIndex: Int
    let candidateAudioIndex: Int
    let language: String?
    let title: String?
    let reference: AudioQualityProfile
    let candidate: AudioQualityProfile
}
public struct SoundtrackAudioReport: Codable, Sendable {
    var measurementLocation = "Worker"
    let evidence: RemoteAudioQualityEvidence?
    let unavailableReason: String?
}
public struct SoundtrackQualityTrack: Codable, Sendable {
    let track: SoundtrackQualityPair
    let report: SoundtrackAudioReport
}
public struct SoundtrackQualityReport: Codable, Sendable {
    let tracks: [SoundtrackQualityTrack]
    let unavailableReason: String?
}

/// Tracks stay ordered by the frozen removal contract. The server validates identities against both full probes.
public struct SoundtrackQualityAssessment: Sendable {
    let runner: any TranscodeRunner
    public init(runner: any TranscodeRunner = ProcessTranscodeRunner()) { self.runner = runner }

    static func plan(sourceProbe: String, candidateProbe: String, request: SoundtrackQualityRequest) throws -> [SoundtrackQualityPair] {
        func tracks(_ json: String) throws -> [[String: Any]] {
            guard json.utf8.count <= 1024 * 1024, let data = json.data(using: .utf8),
                  let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let streams = object["streams"] as? [[String: Any]] else { throw Failure("Soundtrack probes are incomplete.") }
            return streams.filter { $0["codec_type"] as? String == "audio" }
        }
        let source = try tracks(sourceProbe), output = try tracks(candidateProbe)
        let removed = request.removedSourceAudioIndexes
        guard Set(removed).count == removed.count, removed.allSatisfy({ source.indices.contains($0) }) else {
            throw Failure("The retained soundtrack assignment is invalid.")
        }
        let retained = source.indices.filter { !removed.contains($0) }
        guard (1...8).contains(retained.count), retained.count == output.count else {
            throw Failure("Assessment requires every retained soundtrack, with at most eight tracks.")
        }
        return try retained.enumerated().map { outputIndex, sourceIndex in
            guard let reference = AudioQualityAssessment.profile(sourceProbe, audioIndex: sourceIndex),
                  let candidate = AudioQualityAssessment.profile(candidateProbe, audioIndex: outputIndex),
                  reference.channels == candidate.channels, reference.channelLayout == candidate.channelLayout,
                  abs(reference.durationSeconds - candidate.durationSeconds) <= 0.1 else {
                throw Failure("Soundtrack \(outputIndex + 1): assessment supports matching known-duration mono/stereo tracks. Surround and surround downmix assessment are not available.")
            }
            let tags = source[sourceIndex]["tags"] as? [String: Any]
            let language = (tags?["language"] as? String)?.trimmingCharacters(in: .whitespaces).lowercased()
            return .init(sourceAudioIndex: sourceIndex, candidateAudioIndex: outputIndex,
                language: language == "und" || language == "" ? nil : language,
                title: tags?["title"] as? String, reference: reference, candidate: candidate)
        }
    }

    public func measure(ffmpeg: URL, ffprobe: URL, metric: URL, source: URL, candidate: URL,
                        sourceProbe: String, candidateProbe: String, request: SoundtrackQualityRequest,
                        healthy: Bool, scratch: URL) async throws -> SoundtrackQualityReport {
        try Task.checkCancellation()
        guard healthy else { return .init(tracks: [], unavailableReason: "Skipped because the candidate failed decode health.") }
        let pairs: [SoundtrackQualityPair]
        do { pairs = try Self.plan(sourceProbe: sourceProbe, candidateProbe: candidateProbe, request: request) }
        catch { return .init(tracks: [], unavailableReason: String(describing: error)) }
        var results: [SoundtrackQualityTrack] = []
        for pair in pairs {
            let evidence = try await AudioQualityAssessment(runner: runner).measure(ffmpeg: ffmpeg, ffprobe: ffprobe,
                metric: metric, source: source, candidate: candidate, sourceProbe: sourceProbe, candidateProbe: candidateProbe,
                scratch: scratch, sourceAudioIndex: pair.sourceAudioIndex, candidateAudioIndex: pair.candidateAudioIndex)
            results.append(.init(track: pair, report: .init(evidence: evidence.assessment.measured ? evidence : nil,
                unavailableReason: evidence.assessment.error)))
        }
        return .init(tracks: results, unavailableReason: nil)
    }
    private struct Failure: Error, CustomStringConvertible { let description: String; init(_ message: String) { description = message } }
}
