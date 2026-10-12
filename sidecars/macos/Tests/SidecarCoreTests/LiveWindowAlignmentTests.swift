import Foundation
import Testing
@testable import SidecarCore

/// The manifest contains disposable fixtures and commands emitted by the current Core builder.
/// No private media is committed. Run with OPTIMISARR_WINDOW_ALIGNMENT_MANIFEST and OPTIMISARR_FFMPEG.
@Suite("Live whole-window alignment",
       .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_WINDOW_ALIGNMENT_MANIFEST"] != nil))
struct LiveWindowAlignmentTests {
    struct Fixture: Decodable {
        let source: String
        let candidate: String
        let arguments: [String]
        let frameSeconds: Double
        let expectedShift: String
        let minimumHarmonic: Double?
        let maximumHarmonic: Double?
    }

    @Test("native worker preserves clean pictures and refuses content damage",
          arguments: ["timestamp-only", "content-shift", "degraded", "timestamp-search", "mixed-offset", "clean"])
    func measure(_ name: String) async throws {
        let env = ProcessInfo.processInfo.environment
        let manifest = try JSONDecoder().decode([String: Fixture].self,
            from: Data(contentsOf: URL(fileURLWithPath: env["OPTIMISARR_WINDOW_ALIGNMENT_MANIFEST"]!)))
        let fixture = try #require(manifest[name])
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let ffmpeg = URL(fileURLWithPath: env["OPTIMISARR_FFMPEG"]
            ?? "/Applications/OptimisarrSidecar.app/Contents/Resources/ffmpeg")
        let source = URL(fileURLWithPath: fixture.source)
        let candidate = URL(fileURLWithPath: fixture.candidate)
        let command = try MeasurementCommand.validate(fixture.arguments)
        let runner = ProcessTranscodeRunner()
        let shift: String?
        if fixture.arguments.contains(where: { $0.contains("{{distortedShift}}") }) {
            shift = await TimelineAlignment.measure(ffmpeg: ffmpeg, command: command,
                source: source, candidate: candidate, frameSeconds: fixture.frameSeconds,
                scratch: root, runner: runner)
        } else {
            shift = "0"
        }
        #expect(shift == fixture.expectedShift)
        let log = root.appendingPathComponent("score.json")
        let result = try await runner.run(ffmpeg,
            command.materialise(distorted: candidate, reference: source, log: log, distortedShift: shift), progress: { _ in })
        #expect(result.exitCode == 0)
        let json = try #require(JSONSerialization.jsonObject(with: Data(contentsOf: log)) as? [String: Any])
        let pooled = try #require(json["pooled_metrics"] as? [String: Any])
        let scores = try #require(pooled["vmaf"] as? [String: Double])
        let harmonic = try #require(scores["harmonic_mean"])
        if let minimum = fixture.minimumHarmonic { #expect(harmonic >= minimum) }
        if let maximum = fixture.maximumHarmonic { #expect(harmonic <= maximum) }
    }
}
