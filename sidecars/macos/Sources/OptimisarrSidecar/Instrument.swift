import AppKit
import SwiftUI

/// The panel's colours. Everything is the system's own — label colours, separators, fills and
/// the user's accent — so the menu looks like the Mac it runs on and follows its appearance,
/// accent and contrast settings without being told.
///
/// The one exception is what a state *means*. The web interface, this menu and the Windows tray
/// say "working", "no server" and "revoked" in the same colours, chosen to stay apart under
/// colour-blindness: success is blue rather than green, a hold-up gold, a fault raspberry. Those
/// values are written exactly as `web/src/app.css` writes them, with the token named in each
/// comment; `scripts/tests/test_sidecar_theme.py` fails if this file, the Windows tray's
/// `SidecarTheme.cs` and the stylesheet drift apart.
enum Instrument {
    static let ink = Color.primary
    static let ink2 = Color(nsColor: .labelColor).opacity(0.85)
    static let ink3 = Color.secondary
    static let ink4 = Color(nsColor: .tertiaryLabelColor)
    static let accent = Color.accentColor
    static let separator = Color(nsColor: .separatorColor)
    /// A recessed well for a picture that has not arrived yet.
    static let well = Color(nsColor: .quaternaryLabelColor).opacity(0.5)

    static let ok = dynamic(light: "#0062cc", dark: "#4ea1ff")                                      // --ok
    static let okStrong = dynamic(light: "#004593", dark: "#bcdaff")                                // --ok-strong
    static let okSoft = dynamic(light: "rgba(0, 98, 204, 0.1)", dark: "rgba(78, 161, 255, 0.15)")   // --ok-soft
    static let warn = dynamic(light: "#946b00", dark: "#f5cc4f")                                    // --warn
    static let warnStrong = dynamic(light: "#5c4200", dark: "#fde7a2")                              // --warn-strong
    static let warnSoft = dynamic(light: "rgba(214, 160, 0, 0.17)", dark: "rgba(245, 204, 79, 0.14)") // --warn-soft
    static let bad = dynamic(light: "#d0216e", dark: "#ff6b9a")                                     // --bad
    static let badStrong = dynamic(light: "#8e0f48", dark: "#ffc6d8")                               // --bad-strong
    static let badSoft = dynamic(light: "rgba(208, 33, 110, 0.1)", dark: "rgba(255, 107, 154, 0.14)") // --bad-soft
    static let info = dynamic(light: "#4f4f57", dark: "#b4b4bb")                                    // --info
    static let infoStrong = dynamic(light: "#2c2c31", dark: "#e6e6eb")                              // --info-strong
    static let infoSoft = dynamic(light: "rgba(118, 118, 128, 0.13)", dark: "rgba(142, 142, 150, 0.18)") // --info-soft

    /// One colour that answers to the Mac's appearance, so a state is never stated in a theme
    /// the rest of the machine has left.
    private static func dynamic(light: String, dark: String) -> Color {
        let light = NSColor(css: light), dark = NSColor(css: dark)
        return Color(nsColor: NSColor(name: nil) { appearance in
            appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua ? dark : light
        })
    }

    /// A label in the readout: small and secondary, in sentence case the way macOS labels a group.
    static func label(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 11, weight: .medium))
            .foregroundStyle(ink3)
    }
}

/// What a state means, as a tinted fill and an ink to read on it: the web interface's `tone-*`
/// classes. One tone colours the status chip and any fault text the same way.
enum Tone {
    case ok, warn, bad, info

    var ink: Color {
        switch self {
        case .ok: Instrument.ok
        case .warn: Instrument.warn
        case .bad: Instrument.bad
        case .info: Instrument.info
        }
    }

    var strong: Color {
        switch self {
        case .ok: Instrument.okStrong
        case .warn: Instrument.warnStrong
        case .bad: Instrument.badStrong
        case .info: Instrument.infoStrong
        }
    }

    var soft: Color {
        switch self {
        case .ok: Instrument.okSoft
        case .warn: Instrument.warnSoft
        case .bad: Instrument.badSoft
        case .info: Instrument.infoSoft
        }
    }
}

/// A state in a word or two, as a tinted capsule.
struct StatusChip: View {
    let text: String
    let tone: Tone

    var body: some View {
        Text(text)
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(tone.strong)
            .padding(.horizontal, 8)
            .padding(.vertical, 3)
            .background(tone.soft, in: Capsule())
            .fixedSize()
    }
}

/// One line of the readout: what it is on the left, what it reads on the right.
///
/// Tabular digits on purpose. These figures are compared down the column far more often than
/// they are read one at a time, and proportional digits make that harder.
struct ReadoutRow: View {
    let name: String
    let value: String
    var lit = false

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Instrument.label(name)
            Spacer(minLength: 8)
            Text(value)
                .font(.system(size: 12))
                .monospacedDigit()
                .foregroundStyle(lit ? Instrument.accent : Instrument.ink2)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .padding(.vertical, 6)
        .overlay(alignment: .bottom) {
            Rectangle().fill(Instrument.separator).frame(height: 1)
        }
    }
}

/// A figure large enough to read without stopping, over a label small enough to ignore once you
/// know what it is. Three of these share one card.
struct ReadoutCell: View {
    let figure: String
    let unit: String

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(figure)
                .font(.system(size: 17, weight: .semibold))
                .monospacedDigit()
                .foregroundStyle(Instrument.ink)
                .lineLimit(1)
            Instrument.label(unit)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

extension NSColor {
    /// A colour written as the stylesheet writes it: `#rrggbb` or `rgba(r, g, b, a)`.
    convenience init(css: String) {
        let text = css.trimmingCharacters(in: .whitespaces).lowercased()
        if text.hasPrefix("#"), text.count == 7, let hex = UInt32(text.dropFirst(), radix: 16) {
            self.init(srgbRed: Double((hex >> 16) & 0xFF) / 255, green: Double((hex >> 8) & 0xFF) / 255,
                      blue: Double(hex & 0xFF) / 255, alpha: 1)
            return
        }
        if text.hasPrefix("rgba("), text.hasSuffix(")") {
            let parts = text.dropFirst(5).dropLast().split(separator: ",")
                .compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
            if parts.count == 4 {
                self.init(srgbRed: parts[0] / 255, green: parts[1] / 255, blue: parts[2] / 255, alpha: parts[3])
                return
            }
        }
        preconditionFailure("Unsupported colour token: \(css)")
    }
}
