import AppKit
import SidecarCore

/// The same Precession artwork used by the application, Finder and the Windows tray.
@MainActor
enum MenuBarIcon {
    static let artwork = loadArtwork(named: "BrandMark")
    static let lightArtwork = loadArtwork(named: "BrandMarkLight")
    static let motionArtwork = loadArtwork(named: "BrandMotion")
    static let lightMotionArtwork = loadArtwork(named: "BrandMotionLight")
    static let darkFrames = frames(from: motionArtwork)
    private static let lightFrames = frames(from: lightMotionArtwork)
    static let frameCount = 88

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

    static func image(for status: SidecarStatus, spin: Double = 0, reduceMotion: Bool? = nil) -> NSImage {
        let dark = NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        if let frame = frameIndex(for: status, spin: spin,
                                  reduceMotion: reduceMotion ?? NSWorkspace.shared.accessibilityDisplayShouldReduceMotion), frame > 0 {
            return (dark ? darkFrames : lightFrames)[frame]
        }
        let image = NSImage(size: NSSize(width: 18, height: 18))
        image.lockFocus()
        let mark = dark ? artwork : lightArtwork
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

    static func frameIndex(for status: SidecarStatus, spin: Double, reduceMotion: Bool) -> Int? {
        guard !reduceMotion, case .working = status else { return nil }
        let phase = spin.truncatingRemainder(dividingBy: 1)
        return min(frameCount - 1, Int(max(0, phase) * Double(frameCount) + 1e-6))
    }

    private static func frames(from sheet: NSImage) -> [NSImage] {
        (0..<frameCount).map { frame in
            NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
                let source = NSRect(x: CGFloat(frame % 11 * 48), y: CGFloat((7 - frame / 11) * 48),
                                    width: 48, height: 48)
                sheet.draw(in: rect, from: source, operation: .copy, fraction: 1)
                return true
            }
        }
    }

    static func blend(from: NSImage, to: NSImage, progress: CGFloat) -> NSImage {
        NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            from.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1 - progress)
            to.draw(in: rect, from: .zero, operation: .sourceOver, fraction: progress)
            return true
        }
    }
}
