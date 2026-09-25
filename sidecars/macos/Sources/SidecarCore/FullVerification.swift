import Foundation

public struct FullVerificationContract: Codable, Sendable, Equatable {
    public let version: Int
    public let id: String
    public let measureAudio: Bool
}

public struct VerificationDecode: Codable, Sendable, Equatable {
    public let healthy: Bool
    public let error: String?
    public let errorCount: Int
}

public struct VerificationTimestamps: Codable, Sendable, Equatable {
    public let measured: Bool
    public let nonMonotonicCount: Int
    public let firstRegressionDetail: String?
    public let lastPresentationSeconds: Double?
}

public struct VerificationLoudness: Codable, Sendable, Equatable {
    public let measured: Bool
    public let integratedLufs: Double?
    public let truePeakDbtp: Double?
    public let error: String?
}

public struct FullVerificationEvidence: Codable, Sendable {
    public let contractId: String
    public let sourceSha256: String
    public let candidateSha256: String
    public var sourceProbe: String?
    public var candidateProbe: String?
    public var decode: VerificationDecode?
    public var sourceVideo: VerificationTimestamps?
    public var candidateVideo: VerificationTimestamps?
    public var sourceAudio: VerificationTimestamps?
    public var sourceLoudness: VerificationLoudness?
    public var candidateLoudness: VerificationLoudness?
    public var error: String?
}

/// Reduces arbitrarily long packet streams without returning them to the server or retaining them in RAM.
struct VerificationTimestampAccumulator {
    var count = 0
    var regressions = 0
    var previousDts: Double?
    var lastPresentation: Double?
    var firstRegression: String?

    mutating func append(_ line: String) {
        let fields = line.split(separator: ",", omittingEmptySubsequences: false)
        func number(_ index: Int) -> Double? {
            guard fields.indices.contains(index), let value = Double(fields[index].trimmingCharacters(in: .whitespaces)), value.isFinite else { return nil }
            return value
        }
        let pts = number(0), dts = number(1)
        if pts != nil || dts != nil { count += 1 }
        if let pts {
            let endpoint = pts + max(0, number(2) ?? 0)
            lastPresentation = max(lastPresentation ?? endpoint, endpoint)
        }
        if let dts {
            if let previousDts, dts < previousDts {
                regressions += 1
                if firstRegression == nil { firstRegression = "decode timestamp went from \(previousDts)s back to \(dts)s" }
            }
            previousDts = dts
        }
    }

    var result: VerificationTimestamps {
        VerificationTimestamps(measured: count > 0, nonMonotonicCount: regressions,
            firstRegressionDetail: firstRegression, lastPresentationSeconds: lastPresentation)
    }
}

public struct FullVerification: Sendable {
    // Uppercase V excludes attached artwork, matching the server's primary-video probe.
    static let movingPictureStreamSpecifier = "V:0"
    let runner: any TranscodeRunner
    public init(runner: any TranscodeRunner = ProcessTranscodeRunner()) { self.runner = runner }

    public func measure(contract: FullVerificationContract, ffmpeg: URL, ffprobe: URL?, source: URL,
                        candidate: URL, scratch: URL, sourceHash: String, candidateHash: String) async throws -> FullVerificationEvidence {
        var evidence = FullVerificationEvidence(contractId: contract.id, sourceSha256: sourceHash, candidateSha256: candidateHash)
        do {
            guard contract.version == 1, let ffprobe else { throw Failure("This worker cannot run the requested full verification contract.") }
            evidence.sourceProbe = try await probe(ffprobe, file: source, scratch: scratch, name: "source")
            evidence.candidateProbe = try await probe(ffprobe, file: candidate, scratch: scratch, name: "candidate")
            // The runner retains a bounded diagnostic tail. Stop on decode errors so a later
            // flood of harmless muxer notes cannot evict corruption and produce a false pass.
            let decoded = try await runner.run(ffmpeg, ["-nostdin", "-xerror", "-v", "error", "-i", candidate.path,
                "-map", "0:v?", "-map", "0:a?", "-f", "null", "-"]) { _ in }
            evidence.decode = Self.parseDecode(decoded.stderr, exitCode: decoded.exitCode)
            evidence.sourceVideo = try await timestamps(ffprobe, file: source, stream: Self.movingPictureStreamSpecifier, scratch: scratch, name: "source-video")
            evidence.candidateVideo = try await timestamps(ffprobe, file: candidate, stream: Self.movingPictureStreamSpecifier, scratch: scratch, name: "candidate-video")
            evidence.sourceAudio = try await timestamps(ffprobe, file: source, stream: "a:0", scratch: scratch, name: "source-audio")
            // Strict server verification consumes this evidence without re-reading media. Confirm
            // a short source packet scan here so a transient read is not blamed on the original.
            if Self.needsSourceVideoConfirmation(video: evidence.sourceVideo, audio: evidence.sourceAudio,
                sourceProbe: evidence.sourceProbe) {
                let confirmed = try await timestamps(ffprobe, file: source, stream: Self.movingPictureStreamSpecifier,
                    scratch: scratch, name: "source-video-confirmation")
                if confirmed.measured && confirmed.lastPresentationSeconds != nil { evidence.sourceVideo = confirmed }
            }
            if contract.measureAudio {
                evidence.sourceLoudness = try await loudness(ffmpeg, file: source)
                evidence.candidateLoudness = try await loudness(ffmpeg, file: candidate)
            }
        } catch is CancellationError { throw CancellationError() }
          catch { evidence.error = "Full verification could not complete: \(error)" }
        try Task.checkCancellation()
        return evidence
    }

