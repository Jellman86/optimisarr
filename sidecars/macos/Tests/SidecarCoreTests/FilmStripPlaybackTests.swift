import Foundation
import Testing
@testable import SidecarCore

struct FilmStripPlaybackTests {
    private func strip(_ count: Int) -> FilmStrip {
        FilmStrip(frames: (0..<count).map { Data([UInt8($0)]) })
    }

    @Test("the picture advances with the clock, not with how often the view is rebuilt")
    func advances() {
        let start = Date(timeIntervalSinceReferenceDate: 1_000)
        let step = 1 / FilmStripPlayback.framesPerSecond
        let strip = strip(4)

        // The bug this covers: the hero stayed on frame zero for a whole job because the view's
        // own timer was replaced on every redraw. Asking the moment gives a different answer at a
        // different moment however many times it is asked at the same one.
        #expect(strip.frame(at: start) == strip.frame(at: start))
        #expect(strip.frame(at: start) != strip.frame(at: start.addingTimeInterval(step)))
        #expect(strip.frame(at: start.addingTimeInterval(step)) == Data([1]))
        #expect(strip.frame(at: start.addingTimeInterval(2 * step)) == Data([2]))
    }

    @Test("playback cycles rather than running off the end")
    func cycles() {
        let start = Date(timeIntervalSinceReferenceDate: 1_000)
        let step = 1 / FilmStripPlayback.framesPerSecond
        let strip = strip(3)

        #expect(strip.frame(at: start.addingTimeInterval(3 * step)) == strip.frame(at: start))
        #expect(strip.playingIndex(at: start.addingTimeInterval(4 * step)) == 1)
    }

    @Test("an empty strip has nothing to show and nothing to mark")
    func empty() {
        let now = Date()
        #expect(FilmStrip().frame(at: now) == nil)
        #expect(FilmStrip().playingIndex(at: now) == nil)
    }

    @Test("the marked thumbnail is the one being shown")
    func markMatchesPicture() {
        let strip = strip(5)
        for offset in 0..<12 {
            let at = Date(timeIntervalSinceReferenceDate: 1_000)
                .addingTimeInterval(Double(offset) / FilmStripPlayback.framesPerSecond)
            let index = strip.playingIndex(at: at)
            #expect(index != nil)
            #expect(strip.frames[index!] == strip.frame(at: at))
        }
    }
}
