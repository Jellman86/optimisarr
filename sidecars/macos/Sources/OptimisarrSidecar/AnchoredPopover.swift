import AppKit
import SwiftUI

/// Owns native popover chrome and its menu-bar anchor independently of SwiftUI's changing layout.
@MainActor
final class AnchoredPopover: NSObject, NSPopoverDelegate {
    let popover = NSPopover()
    let hosting: NSHostingController<AnyView>
    private let visibilityChanged: (Bool) -> Void
    private var sizeObservation: NSKeyValueObservation?

    init<Content: View>(content: Content, visibilityChanged: @escaping (Bool) -> Void = { _ in }) {
        self.visibilityChanged = visibilityChanged
        hosting = NSHostingController(rootView: AnyView(content))
        super.init()
        hosting.sizingOptions = [.preferredContentSize]
        popover.contentViewController = hosting
        popover.behavior = .transient
        // SwiftUI handles control transitions. Native window interpolation can leave stale
        // square backing-store pixels when a disclosure reverses before its resize completes.
        popover.animates = false
        popover.delegate = self
        sizeObservation = hosting.observe(\.preferredContentSize, options: [.new]) { [weak self] _, _ in
            Task { @MainActor [weak self] in self?.updateSize() }
        }
    }

    func show(relativeTo anchor: NSView) {
        NSApplication.shared.activate(ignoringOtherApps: true)
        updateSize()
        popover.show(relativeTo: anchor.bounds, of: anchor, preferredEdge: .minY)
        popover.contentViewController?.view.window?.makeKey()
    }

    func updateSize() {
        let ideal = hosting.preferredContentSize
        let size = NSSize(width: max(1, ideal.width), height: max(1, ideal.height))
        guard ideal.width > 0, ideal.height > 0, popover.contentSize != size else { return }
        popover.contentSize = size
    }

    func popoverWillShow(_ notification: Notification) { visibilityChanged(true) }
    func popoverDidClose(_ notification: Notification) { visibilityChanged(false) }

    func popoverShouldDetach(_ popover: NSPopover) -> Bool { false }
}
