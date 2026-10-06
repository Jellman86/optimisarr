import SwiftUI

/// A grouped block of content inside the panel: the system's quiet fill, no outline.
///
/// Not glass. The popover itself is glass on macOS 26, and the controls are; content sitting on
/// glass stays a plain grouped surface, as Apple's own menus and Control Centre modules do.
private struct MonitorCard: ViewModifier {
    func body(content: Content) -> some View {
        content.padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(.fill.quaternary, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
    }
}

extension View {
    func monitorCard() -> some View { modifier(MonitorCard()) }

    /// The system button for an action in the panel: glass on macOS 26, bordered before it.
    /// `prominent` is the one button that does the thing.
    func panelButton(prominent: Bool = false) -> some View { modifier(PanelButton(prominent: prominent)) }
}

private struct PanelButton: ViewModifier {
    let prominent: Bool
    @Environment(\.snapshotRendering) private var snapshot

    func body(content: Content) -> some View {
        // Glass needs the macOS 26 SDK (Swift 6.2 / Xcode 26) to build, as well as macOS 26 to run.
        #if compiler(>=6.2)
        if #available(macOS 26, *), !snapshot {
            if prominent { content.buttonStyle(.glassProminent).controlSize(.large) }
            else { content.buttonStyle(.glass).controlSize(.large) }
        } else {
            bordered(content)
        }
        #else
        bordered(content)
        #endif
    }

    @ViewBuilder private func bordered(_ content: Content) -> some View {
        if prominent { content.buttonStyle(.borderedProminent).controlSize(.large) }
        else { content.buttonStyle(.bordered).controlSize(.large) }
    }
}

extension EnvironmentValues {
    /// Set by `--render-menu`, which pictures the panel offscreen where glass cannot be drawn.
    @Entry var snapshotRendering = false
}
