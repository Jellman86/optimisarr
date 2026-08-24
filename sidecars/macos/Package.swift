// swift-tools-version: 6.0
import PackageDescription

// A Swift package rather than an .xcodeproj so the whole build is reviewable in a diff.
// The protocol client lives in its own library target with no AppKit or SwiftUI dependency,
// which is what lets the contract be tested without launching a menu-bar app.
let package = Package(
    name: "OptimisarrSidecar",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "SidecarCore", targets: ["SidecarCore"]),
        .executable(name: "OptimisarrSidecar", targets: ["OptimisarrSidecar"]),
    ],
    targets: [
        .target(name: "SidecarCore"),
        .executableTarget(name: "OptimisarrSidecar", dependencies: ["SidecarCore"]),
        .testTarget(name: "SidecarCoreTests", dependencies: ["SidecarCore"]),
    ]
)
