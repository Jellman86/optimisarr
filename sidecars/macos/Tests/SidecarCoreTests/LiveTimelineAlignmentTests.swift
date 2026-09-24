import Foundation
import Testing
@testable import SidecarCore

/// Chooses the right alignment for two real pairs that disagree about what "right" is.
///
/// The pair of files behind this is the whole reason the alignment is measured rather than derived.
/// Two episodes of the same show, encoded by the same command on the same machine, are identical
/// in every header field — container start, stream start, first decoded frame — and need opposite
/// answers:
///
///     S13E20   shift 0 → harmonic  0.21      shift +1 frame → harmonic 92.32
///     S13E21   shift 0 → harmonic 89.88      shift +1 frame → harmonic  0.31
///
/// Needs the four files and a real FFmpeg, so it is asked for rather than assumed:
///
///     OPTIMISARR_ALIGNMENT_FIXTURES=/path/to/dir swift test --filter LiveTimelineAlignment
///
/// The directory holds fail-S13E20.mkv, cand-S13E20.mp4, pass-S13E21.mkv and cand-S13E21.mp4.
@Suite(
    "Live timeline alignment",
    .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_ALIGNMENT_FIXTURES"] != nil))
struct LiveTimelineAlignmentTests {
    private var fixtures: URL {
        URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPTIMISARR_ALIGNMENT_FIXTURES"]!)
    }

    private var ffmpeg: URL {
        URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"]
            ?? "/Applications/OptimisarrSidecar.app/Contents/Resources/ffmpeg")
    }

    /// The shape of the server's window measurement, forty seconds from one minute in.
    private static let windowAtOneMinute = [
        "-nostdin", "-v", "error",
        "-ss", "55", "-i", "{{distorted}}", "-ss", "55", "-i", "{{reference}}",
        "-lavfi", "[0:v]settb=AVTB,setpts=PTS-{{distortedShift}}*1000000,fps=fps=25:start_time=0,trim=start=5:duration=40,settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p[dist];[1:v]settb=AVTB,fps=fps=25:start_time=0,trim=start=5:duration=40,settb=AVTB,setpts=PTS-STARTPTS,format=yuv420p[ref];[dist][ref]libvmaf=model=version=vmaf_v0.6.1:log_fmt=json:log_path={{log}}:shortest=1:repeatlast=0",
        "-t", "40", "-f", "null", "-",
    ]

    private func chosenShift(source: String, candidate: String) async -> String? {
        await TimelineAlignment.measure(
            ffmpeg: ffmpeg,
            command: try! MeasurementCommand.validate(Self.windowAtOneMinute),
            source: fixtures.appendingPathComponent(source),
            candidate: fixtures.appendingPathComponent(candidate),
            frameSeconds: 1.0 / 25.0,
            scratch: fixtures,
            runner: ProcessTranscodeRunner())
    }

    @Test("a candidate that lost a frame is moved by one")
    func movesAFrame() async {
        #expect(await chosenShift(source: "fail-S13E20.mkv", candidate: "cand-S13E20.mp4") == "0.04")
    }

    @Test("a candidate that is already in step is left alone")
    func leavesItAlone() async {
        // The half that matters most. A correction applied where none is wanted destroys a
        // measurement just as thoroughly as a missing one, and this pair scores 89.88 at zero and
        // 0.31 a frame away.
        #expect(await chosenShift(source: "pass-S13E21.mkv", candidate: "cand-S13E21.mp4") == "0")
    }
}
