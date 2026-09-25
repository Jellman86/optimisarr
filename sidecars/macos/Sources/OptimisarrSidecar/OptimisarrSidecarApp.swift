import AppKit
import Combine
import SidecarCore
import SwiftUI

/// The one session, shared by the menu bar and by the fallback window so both show the same state
/// rather than each running their own pairing.
@MainActor
final class AppState {
    static let shared = AppState()
    let settings: SidecarSettings
    let session: SidecarSession

    private init() {
        let settings = SidecarSettings()
        self.settings = settings
        // The runner reads the location per job, so changing it here takes effect on the next job
        // without a restart.
        // No executor is passed. `SidecarSession` builds one itself wired to the preview gate it
        // hands the menu, and passing one here silently replaced it with a runner whose previews
        // defaulted to off — which is why no shipped build has ever drawn a film strip. The
        // settings it needs are given to the session instead, so the two cannot come apart again.
        self.session = SidecarSession(settingsSnapshot: settings.snapshot)
    }
}

/// A menu-bar app, with a way back in when the menu bar has no room for it.
///
/// The menu-bar popover is the whole interface right up until macOS has nowhere to draw it: on a
/// notched Mac with a busy menu bar the icon lands behind the notch, and an app with no window and
/// no Dock icon then has no reachable surface at all. Relaunching therefore opens a real window,
/// which is the gesture someone will already try when they think an app failed to start.
@main
@MainActor
enum OptimisarrSidecarApp {
    static func main() {
        // A design pass over the menu, rather than the app: render its states and stop before any
        // pairing, network or menu bar work begins.
        let arguments = CommandLine.arguments
        if let flag = arguments.firstIndex(of: MenuRenderer.flag), flag + 1 < arguments.count {
            MenuRenderer.render(into: URL(fileURLWithPath: arguments[flag + 1]))
            exit(0)
        }
        if let flag = arguments.firstIndex(of: MenuRenderer.iconFlag), flag + 1 < arguments.count {
            MenuRenderer.renderIcons(into: URL(fileURLWithPath: arguments[flag + 1]))
            exit(0)
        }

        // Pair from a terminal and exit, without ever putting a menu bar or a window on screen.
        // A machine that can only be paired by hand cannot be set up over SSH or recovered
        // remotely, which is a poor property for an app whose job is to sit in a cupboard.
        if let flag = arguments.firstIndex(of: HeadlessPairing.flag), flag + 1 < arguments.count {
            let address = arguments[flag + 1]
            nonisolated(unsafe) var result: Int32 = 1
            // Run the main run loop rather than blocking on a semaphore. Pairing has to touch the
            // main actor — the session lives there — so parking the main thread on a wait means the
            // work it is waiting for can never start, and the command hangs for ever. Running the
            // loop lets that work proceed on the thread it needs, and stopping it is what ends the
            // command.
            Task {
                result = await HeadlessPairing.run(serverAddress: address)
                CFRunLoopStop(CFRunLoopGetMain())
            }
            CFRunLoopRun()
            exit(result)
        }

        // Accessory rather than regular: no Dock icon, no app switcher entry. Set in code so the
        // package behaves correctly even when run straight from the build directory.
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        let delegate = AppDelegate()
        application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
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
    private var statusItem: NSStatusItem?
    private var monitor: AnchoredPopover?
    private var iconObservation: AnyCancellable?
    private var iconSettleTask: Task<Void, Never>?
    private var lastWorkingIcon: NSImage?
    private var lastWorkingPhase = 0.0
    private var iconWasWorking = false
    private var iconWasReduced = false
    private var lastIdleStatus: SidecarStatus?
    /// False until the stored pairing has been looked for. Reopen arrives before that answer does.
    private var restoreSettled = false

    /// `open` on an already-running app raises this instead of starting a second copy, which is
    /// what makes relaunching the escape hatch.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows: Bool) -> Bool {
        // Only when there is something the menu cannot do. This used to raise a window every time,
        // so logging in — or opening the app from Finder out of habit — put a window on screen for
        // an app whose whole interface is in the menu bar.
        //
        // Two conditions, not one. Reopen also arrives *during* launch, before the stored pairing
        // has been read, and the status is `.unpaired` until it has been — so testing the status
        // alone still showed a window on every launch, which is the bug this was meant to fix.
        // Until the restore has settled, the launch path below owns that decision and this one
        // stays out of it.
        //
        // The cost is that the window no longer works as an escape hatch for a menu bar icon lost
        // behind the notch while paired. That is the rarer problem, and a window nobody asked for
        // every login is the one actually being experienced.
        guard restoreSettled, !AppState.shared.session.isPaired else { return true }
        showPairingWindow()
        return true
    }

    /// Shown on first launch too, while nothing is paired. Someone who has just installed this has
    /// no reason to know it lives in the menu bar, and if the icon is hidden they would otherwise
    /// see nothing happen at all.
    /// The sleep and wake hooks live here rather than in the session so SidecarCore stays free
    /// of AppKit and its lifecycle can be tested by calling the same two methods directly.
    private func observePowerEvents() {
        let centre = NSWorkspace.shared.notificationCenter
        centre.addObserver(forName: NSWorkspace.willSleepNotification, object: nil, queue: .main) { _ in
            Task { @MainActor in await AppState.shared.session.systemWillSleep() }
        }
        centre.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { _ in
            Task { @MainActor in AppState.shared.session.systemDidWake() }
        }
    }

