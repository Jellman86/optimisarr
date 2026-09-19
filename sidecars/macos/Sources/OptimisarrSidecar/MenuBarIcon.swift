import AppKit
import SidecarCore

/// The same Precession artwork used by the application, Finder and the Windows tray.
@MainActor
enum MenuBarIcon {
    static let artwork = loadArtwork(named: "BrandMark")
    static let lightArtwork = loadArtwork(named: "BrandMarkLight")

    private static func loadArtwork(named name: String) -> NSImage {
        let url = resourceURL(named: name, applicationURL: Bundle.main.bundleURL,
                              resourceDirectory: Bundle.main.resourceURL) {
            Bundle.module.url(forResource: name, withExtension: "png")
        }
        guard let url, let image = NSImage(contentsOf: url) else {
            preconditionFailure("Missing packaged Precession artwork: \(name)")
        }
        return image
    }

    /// A packaged app must use its own assets. SwiftPM's generated accessor can otherwise fall
    /// back to an absolute build-tree path and hide a broken release on the machine that built it.
    static func resourceURL(named name: String, applicationURL: URL, resourceDirectory: URL?,
                            developmentResource: () -> URL?) -> URL? {
        guard applicationURL.pathExtension == "app" else { return developmentResource() }
        guard let resourceDirectory else { return nil }
        let package = resourceDirectory.appendingPathComponent("OptimisarrSidecar_OptimisarrSidecar.bundle")
        // SwiftPM lays out plain resource directories; Xcode uses Contents/Resources bundles.
        for folder in [package.appendingPathComponent("Contents/Resources"), package] {
            let candidate = folder.appendingPathComponent(name + ".png")
            let resolved = candidate.resolvingSymlinksInPath().path
            guard resolved.hasPrefix(applicationURL.resolvingSymlinksInPath().path + "/") else { continue }
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
        }
        return nil
    }

    static func image(for status: SidecarStatus, spin: Double = 0) -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18))
        image.lockFocus()
        let mark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? artwork : lightArtwork
        let turns = rotationTurns(for: status, spin: spin,
                                  reduceMotion: NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
        NSGraphicsContext.current?.saveGraphicsState()
        let transform = NSAffineTransform()
        transform.translateX(by: 9, yBy: 9)
        transform.rotate(byDegrees: turns * 360)
        transform.translateX(by: -9, yBy: -9)
        transform.concat()
        mark.draw(in: NSRect(x: 0, y: 0, width: 18, height: 18))
        NSGraphicsContext.current?.restoreGraphicsState()
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

    static func rotationTurns(for status: SidecarStatus, spin: Double, reduceMotion: Bool) -> Double {
        guard !reduceMotion, case .working = status else { return 0 }
        return spin.truncatingRemainder(dividingBy: 1)
    }
}
