import AppKit
import SidecarCore

/// The menu bar mark: Optimisarr's isometric wireframe cube, drawn rather than scaled from the app
/// icon.
///
/// A menu bar icon must be a *template* — flat shapes with no colour of their own, which macOS
/// tints for light and dark and inverts while the menu is open. The app icon is a glowing cyan
/// render on a dark ground; thresholding that into a silhouette gives mud at 16pt, and it would
/// look wrong the moment the menu highlighted. Redrawing the mark as strokes keeps it crisp at menu
/// bar size and lets the system own the colour, which is what makes it sit correctly next to
/// Apple's own icons.
///
/// The cube is simplified deliberately. All twelve wireframe edges read as a smudge at this size,
/// so it draws the hexagonal silhouette plus the three edges meeting at the centre — the projection
/// the full wireframe resolves to anyway, and the part the eye actually reads as "cube".
enum MenuBarIcon {
    /// Menu bar content is 16pt tall by convention; a touch smaller leaves optical breathing room.
    private static let size = NSSize(width: 18, height: 18)

    static func image(for status: SidecarStatus) -> NSImage {
        let image = NSImage(size: size, flipped: false) { rect in
            let badge = badge(for: status)
            drawCube(in: rect, badged: badge != nil)
            if let badge {
                draw(badge: badge, in: rect)
            }
            return true
        }

        // The whole point: macOS colours it, so it adapts to the menu bar's appearance and to
        // being highlighted rather than staying one fixed shade.
        image.isTemplate = true
        return image
    }

    private static func drawCube(in rect: NSRect, badged: Bool) {
        // Shifted up and left when badged so the dot gets its own corner. Overlapping the mark
        // instead means punching a hole through an edge, which reads as a broken cube rather than
        // a cube with a status dot.
        var inset = rect.insetBy(dx: 2, dy: 2)
        if badged {
            inset = NSRect(
                x: inset.minX,
                y: inset.minY + 2.5,
                width: inset.width - 2.5,
                height: inset.height - 2.5)
        }
        let centre = NSPoint(x: inset.midX, y: inset.midY)
        let radius = min(inset.width, inset.height) / 2

        // A cube in isometric projection is a regular hexagon with three spokes to alternating
        // vertices. Starting at 90° puts a vertex at the top, which is how the app icon sits.
        var vertices: [NSPoint] = []
        for step in 0..<6 {
            let angle = CGFloat.pi / 2 + CGFloat(step) * (CGFloat.pi / 3)
            vertices.append(NSPoint(
                x: centre.x + radius * cos(angle),
                y: centre.y + radius * sin(angle)))
        }

        let path = NSBezierPath()
        path.lineWidth = 1.4
        path.lineJoinStyle = .round
        path.lineCapStyle = .round

        path.move(to: vertices[0])
        for vertex in vertices.dropFirst() {
            path.line(to: vertex)
        }
        path.close()

        // The three visible front edges, meeting at the centre — what makes it read as a solid
        // rather than a flat hexagon.
        for index in stride(from: 1, to: 6, by: 2) {
            path.move(to: centre)
            path.line(to: vertices[index])
        }

        NSColor.black.setStroke()
        path.stroke()
    }

    /// Whether the state needs saying, and how loudly.
    ///
    /// Connected carries no badge at all: the ordinary state should look ordinary, and a permanent
    /// marker would train the eye to ignore it. Anything else gets a dot, because the reason to
    /// glance at a menu bar icon is to find out whether something needs attention.
    private static func badge(for status: SidecarStatus) -> Badge? {
        switch status {
        // Working is as ordinary as connected: a job running is the machine doing its job.
        case .connected, .working: return nil
        case .pairing: return .hollow
        case .unpaired: return .hollow
        case .unreachable, .disabledOnServer: return .solid
        case .revoked, .pairingFailed: return .solid
        }
    }

    private enum Badge {
        case hollow
        case solid
    }

    private static func draw(badge: Badge, in rect: NSRect) {
        let diameter: CGFloat = 5
        // Bottom-trailing, where a badge conventionally sits and where it overlaps least of the
        // mark.
        let dot = NSRect(
            x: rect.maxX - diameter - 1,
            y: rect.minY + 1,
            width: diameter,
            height: diameter)

        // No clearance punch: the cube has already moved aside, so the dot sits in free space
        // rather than being cut out of a stroke.
        let path = NSBezierPath(ovalIn: dot.insetBy(dx: 0.6, dy: 0.6))
        NSColor.black.set()

        switch badge {
        case .solid:
            path.fill()
        case .hollow:
            path.lineWidth = 1.4
            path.stroke()
        }
    }
}
