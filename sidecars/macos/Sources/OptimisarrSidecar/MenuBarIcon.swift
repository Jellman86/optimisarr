import AppKit
import SidecarCore

/// The same Stellar artwork used by the application, Finder and the Windows tray.
@MainActor
enum MenuBarIcon {
    static let artwork = NSImage(contentsOf: Bundle.module.url(forResource: "BrandMark", withExtension: "png")!)!

    static let lightArtwork = NSImage(contentsOf: Bundle.module.url(forResource: "BrandMarkLight", withExtension: "png")!)!

    static func image(for status: SidecarStatus, spin: Double = 0) -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18))
        image.lockFocus()
        let mark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? artwork : lightArtwork
        mark.draw(in: NSRect(x: 0, y: 0, width: 18, height: 18))
        switch status {
        case .connected, .working: break
        default:
            NSColor.windowBackgroundColor.setFill()
            NSBezierPath(ovalIn: NSRect(x: 12, y: 0, width: 6, height: 6)).fill()
            NSColor.systemOrange.setFill()
            NSBezierPath(ovalIn: NSRect(x: 13, y: 1, width: 4, height: 4)).fill()
        }
        image.unlockFocus()
        image.isTemplate = false
        return image
    }
}
