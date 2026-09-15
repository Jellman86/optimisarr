import Foundation

/// The clock the film strip plays to.
///
/// Derived from absolute time rather than counted up by the view. A SwiftUI view that owns a timer
/// gets a fresh one every time its struct is rebuilt, and the menu is rebuilt on every progress
/// report an encode makes — several times a second — so the timer was reset before it ever fired
/// and the hero picture stayed on the strip's first frame for the whole job. Asking the current
/// moment what should be on screen cannot be reset by a redraw.
public enum FilmStripPlayback {
    /// Fast enough to read as motion, slow enough that a short strip is not a flicker.
    public static let framesPerSecond = 6.0

    public static func tick(at date: Date, framesPerSecond: Double = framesPerSecond) -> Int {
        Int((date.timeIntervalSinceReferenceDate * framesPerSecond).rounded(.down))
    }
}

extension FilmStrip {
    /// The frame to show at a moment, cycling. Nil while the strip is empty.
    public func frame(at date: Date, framesPerSecond: Double = FilmStripPlayback.framesPerSecond) -> Data? {
        frame(atTick: FilmStripPlayback.tick(at: date, framesPerSecond: framesPerSecond))
    }

    /// Which of `frames` is on screen at a moment, so the strip beneath can mark it. Nil while the
    /// strip is empty.
    public func playingIndex(at date: Date, framesPerSecond: Double = FilmStripPlayback.framesPerSecond) -> Int? {
        guard !frames.isEmpty else { return nil }
        let tick = FilmStripPlayback.tick(at: date, framesPerSecond: framesPerSecond)
        return ((tick % frames.count) + frames.count) % frames.count
    }
}
