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

    /// Playback speed. Fast enough to read as motion, slow enough that a short strip is not a
    /// flicker.
    private static let framesPerSecond = 6.0

    /// The strip is a fixed number of fixed-size slots.
    ///
    /// Both matter. An image asked to fill a height with no width limit reports an ideal width of
    /// its own aspect ratio, and two dozen of those in a row demanded far more than the menu's
    /// width — so the window grew, the content slid left and the panel changed shape as frames
    /// arrived. Fixed slots also stop the strip jittering as the buffer fills.
    private static let visibleThumbnails = 10
    private static let thumbnailSize = CGSize(width: 26, height: 16)

    @State private var tick = 0
    private let timer = Timer.publish(
        every: 1 / framesPerSecond, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(spacing: 5) {
            hero
            thumbnails
        }
        .onReceive(timer) { _ in
            guard !strip.isEmpty else { return }
            tick &+= 1
        }
    }

    private var hero: some View {
        // A fixed 16:9 well, so the menu does not jump about as frames of different aspect
        // ratios arrive, and there is something to look at before the first one does.
        ZStack {
            RoundedRectangle(cornerRadius: 6)
                .fill(.black.opacity(0.35))

            if let data = strip.frame(atTick: tick), let image = NSImage(data: data) {
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
    private var thumbnails: some View {
        if strip.frames.count > 1 {
            // The tail of the run, newest last, so the strip reads left to right in time.
            let shown = Array(strip.frames.suffix(Self.visibleThumbnails).enumerated())
            let offset = strip.frames.count - shown.count
            let playing = ((tick % strip.frames.count) + strip.frames.count) % strip.frames.count
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
