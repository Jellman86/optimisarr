import CoreGraphics
import Foundation

/// The geometry of the wireframe cube the app draws as its mark.
///
/// A cube seen down its body diagonal projects to a regular hexagon with three edges running from
/// the centre to alternating vertices. Rotating the cube about that diagonal leaves the hexagon
/// exactly where it is and sweeps the three edges round inside it, which is what lets the menu bar
/// show work happening without swapping in a different icon.
///
/// The arithmetic lives here rather than in the drawing code because it is the part that can be
/// wrong: a spoke drawn at the circumradius is correct only at the six vertex angles and pokes out
/// through the silhouette everywhere between, by up to fifteen per cent at the edge midpoints.
public enum IsometricCube {
    /// The angle of the first vertex. Straight up, so the cube sits as it does in the app icon.
    public static let vertexAngle = Double.pi / 2

    /// How far the hexagonal silhouette is from the centre along a given direction.
    ///
    /// Equal to `circumradius` at each of the six vertices and to the apothem
    /// (`circumradius * cos(30°)`) at the edge midpoints, tracing the straight edges between.
    /// A spoke drawn to this length ends on the outline rather than through it.
    public static func silhouetteRadius(circumradius: Double, angle: Double) -> Double {
        let sector = Double.pi / 3
        let fromVertex = (angle - vertexAngle).truncatingRemainder(dividingBy: sector)
        // Swift's remainder keeps the sign of the dividend, so a direction below the first vertex
        // would otherwise land in a negative sector and read off the wrong edge.
        let wrapped = fromVertex < 0 ? fromVertex + sector : fromVertex
        return circumradius * cos(sector / 2) / cos(wrapped - sector / 2)
    }

    /// Where the three visible edges end for a given amount of turn, in turns of the cube.
    ///
    /// Three-fold symmetry about the body diagonal means a third of a revolution is a whole one as
    /// far as anyone watching is concerned, so the spin repeats every turn of this value.
    public static func spokeAngles(spin: Double) -> [Double] {
        let sweep = spin.truncatingRemainder(dividingBy: 1) * (2 * .pi / 3)
        return stride(from: 1, to: 6, by: 2).map { index in
            vertexAngle + Double(index) * (.pi / 3) + sweep
        }
    }
}

/// The mark itself: the six-sided outline and the three edges inside it, as points.
///
/// Points rather than a drawn path so the whole mark, not just one length, can be checked without
/// AppKit — the same reason the rest of this module has no window in it.
public struct CubeMark: Sendable, Equatable {
    public let outline: [CGPoint]
    public let spokes: [CGPoint]
    public let centre: CGPoint

    /// - Parameter spin: turns of the cube about its body diagonal. The outline never moves.
    public init(centre: CGPoint, circumradius: Double, spin: Double = 0) {
        self.centre = centre
        outline = (0..<6).map { step in
            CubeMark.point(
                from: centre,
                angle: IsometricCube.vertexAngle + Double(step) * (.pi / 3),
                radius: circumradius)
        }
        spokes = IsometricCube.spokeAngles(spin: spin).map { angle in
            CubeMark.point(
                from: centre,
                angle: angle,
                radius: IsometricCube.silhouetteRadius(circumradius: circumradius, angle: angle))
        }
    }

    public static func point(from centre: CGPoint, angle: Double, radius: Double) -> CGPoint {
        CGPoint(x: centre.x + radius * cos(angle), y: centre.y + radius * sin(angle))
    }
}
