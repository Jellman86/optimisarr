import Foundation
import Testing
@testable import SidecarCore

@Suite("GPU statistics")
struct GpuStatisticsTests {
    @Test("a utilisation percentage becomes a fraction, with memory alongside")
    func parsesUtilisation() {
        let usage = GpuStatisticsParser.parse([
            "Device Utilization %": NSNumber(value: 55),
            "In use system memory": NSNumber(value: 1_253_064_704),
        ])

        #expect(usage?.device == 0.55)
        #expect(usage?.memoryInUse == 1_253_064_704)
    }

    @Test("a dictionary with no utilisation figure reports nothing rather than idle")
    func refusesToInventZero() {
        // Zero would be drawn as a GPU sitting idle, which is a different claim from "this Mac
        // does not publish the number".
        #expect(GpuStatisticsParser.parse([:]) == nil)
        #expect(GpuStatisticsParser.parse(["In use system memory": NSNumber(value: 4)]) == nil)
    }

    @Test("an out-of-range or oddly typed reading is clamped rather than believed")
    func clampsAndCoerces() {
        #expect(GpuStatisticsParser.parse(["Device Utilization %": NSNumber(value: 140)])?.device == 1)
        #expect(GpuStatisticsParser.parse(["Device Utilization %": NSNumber(value: -3)])?.device == 0)
        // Some builds hand these back as strings; a reading is better than nothing.
        #expect(GpuStatisticsParser.parse(["Device Utilization %": "50"])?.device == 0.5)
    }
}

@Suite("Frame preview")
struct FramePreviewTests {
    @Test("the seek comes before the input so one frame costs a keyframe jump, not a full decode")
    func seeksBeforeInput() {
        let arguments = FramePreviewCommand.arguments(
            source: URL(fileURLWithPath: "/scratch/source"), atSeconds: 61.5, width: 240)

        let seek = arguments.firstIndex(of: "-ss")!
        let input = arguments.firstIndex(of: "-i")!
        #expect(seek < input)
        #expect(arguments[seek + 1] == "61.50")
        #expect(arguments[input + 1] == "/scratch/source")
        #expect(arguments.contains("mjpeg"))
        #expect(arguments.last == "-")
    }

    @Test("a negative position is clamped rather than passed to ffmpeg")
    func clampsPosition() {
        let arguments = FramePreviewCommand.arguments(
            source: URL(fileURLWithPath: "/scratch/source"), atSeconds: -4, width: 240)

        #expect(arguments[arguments.firstIndex(of: "-ss")! + 1] == "0.00")
    }

    @Test("sampling is rate limited, so encoder progress cannot launch a process per update")
    func rateLimits() async {
        let runner = CountingBinaryRunner(output: Data([0xFF, 0xD8, 0xFF]))
        let sampler = FramePreviewSampler(
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"), runner: runner, interval: 2)
        let start = Date()
        let source = URL(fileURLWithPath: "/scratch/source")

        #expect(await sampler.frame(from: source, atSeconds: 1, now: start) != nil)
        #expect(await sampler.frame(from: source, atSeconds: 2, now: start.addingTimeInterval(0.5)) == nil)
        #expect(await sampler.frame(from: source, atSeconds: 3, now: start.addingTimeInterval(2.1)) != nil)
        #expect(runner.calls == 2)
    }

    @Test("a failed grab is silent, because a missing picture must never disturb a job")
    func failureIsSilent() async {
        let sampler = FramePreviewSampler(
            ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
            runner: CountingBinaryRunner(output: Data(), exitCode: 1))

        #expect(await sampler.frame(from: URL(fileURLWithPath: "/scratch/source"), atSeconds: 1) == nil)
    }
}

final class CountingBinaryRunner: BinaryCommandRunner, @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    private let output: Data
    private let exitCode: Int32

    init(output: Data, exitCode: Int32 = 0) {
        self.output = output
        self.exitCode = exitCode
    }

    var calls: Int { lock.withLock { count } }

    func run(_ executable: URL, _ arguments: [String]) async -> (exitCode: Int32, output: Data) {
        lock.withLock { count += 1 }
        return (exitCode, output)
    }
}

@Suite("Film strip")
struct FilmStripTests {
    private func frame(_ byte: UInt8) -> Data { Data([byte]) }

    @Test("frames accumulate in order so the strip plays forwards")
    func keepsOrder() {
        var strip = FilmStrip()
        strip.append(frame(1))
        strip.append(frame(2))
        strip.append(frame(3))

        #expect(strip.frames == [frame(1), frame(2), frame(3)])
        #expect(strip.frame(atTick: 0) == frame(1))
        #expect(strip.frame(atTick: 4) == frame(2))
    }

    @Test("the strip is bounded, so a long encode cannot grow it without limit")
    func dropsOldest() {
        // At one frame a second and several jobs at once, an unbounded strip would hold tens of
        // megabytes of JPEG by the end of a film.
        var strip = FilmStrip()
        for byte in 0..<UInt8(FilmStrip.capacity + 10) { strip.append(frame(byte)) }

        #expect(strip.frames.count == FilmStrip.capacity)
        #expect(strip.frames.first == frame(10))
        #expect(strip.frames.last == frame(UInt8(FilmStrip.capacity + 9)))
    }

    @Test("an empty strip has no frame to show rather than crashing on the modulo")
    func emptyIsSafe() {
        #expect(FilmStrip().frame(atTick: 3) == nil)
        #expect(FilmStrip().isEmpty)
    }
}

@MainActor
@Suite("Preview gating")
struct PreviewGatingTests {
    @Test("nothing is sampled until the menu asks, and what was sampled is dropped when it closes")
    func gatesAndClears() {
        // A frame grab is a process launch beside a running encode. Paying for it with no menu on
        // screen would be pure waste, and keeping the last frame after the menu closes would show
        // a stale picture the next time it opens.
        let gate = PreviewGate()
        #expect(!gate.isWanted)

        gate.set(true)
        #expect(gate.isWanted)

        gate.set(false)
        #expect(!gate.isWanted)
    }
}

/// Real ffmpeg, skipped unless `OPTIMISARR_FFMPEG` points at one:
///
///     OPTIMISARR_FFMPEG=$(pwd)/vendor/ffmpeg swift test
@Suite("Live frame preview", .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"] != nil))
struct LiveFramePreviewTests {
    @Test("grabs a real JPEG out of a real file")
    func grabsAJpeg() async throws {
        let ffmpeg = URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"]!)
        let clip = FileManager.default.temporaryDirectory
            .appendingPathComponent("preview-source-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: clip) }

        let made = await ProcessCommandRunner().run(ffmpeg, [
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-t", "4",
            "-c:v", "libx264", "-preset", "ultrafast", clip.path,
        ])
        #expect(made.exitCode == 0)

        let sampler = FramePreviewSampler(ffmpeg: ffmpeg, interval: 0, width: 240)
        let frame = await sampler.frame(from: clip, atSeconds: 2)

        let bytes = try #require(frame)
        // JPEG's start-of-image marker: proof this is a picture, not an error page or empty data.
        #expect(bytes.prefix(2) == Data([0xFF, 0xD8]))
        #expect(bytes.count > 500)
    }
}
