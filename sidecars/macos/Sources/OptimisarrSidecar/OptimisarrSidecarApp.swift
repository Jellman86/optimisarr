import AppKit
import SidecarCore
import SwiftUI

/// A menu-bar-only app. There is no window to manage and no dock icon: this thing's whole job is
/// to sit quietly, check in, and be honest about whether it is connected.
@main
struct OptimisarrSidecarApp: App {
    @StateObject private var session = SidecarSession()

    var body: some Scene {
        MenuBarExtra {
            SidecarMenu(session: session)
        } label: {
            // The icon carries the state, so a glance at the menu bar answers "is it working?"
            // without opening anything.
            Image(systemName: session.status.symbolName)
        }
        .menuBarExtraStyle(.window)
    }

    init() {
        // Accessory rather than regular: no dock icon, no app switcher entry. Done in code so the
        // package needs no Info.plist, which keeps the whole build reviewable as source.
        NSApplication.shared.setActivationPolicy(.accessory)
    }
}

extension SidecarStatus {
    var symbolName: String {
        switch self {
        case .connected: return "checkmark.circle.fill"
        case .pairing: return "ellipsis.circle"
        case .unpaired: return "link.badge.plus"
        case .unreachable: return "exclamationmark.triangle"
        case .revoked: return "xmark.circle.fill"
        case .disabledOnServer: return "pause.circle"
        case .pairingFailed: return "exclamationmark.triangle"
        }
    }
}