    /// Quitting mid-encode hands the job back first, so the server reassigns it now rather than
    /// after the lease lapses. The reply is deferred only for as long as that one call takes.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        Task { @MainActor in
            await AppState.shared.session.prepareToQuit()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    /// A RAM disk that outlived a crash holds real memory until the Mac reboots, and nothing on
    /// screen would say so. Cleared at launch, before any job can make another.
    private func sweepStrayRamDisks() {
        Task.detached(priority: .utility) { RamDisk.sweepStrays() }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        installApplicationMenu()
        installMenuBarItem()
        observePowerEvents()
        sweepStrayRamDisks()
        // Restoring reads the Keychain, and that read can block — on an item written by a build
        // whose signature no longer matches, indefinitely. Deferring it into a `Task { @MainActor }`
        // was not enough and looked like it was: this is already the main actor, so the block
        // happened here just the same, leaving a windowless app that had started and then frozen.
        // The session now does the read off the main actor; this waits for the answer only so it
        // knows whether to offer pairing.
        Task { @MainActor in
            await AppState.shared.session.restoreAndSettle()
            restoreSettled = true
            // Whether a pairing was found, not what the status says. A restored pairing leaves the
            // status at `.unpaired` until the first check-in answers, so asking the status opened
            // this window on every launch of a perfectly well paired app.
            if !AppState.shared.session.isPaired {
                showPairingWindow()
            }
        }
    }

    private func installApplicationMenu() {
        let menu = NSMenu()
        let application = NSMenuItem()
        application.submenu = NSMenu(title: "Optimisarr Sidecar")
        application.submenu?.addItem(withTitle: "About Optimisarr Sidecar", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        application.submenu?.addItem(.separator())
        application.submenu?.addItem(withTitle: "Quit Optimisarr Sidecar", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(application)
        let edit = NSMenuItem()
        edit.submenu = NSMenu(title: "Edit")
        for (title, action, key) in [("Undo", "undo:", "z"), ("Cut", "cut:", "x"), ("Copy", "copy:", "c"), ("Paste", "paste:", "v"), ("Select All", "selectAll:", "a")] {
            edit.submenu?.addItem(withTitle: title, action: Selector(action), keyEquivalent: key)
        }
        menu.addItem(edit)
        NSApplication.shared.mainMenu = menu
    }

    private func installMenuBarItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem = item
        monitor = AnchoredPopover(content: SidecarMenu(session: AppState.shared.session)) { visible in
            AppState.shared.session.setPreviewsWanted(visible)
        }
        item.button?.target = self
        item.button?.action = #selector(toggleMonitor)
        item.button?.toolTip = "Optimisarr Sidecar"
        item.button?.setAccessibilityLabel("Optimisarr Sidecar")
        iconObservation = Publishers.CombineLatest(
            AppState.shared.session.$status,
            AppState.shared.session.$spin
        ).sink { [weak self] status, spin in
            guard let self else { return }
            if case .working = status {
                self.lastIdleStatus = nil
                let reduced = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
                if reduced && self.iconWasWorking && self.iconWasReduced { return }
                self.iconSettleTask?.cancel()
                self.iconSettleTask = nil
                self.iconWasWorking = true
                self.iconWasReduced = reduced
                // The session clears its phase just before publishing the idle status. Keep the
                // last active frame until that status arrives, so the outgoing motion can settle.
                if spin == 0 && self.lastWorkingPhase > 0 && !reduced { return }
                self.lastWorkingPhase = spin
                let frame = MenuBarIcon.image(for: status, spin: spin)
                self.lastWorkingIcon = frame
                self.statusItem?.button?.image = frame
            } else {
                let idle = MenuBarIcon.image(for: status)
                if self.iconSettleTask != nil && self.lastIdleStatus != status {
                    self.iconSettleTask?.cancel()
                    self.iconSettleTask = nil
                }
                self.lastIdleStatus = status
                if self.iconWasWorking, let previous = self.lastWorkingIcon,
                   !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
                    self.iconSettleTask?.cancel()
                    self.iconSettleTask = Task { [weak self] in
                        for step in 1...6 {
                            try? await Task.sleep(for: .milliseconds(80))
                            guard !Task.isCancelled else { return }
                            self?.statusItem?.button?.image = step == 6 ? idle : MenuBarIcon.blend(
                                from: previous, to: idle, progress: CGFloat(step) / 6)
                        }
                        self?.iconSettleTask = nil
                    }
                } else if self.iconSettleTask == nil {
                    self.statusItem?.button?.image = idle
                }
                self.iconWasWorking = false
                self.iconWasReduced = false
                self.lastWorkingIcon = nil
                self.lastWorkingPhase = 0
            }
        }
    }

    @objc private func toggleMonitor() {
        guard let monitor, let button = statusItem?.button else { return }
        if monitor.popover.isShown { monitor.popover.performClose(nil) }
        else { monitor.show(relativeTo: button) }
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
        created.isReleasedWhenClosed = false
        created.center()

        window = created
        created.makeKeyAndOrderFront(nil)
    }
}