    private func probe(_ ffprobe: URL, file: URL, scratch: URL, name: String) async throws -> String {
        let output = scratch.appendingPathComponent("verification-\(name).json")
        defer { try? FileManager.default.removeItem(at: output) }
        let result = try await runner.run(ffprobe, ["-v", "error", "-show_format", "-show_streams", "-of", "json", "-o", output.path, file.path]) { _ in }
        guard result.exitCode == 0 else { throw Failure("ffprobe failed: \(result.stderr)") }
        let size = (try FileManager.default.attributesOfItem(atPath: output.path)[.size] as? NSNumber)?.intValue ?? 0
        guard size > 0 && size <= 1024 * 1024 else { throw Failure("ffprobe evidence is empty or exceeds 1 MiB.") }
        return try String(contentsOf: output, encoding: .utf8)
    }

    private func timestamps(_ ffprobe: URL, file: URL, stream: String, scratch: URL, name: String) async throws -> VerificationTimestamps {
        let output = scratch.appendingPathComponent("verification-\(name).csv")
        defer { try? FileManager.default.removeItem(at: output) }
        let result = try await runner.run(ffprobe, ["-v", "error", "-select_streams", stream,
            "-show_entries", "packet=pts_time,dts_time,duration_time", "-of", "csv=p=0", "-o", output.path, file.path]) { _ in }
        guard result.exitCode == 0 else { throw Failure("Timestamp probe failed: \(result.stderr)") }
        let handle = try FileHandle(forReadingFrom: output)
        defer { try? handle.close() }
        var accumulator = VerificationTimestampAccumulator()
        var pending = Data()
        while let chunk = try handle.read(upToCount: 65536), !chunk.isEmpty {
            try Task.checkCancellation()
            pending.append(chunk)
            while let newline = pending.firstIndex(of: 10) {
                accumulator.append(String(decoding: pending[..<newline], as: UTF8.self))
                pending.removeSubrange(...newline)
            }
            guard pending.count <= 65536 else { throw Failure("Oversized timestamp row.") }
        }
        if !pending.isEmpty { accumulator.append(String(decoding: pending, as: UTF8.self)) }
        return accumulator.result
    }

    static func needsSourceVideoConfirmation(video: VerificationTimestamps?, audio: VerificationTimestamps?,
                                             sourceProbe: String?) -> Bool {
        guard let video, video.measured, let pictureEnd = video.lastPresentationSeconds,
              pictureEnd >= 0, pictureEnd.isFinite,
              let audio, audio.measured, let audioEnd = audio.lastPresentationSeconds,
              audioEnd > 0, audioEnd.isFinite else { return false }
        let streams = ((sourceProbe?.data(using: .utf8)).flatMap {
            (try? JSONSerialization.jsonObject(with: $0) as? [String: Any])?["streams"] as? [[String: Any]]
        }) ?? []
        func start(_ kind: String) -> Double {
            let stream = streams.first { stream in
                guard stream["codec_type"] as? String == kind else { return false }
                if kind == "video", let disposition = stream["disposition"] as? [String: Any],
                   (disposition["attached_pic"] as? Int) == 1 { return false }
                return true
            }
            let value = stream?["start_time"]
            return Double(value as? String ?? "") ?? (value as? Double) ?? 0
        }
        let videoSpan = max(0, pictureEnd - start("video"))
        let audioSpan = max(0, audioEnd - start("audio"))
        let shortfall = audioSpan - videoSpan
        return audioSpan > 0 && shortfall > 1 && shortfall / audioSpan > 0.02
    }

    private func loudness(_ ffmpeg: URL, file: URL) async throws -> VerificationLoudness {
        let result = try await runner.run(ffmpeg, ["-nostdin", "-hide_banner", "-nostats", "-v", "info",
            "-i", file.path, "-map", "0:a:0", "-af", "ebur128=peak=true", "-f", "null", "-"]) { _ in }
        return Self.parseLoudness(result.stderr, exitCode: result.exitCode)
    }

    static func parseLoudness(_ text: String, exitCode: Int32) -> VerificationLoudness {
        func last(_ expression: String) -> Double? {
            guard let regex = try? NSRegularExpression(pattern: expression),
                  let match = regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).last,
                  let range = Range(match.range(at: 1), in: text), let number = Double(text[range]), number.isFinite else { return nil }
            return number
        }
        let lufs = last(#"I:\s+(-?[\d.]+) LUFS"#), peak = last(#"Peak:\s+(-?[\d.]+) dBFS"#)
        return VerificationLoudness(measured: exitCode == 0 && lufs != nil, integratedLufs: lufs,
            truePeakDbtp: peak, error: exitCode == 0 && lufs != nil ? nil : "No complete EBU R128 measurement.")
    }

    static func parseDecode(_ text: String, exitCode: Int32) -> VerificationDecode {
        var count = 0
        var first: String?
        var counted = false
        for raw in text.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !line.isEmpty else { continue }
            if let range = line.range(of: "Last message repeated ", options: .caseInsensitive) {
                if counted, let repeats = Int(line[range.upperBound...].prefix(while: { $0.isNumber })) {
                    count = min(Int(Int32.max), count + min(repeats, Int(Int32.max)))
                }
                continue
            }
            counted = line.range(of: "non monotonically increasing dts", options: .caseInsensitive) == nil
            if counted { count += 1; if first == nil { first = line } }
        }
        if exitCode != 0 && count == 0 { count = 1; first = "Software decode failed (\(exitCode))." }
        return VerificationDecode(healthy: count == 0, error: first, errorCount: count)
    }

    private struct Failure: Error, CustomStringConvertible {
        let description: String
        init(_ description: String) { self.description = description }
    }
}
