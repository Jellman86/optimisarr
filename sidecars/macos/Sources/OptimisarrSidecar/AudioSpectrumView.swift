import SwiftUI

/// Frequency energy in a short source window near the encoding cursor; never a quality verdict.
struct AudioSpectrumView: View {
    let frame: Data?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text("SOURCE AUDIO").font(.system(size: 10, weight: .semibold, design: .monospaced))
                Spacer()
                Text("24 kHz").font(.system(size: 10, design: .monospaced))
            }.foregroundStyle(Instrument.dim)
            ZStack {
                RoundedRectangle(cornerRadius: 6).fill(.black.opacity(0.35))
                if let frame, let image = NSImage(data: frame) {
                    Image(nsImage: image).resizable().aspectRatio(contentMode: .fit)
                } else {
                    Text("Waiting for source spectrum…").font(.caption).foregroundStyle(Instrument.dim)
                }
            }
            .frame(maxWidth: .infinity).frame(height: 96).clipped()
            .clipShape(RoundedRectangle(cornerRadius: 6))
            .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(.white.opacity(0.08)))
            HStack {
                Text("0 Hz · logarithmic frequency")
                Spacer()
                Text("Up to 3 s →")
            }.font(.system(size: 9, design: .monospaced)).foregroundStyle(Instrument.dim)
            Text("Brighter colour = stronger signal. Source preview; verification runs separately.")
                .font(.system(size: 10)).foregroundStyle(Instrument.dim)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Source audio spectrogram. Logarithmic frequency from zero to 24 kilohertz; up to three seconds near the encoding position. Brightness shows signal strength, not verified output quality.")
    }
}
