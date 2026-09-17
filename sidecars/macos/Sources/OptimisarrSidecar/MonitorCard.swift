import SwiftUI

private struct MonitorCard: ViewModifier {
    @State private var hovering = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content.padding(16)
            .background(LinearGradient(colors: [Instrument.cell, Instrument.ground], startPoint: .topLeading, endPoint: .bottomTrailing), in: RoundedRectangle(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(hovering ? Instrument.phosphor.opacity(0.5) : Instrument.rule.opacity(0.7)))
            .shadow(color: .black.opacity(hovering ? 0.24 : 0.12), radius: hovering ? 12 : 6, y: hovering ? 7 : 3)
            .animation(reduceMotion ? nil : .easeOut(duration: 0.18), value: hovering)
            .onHover { hovering = $0 }
    }
}

extension View {
    func monitorCard() -> some View { modifier(MonitorCard()) }
}

struct MonitorButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.foregroundStyle(Instrument.ink)
            .background(Instrument.cell.opacity(configuration.isPressed ? 0.6 : 1), in: RoundedRectangle(cornerRadius: 9))
            .overlay(RoundedRectangle(cornerRadius: 9).stroke(Instrument.rule))
    }
}
