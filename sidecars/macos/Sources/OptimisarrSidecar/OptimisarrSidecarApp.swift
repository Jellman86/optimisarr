import AppKit
import SidecarCore
import SwiftUI

/// The one session, shared by the menu bar and by the fallback window so both show the same state
/// rather than each running their own pairing.
@MainActor
final class AppState {
    static let shared = AppState()
    let session = SidecarSession()
    private init() {}
}

/// A menu-bar app, with a way back in when the menu bar has no room for it.
///
/// A `MenuBarExtra` is the whole interface right up until macOS has nowhere to draw it: on a
/// notched Mac with a busy menu bar the icon lands behind the notch, and an app with no window and
/// no Dock icon then has no reachable surface at all. Relaunching therefore opens a real window,
/// which is the gesture someone will already try when they think an app failed to start.
@main
struct OptimisarrSidecarApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var delegate
    @StateObject private var session = AppState.shared.session

    var body: some Scene {
        MenuBarExtra {
            SidecarMenu(session: session)
        } label: {
            // The icon carries the state, so a glance answers "is it working?" without clicking.
            Image(systemName: session.status.symbolName)
        }
        .menuBarExtraStyle(.window)
    }

    init() {
        // Accessory rather than regular: no Dock icon, no app switcher entry. Set in code so the
        // package behaves correctly even when run straight from the build directory.
        NSApplication.shared.setActivationPolicy(.accessory)
    }
}

/// Owns the fallback window.
///
/// Built with `NSHostingController` rather than a SwiftUI `Window` scene because an accessory app
/// has no reliable way to raise a scene from `applicationShouldHandleReopen` — the first attempt
/// here used a custom URL scheme, which opened nothing at all since the scheme was never
/// registered. Constructing the window directly is longer but actually works, and it was verified
/// by relaunching rather than assumed.
@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow?

    /// `open` on an already-running app raises this instead of starting a second copy, which is
    /// what makes relaunching the escape hatch.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        showPairingWindow()
        return true
    }

    /// Shown on first launch too, while nothing is paired. Someone who has just installed this has
    /// no reason to know it lives in the menu bar, and if the icon is hidden they would otherwise
    /// see nothing happen at all.
    func applicationDidFinishLaunching(_ notification: Notification) {
        AppState.shared.session.restore()
        if case .unpaired = AppState.shared.session.status {
            showPairingWindow()
        }
    }

    private func showPairingWindow() {
        // An accessory app is not frontmost, so without activating first the window would open
        // behind whatever the person is actually looking at.
        NSApplication.shared.activate(ignoringOtherApps: true)

        if let window {
            window.makeKeyAndOrderFront(nil)
            return
        }

        let hosting = NSHostingController(rootView: SidecarMenu(session: AppState.shared.session))
        let created = NSWindow(contentViewController: hosting)
        created.title = "Optimisarr Sidecar"
        created.styleMask = [.titled, .closable]
        created.setContentSize(NSSize(width: 340, height: 340))
        created.isReleasedWhenClosed = false
        created.center()

        window = created
        created.makeKeyAndOrderFront(nil)
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
