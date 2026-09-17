import AppKit
import SwiftUI

/// Shared panel colours follow the system appearance while keeping Optimisarr's slate and teal identity.
enum Instrument {
    // Optimisarr's own palette, taken from the web interface: Tailwind slate for every neutral and
    // cyan for the one accent. The two faces are the same product and a person moves between them
    // in a minute, so a sidecar with a palette of its own would read as a different tool.
    //
    // Both appearances, for the same reason — the web interface has a light theme and a dark one,
    // and this follows the Mac the way that follows its toggle.
    static let ground = dynamic(light: 0xF8FAFC, dark: 0x101A2C)   // slate-50  / slate-950
    static let cell = dynamic(light: 0xFFFFFF, dark: 0x18253B)     // white     / slate-900
    static let rule = dynamic(light: 0xE2E8F0, dark: 0x334155)     // slate-200 / slate-700
    static let hairline = dynamic(light: 0xF1F5F9, dark: 0x1E293B) // slate-100 / slate-800
    static let ink = dynamic(light: 0x0F172A, dark: 0xF8FAFC)      // slate-900 / slate-50
    static let value = dynamic(light: 0x334155, dark: 0xCBD5E1)    // slate-700 / slate-300
    static let dim = dynamic(light: 0x64748B, dark: 0x94A3B8)      // slate-500 / slate-400
    static let phosphor = dynamic(light: 0x087E8B, dark: 0x7BD8D1) // cyan-600  / cyan-500
    static let amber = dynamic(light: 0xD97706, dark: 0xF59E0B)    // amber-600 / amber-500
    static let alarm = dynamic(light: 0xDC2626, dark: 0xF87171)    // red-600   / red-400

    /// One colour that answers to the Mac's appearance, so the panel is not stated in a theme the
    /// rest of the machine has left.
    private static func dynamic(light: UInt32, dark: UInt32) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
                ? NSColor(hex: dark) : NSColor(hex: light)
        })
    }

    /// A label in the readout: small, spaced, and never competing with the figure beside it.
    static func label(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.system(size: 9.5, weight: .medium, design: .monospaced))
            .tracking(0.9)
            .foregroundStyle(dim)
    }
}

/// One line of the readout: what it is on the left, what it reads on the right.
///
/// Monospaced and tabular on purpose. These figures are compared down the column far more often
/// than they are read one at a time, and proportional digits make that impossible.
struct ReadoutRow: View {
    let name: String
    let value: String
    var lit = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Instrument.label(name)
            Spacer(minLength: 8)
            Text(value)
                .font(.system(size: 10.5, design: .monospaced))
                .monospacedDigit()
                .foregroundStyle(lit ? Instrument.phosphor : Instrument.value)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .padding(.vertical, 4)
        .overlay(alignment: .bottom) {
            Rectangle().fill(Instrument.hairline).frame(height: 1)
        }
    }
}

/// A figure large enough to read without stopping, under a label small enough to ignore once
/// you know what it is.
struct ReadoutCell: View {
    let figure: String
    let unit: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(figure)
                .font(.system(size: 15, weight: .regular, design: .monospaced))
                .monospacedDigit()
                .foregroundStyle(Instrument.ink)
            Instrument.label(unit)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 8)
        .padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 4).fill(Instrument.cell))
    }
}

private extension NSColor {
    /// A Tailwind hex, as written in the web interface's stylesheet.
    convenience init(hex: UInt32) {
        self.init(
            srgbRed: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            alpha: 1)
    }
}
