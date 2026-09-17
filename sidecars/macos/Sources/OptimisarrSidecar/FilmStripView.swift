import SidecarCore
import SwiftUI

/// A time-lapse of the frames this Mac has been seen encoding.
///
/// One still every second or so is not much to look at, and it cannot distinguish a job that is
/// moving from one that has quietly stopped. Played back at a steady tick the same frames become
/// a small moving picture of the film going through the encoder, with the strip beneath showing
/// where in that run the current frame sits.
struct FilmStripView: View {
    let strip: FilmStrip

    /// The strip is a fixed number of fixed-size slots.
    ///
    /// Both matter. An image asked to fill a height with no width limit reports an ideal width of
    /// its own aspect ratio, and two dozen of those in a row demanded far more than the menu's
    /// width — so the window grew, the content slid left and the panel changed shape as frames
    /// arrived. Fixed slots also stop the strip jittering as the buffer fills.
    private static let visibleThumbnails = 10
    private static let thumbnailSize = CGSize(width: 26, height: 16)

    /// A timeline rather than a timer this view owns.
    ///
    /// The menu is rebuilt on every progress report a running encode makes, which FFmpeg sends
    /// many times a second, and a `Timer.publish` held as a property is a *new* publisher on each
    /// of those rebuilds — cancelled and resubscribed before it ever reached its first tick. The
    /// count never moved, so the hero picture sat on the strip's oldest frame for the whole job
    /// while new frames arrived behind it.
    ///
    /// `TimelineView` schedules itself, and the picture is worked out from the moment it hands
    /// back rather than from a count this view keeps — so even a schedule that were restarted on
    /// every rebuild could not freeze the playback, because the clock is not this view's to reset.
    var body: some View {
        TimelineView(.periodic(from: .now, by: 1 / FilmStripPlayback.framesPerSecond)) { context in
            VStack(spacing: 5) {
                hero(at: context.date)
                thumbnails(at: context.date)
            }
        }
    }

    private func hero(at date: Date) -> some View {
        // A fixed 16:9 well, so the menu does not jump about as frames of different aspect
        // ratios arrive, and there is something to look at before the first one does.
        ZStack {
            RoundedRectangle(cornerRadius: 6)
                .fill(.black.opacity(0.35))

            if let data = strip.frame(at: date), let image = NSImage(data: data) {
                Image(nsImage: image)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .transition(.opacity)
            } else {
                Image(systemName: "film")
                    .font(.title3)
                    .foregroundStyle(.white.opacity(0.25))
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: 96)
        .clipped()
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .overlay(
            RoundedRectangle(cornerRadius: 6).strokeBorder(.white.opacity(0.08))
        )
        .accessibilityLabel("A time-lapse of the frames being encoded")
    }

    @ViewBuilder
    private func thumbnails(at date: Date) -> some View {
        if strip.frames.count > 1 {
            // The tail of the run, newest last, so the strip reads left to right in time.
            let shown = Array(strip.frames.suffix(Self.visibleThumbnails).enumerated())
            let offset = strip.frames.count - shown.count
            let playing = strip.playingIndex(at: date)
            HStack(spacing: 2) {
                ForEach(shown, id: \.offset) { index, data in
                    if let image = NSImage(data: data) {
                        Image(nsImage: image)
                            .resizable()
                            .aspectRatio(contentMode: .fill)
                            .frame(width: Self.thumbnailSize.width, height: Self.thumbnailSize.height)
                            .clipped()
                            .clipShape(RoundedRectangle(cornerRadius: 1.5))
                            .opacity(index + offset == playing ? 1 : 0.35)
                    }
                }
            }
            .frame(height: Self.thumbnailSize.height)
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityHidden(true)
        }
    }
}
