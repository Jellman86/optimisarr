import SwiftUI

/// Frequency energy in a short source window near the encoding cursor; never a quality verdict.
struct AudioSpectrumView: View {
    let frame: Data?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text("Source audio").font(.system(size: 11, weight: .semibold))
                Spacer()
                Text("24 kHz").font(.system(size: 11)).monospacedDigit()
            }.foregroundStyle(Instrument.ink3)
            ZStack {
                RoundedRectangle(cornerRadius: 8).fill(Instrument.well)
                if let frame, let image = NSImage(data: frame) {
                    Image(nsImage: image).resizable().aspectRatio(contentMode: .fit)
                } else {
                    Text("Waiting for source spectrum…").font(.caption).foregroundStyle(Instrument.ink3)
                }
            }
            .frame(maxWidth: .infinity).frame(height: 96).clipped()
            .clipShape(RoundedRectangle(cornerRadius: 8))
            HStack {
                Text("0 Hz · logarithmic frequency")
                Spacer()
                Text("Up to 3 s →")
            }.font(.system(size: 10)).monospacedDigit().foregroundStyle(Instrument.ink3)
            Text("Brighter colour = stronger signal. Source preview; verification runs separately.")
                .font(.system(size: 10)).foregroundStyle(Instrument.ink3)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Source audio spectrogram. Logarithmic frequency from zero to 24 kilohertz; up to three seconds near the encoding position. Brightness shows signal strength, not verified output quality.")
    }
}
