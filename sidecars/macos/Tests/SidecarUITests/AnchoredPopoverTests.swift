import AppKit
import SwiftUI
import Testing
@testable import OptimisarrSidecar

@MainActor
@Suite(.serialized)
struct AnchoredPopoverTests {
    @Test func contentResizingPreservesTheNativePopoverAndCannotDetach() async throws {
        _ = NSApplication.shared
        let panel = AnchoredPopover(content: Text("Monitor").frame(width: 390, height: 420))
        let original = panel.popover
        for height in [420.0, 650.0, 420.0, 580.0, 420.0] {
            panel.hosting.rootView = AnyView(Text("Monitor").frame(width: 390, height: height))
            panel.hosting.view.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(60))
            panel.updateSize()
            #expect(panel.popover === original)
            #expect(panel.popover.contentSize == NSSize(width: 390, height: height))
            #expect(!panel.popoverShouldDetach(panel.popover))
        }
        #expect(panel.popover.behavior == .transient)
        #expect(!panel.popover.animates)
        #expect(MenuBarIcon.artwork.size.width >= 1024)
    }
    @Test func expandingAndCollapsingKeepsTheSameTopAnchor() async throws {
        _ = NSApplication.shared
        let screen = try #require(NSScreen.main).visibleFrame
        let anchorWindow = NSWindow(contentRect: NSRect(x: screen.midX, y: screen.maxY - 24, width: 80, height: 24), styleMask: [.borderless], backing: .buffered, defer: false)
        let anchor = NSView(frame: NSRect(x: 0, y: 0, width: 80, height: 24))
        anchorWindow.contentView = anchor
        anchorWindow.orderFront(nil)
        let panel = AnchoredPopover(content: Text("Monitor").frame(width: 390, height: 200))
        defer { panel.popover.close(); anchorWindow.orderOut(nil) }
        panel.show(relativeTo: anchor)
        try await Task.sleep(for: .milliseconds(80))
        let nativeWindow = try #require(panel.hosting.view.window)
        let top = nativeWindow.frame.maxY
        for height in [360.0, 200.0, 400.0, 200.0] {
            panel.hosting.rootView = AnyView(Text("Monitor").frame(width: 390, height: height))
            panel.hosting.view.layoutSubtreeIfNeeded()
            try await Task.sleep(for: .milliseconds(80))
            panel.updateSize()
            #expect(panel.popover.isShown)
            #expect(panel.hosting.view.window === nativeWindow)
            #expect(abs(nativeWindow.frame.maxY - top) <= 1)
            #expect(abs(panel.popover.contentSize.height - height) <= 1)
        }
    }

}
