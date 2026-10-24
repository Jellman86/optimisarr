import Foundation
import Testing
@testable import SidecarCore

@Suite("Whole window alignment")
struct WindowAlignmentTests {
    private func scores(_ blocks: [Double]) -> [TimelineAlignment.AlignmentScore] {
        blocks.enumerated().flatMap { block, score in
            (0..<50).map { frame in
                TimelineAlignment.AlignmentScore(frameNumber: block * 250 + frame * 5, score: score)
            }
        }
    }

    @Test("motion after a static opening identifies one correction for the whole window")
    func staticOpening() {
        #expect(TimelineAlignment.chooseWindow([
            (0, scores([100, 100, 40, 40])), (1, scores([100, 100, 98, 98])),
            (-1, scores([100, 100, 20, 20])),
        ], frameRate: 50, duration: 20) == 1)
    }

    @Test("opposing scene preferences cannot be combined to hide lost pictures")
    func conflict() {
        #expect(TimelineAlignment.chooseWindow([
            (0, scores([98, 98, 30, 30])), (1, scores([30, 30, 98, 98])),
            (-1, scores([20, 20, 20, 20])),
        ], frameRate: 50, duration: 20) == 0)
    }

    @Test("static scenes and incomplete evidence never authorise a correction")
    func incomplete() {
        #expect(TimelineAlignment.chooseWindow([
            (0, scores([96, 96])), (1, scores([97, 97])), (-1, scores([95, 95])),
        ], frameRate: 50, duration: 10) == 0)
        #expect(TimelineAlignment.chooseWindow([
            (0, scores([30, 30])), (1, scores([98])), (-1, scores([20, 20])),
        ], frameRate: 50, duration: 10) == 0)
        #expect(TimelineAlignment.chooseWindow([(1, scores([98, 98]))],
            frameRate: 50, duration: 10) == nil)
    }

    @Test("bounded probes leave final sampling unchanged")
    func preparation() {
        let command = ["-lavfi", "libvmaf=n_subsample=1:log_fmt=json", "-t", "40", "-f", "null", "-"]
        #expect(TimelineAlignment.prepare(command).contains("libvmaf=n_subsample=5:log_fmt=json"))
        #expect(command.contains("libvmaf=n_subsample=1:log_fmt=json"))
        #expect(TimelineAlignment.duration(command) == 40)
        #expect(TimelineAlignment.duration(["-f", "null", "-"]) == 5)
        #expect(TimelineAlignment.duration(["-t", "3600", "-f", "null", "-"]) == 40)
    }

    @Test("invalid or duplicate frame numbers are unavailable evidence")
    func malformed() {
        #expect(TimelineAlignment.readScores(#"{"frames":[{"metrics":{"vmaf":98}}]}"#) == nil)
        #expect(TimelineAlignment.readScores(#"{"frames":[{"frameNum":0,"metrics":{"vmaf":98}},{"frameNum":0,"metrics":{"vmaf":99}}]}"#) == nil)
    }

    @Test("worker frame duration uses the frozen measurement cadence")
    func frozenCadence() {
        #expect(abs(TimelineAlignment.commandFrameSeconds(["-lavfi", "fps=fps=23.976023976023978:start_time=0"])! - 0.041708) < 0.000001)
        #expect(TimelineAlignment.commandFrameSeconds(["-lavfi", "trim=start_frame=round({{distortedShift}}*50)"]) == 0.02)
        #expect(TimelineAlignment.commandFrameSeconds(["-lavfi", "fps=fps=0:start_time=0"]) == nil)
        #expect(TimelineAlignment.commandFrameSeconds(["-lavfi", "fps=fps=1e309:start_time=0"]) == nil)
    }
}
