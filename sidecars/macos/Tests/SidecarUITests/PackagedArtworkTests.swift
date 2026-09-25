import AppKit
import Foundation
import SidecarCore
import Testing
@testable import OptimisarrSidecar

@MainActor
struct PackagedArtworkTests {
    @Test func packagedArtworkSupportsSwiftPMAndXcodeResourceLayouts() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        for suffix in ["", "Contents/Resources"] {
            let app = root.appendingPathComponent(UUID().uuidString + ".app")
            let resources = app.appendingPathComponent("Contents/Resources")
            let folder = resources.appendingPathComponent("OptimisarrSidecar_OptimisarrSidecar.bundle").appendingPathComponent(suffix)
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let expected = folder.appendingPathComponent("BrandMark.png")
            try Data([1]).write(to: expected)
            var usedDevelopmentResource = false
            let actual = MenuBarIcon.resourceURL(named: "BrandMark", applicationURL: app, resourceDirectory: resources) {
                usedDevelopmentResource = true
                return nil
            }
            #expect(actual == expected)
            #expect(!usedDevelopmentResource)
        }
    }

    @Test func packagedAppNeverFallsBackToBuildTreeWhenArtworkIsMissing() {
        var usedDevelopmentResource = false
        let app = URL(fileURLWithPath: "/missing/OptimisarrSidecar.app")
        let actual = MenuBarIcon.resourceURL(named: "BrandMark", applicationURL: app,
                                            resourceDirectory: app.appendingPathComponent("Contents/Resources")) {
            usedDevelopmentResource = true
            return URL(fileURLWithPath: "/build-tree/BrandMark.png")
        }
        #expect(actual == nil)
        #expect(!usedDevelopmentResource)
    }

    @Test func packagedArtworkCannotUseASymlinkIntoTheBuildTree() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("OptimisarrSidecar.app")
        let resources = app.appendingPathComponent("Contents/Resources")
        let folder = resources.appendingPathComponent("OptimisarrSidecar_OptimisarrSidecar.bundle")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let external = root.appendingPathComponent("build-tree.png")
        try Data([1]).write(to: external)
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("BrandMark.png"), withDestinationURL: external)
        #expect(MenuBarIcon.resourceURL(named: "BrandMark", applicationURL: app, resourceDirectory: resources) { external } == nil)
    }

    @Test func bareExecutableUsesDevelopmentResources() {
        let expected = URL(fileURLWithPath: "/build-tree/BrandMark.png")
        #expect(MenuBarIcon.resourceURL(named: "BrandMark", applicationURL: URL(fileURLWithPath: "/build-tree"),
                                       resourceDirectory: nil) { expected } == expected)
    }

    @Test func menuBarFramesOnlyAdvanceWhileWorkingAndMotionIsAllowed() {
        let connected = SidecarStatus.connected(workerId: 1, lastCheckIn: .now)
        let working = SidecarStatus.working(jobId: 9, progress: .encoding(encodedSeconds: 12))
        #expect(MenuBarIcon.frameIndex(for: connected, spin: 0.35, reduceMotion: false) == nil)
        #expect(MenuBarIcon.frameIndex(for: working, spin: 0.35, reduceMotion: false) == 30)
        #expect(MenuBarIcon.frameIndex(for: working, spin: 1.35, reduceMotion: false) == 30)
        #expect(MenuBarIcon.frameIndex(for: working, spin: 0.35, reduceMotion: true) == nil)
        #expect(MenuBarIcon.frameIndex(for: working, spin: 8.7 / 8.8, reduceMotion: false) == 87)
    }

    @Test func motionAtlasHasAllFramesForBothAppearances() {
        for artwork in [MenuBarIcon.motionArtwork, MenuBarIcon.lightMotionArtwork] {
            #expect(artwork.size == NSSize(width: 528, height: 384))
        }
    }

    @Test func activeSliceFrameRendersDifferentlyFromTheClosedCube() {
        let idle = MenuBarIcon.darkFrames[0]
        let twisted = MenuBarIcon.darkFrames[32]
        #expect(idle.size == NSSize(width: 18, height: 18))
        #expect(twisted.size == idle.size)
        #expect(twisted.tiffRepresentation != idle.tiffRepresentation)
    }
}
