import Foundation
import Testing
@testable import SidecarCore

struct IsometricCubeTests {
    private let circumradius = 7.0
    private var apothem: Double { circumradius * cos(.pi / 6) }

    @Test("a spoke to a vertex reaches the full circumradius")
    func vertices() {
        for step in 0..<6 {
            let angle = IsometricCube.vertexAngle + Double(step) * (.pi / 3)
            #expect(abs(IsometricCube.silhouetteRadius(circumradius: circumradius, angle: angle) - circumradius) < 1e-9)
        }
    }

    @Test("halfway along an edge the outline is at its closest")
    func edgeMidpoints() {
        for step in 0..<6 {
            let angle = IsometricCube.vertexAngle + (Double(step) + 0.5) * (.pi / 3)
            #expect(abs(IsometricCube.silhouetteRadius(circumradius: circumradius, angle: angle) - apothem) < 1e-9)
        }
    }

    @Test("no direction, forwards or backwards, ever reaches outside the outline")
    func neverPokesOut() {
        // The defect this replaces: a spoke drawn at the circumradius left the silhouette
        // everywhere except the six vertices, which is why the sticks clipped outside the cube as
        // it turned. Negative angles are swept too because the wrap has a sign to get wrong.
        for degrees in stride(from: -720.0, through: 720.0, by: 0.5) {
            let radius = IsometricCube.silhouetteRadius(
                circumradius: circumradius, angle: degrees * .pi / 180)
            #expect(radius <= circumradius + 1e-9)
            #expect(radius >= apothem - 1e-9)
        }
    }

    @Test("a turn is three spokes, evenly spaced, that come back round")
    func spokes() {
        let resting = IsometricCube.spokeAngles(spin: 0)
        #expect(resting.count == 3)
        for (first, second) in zip(resting, resting.dropFirst()) {
            #expect(abs((second - first) - 2 * .pi / 3) < 1e-9)
        }

        // A third of a revolution of a body diagonal is a whole one to the eye, so the mark at one
        // full spin is the mark at rest rather than something that has drifted.
        let round = IsometricCube.spokeAngles(spin: 1)
        for (rested, turned) in zip(resting, round) {
            #expect(abs(rested - turned) < 1e-9)
        }
    }

    @Test("spinning moves the spokes without changing what they reach")
    func spinningStaysInside() {
        for spin in stride(from: 0.0, to: 2.0, by: 0.01) {
            for angle in IsometricCube.spokeAngles(spin: spin) {
                let radius = IsometricCube.silhouetteRadius(circumradius: circumradius, angle: angle)
                #expect(radius <= circumradius + 1e-9)
            }
        }
    }
}

struct CubeMarkTests {
    private let centre = CGPoint(x: 9, y: 9)
    private let circumradius = 7.0

    /// Whether a point is inside the convex hexagon, allowing for arithmetic slack.
    private func isInside(_ point: CGPoint, outline: [CGPoint]) -> Bool {
        for (from, to) in zip(outline, outline.dropFirst() + [outline[0]]) {
            let edge = CGPoint(x: to.x - from.x, y: to.y - from.y)
            let toPoint = CGPoint(x: point.x - from.x, y: point.y - from.y)
            // The outline is wound anticlockwise, so an inside point is to the left of every edge.
            if edge.x * toPoint.y - edge.y * toPoint.x < -1e-9 { return false }
        }
        return true
    }

    @Test("the outline is six-sided and does not move as the cube turns")
    func outlineIsFixed() {
        let resting = CubeMark(centre: centre, circumradius: circumradius, spin: 0)
        #expect(resting.outline.count == 6)
        for spin in stride(from: 0.0, to: 1.0, by: 0.05) {
            let turned = CubeMark(centre: centre, circumradius: circumradius, spin: spin)
            #expect(turned.outline == resting.outline)
        }
    }

    @Test("every spoke ends inside the outline, at every angle it turns through")
    func spokesStayInside() {
        // What Scott saw: the sticks clipped outside the cube as it rotated, because a spoke was
        // drawn at the circumradius and the outline is only that far away at its six corners.
        for spin in stride(from: 0.0, to: 2.0, by: 0.005) {
            let mark = CubeMark(centre: centre, circumradius: circumradius, spin: spin)
            #expect(mark.spokes.count == 3)
            for end in mark.spokes {
                #expect(isInside(end, outline: mark.outline))
            }
        }
    }

    @Test("a spoke at rest still reaches its corner")
    func restingSpokesTouchVertices() {
        // Shortening them must not shrink the mark: at rest each visible edge runs corner to
        // centre exactly as it did.
        let mark = CubeMark(centre: centre, circumradius: circumradius, spin: 0)
        for end in mark.spokes {
            let reached = mark.outline.contains { corner in
                abs(corner.x - end.x) < 1e-9 && abs(corner.y - end.y) < 1e-9
            }
            #expect(reached)
        }
    }
}
