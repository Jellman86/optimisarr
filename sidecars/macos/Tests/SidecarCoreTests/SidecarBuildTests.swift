import Testing
@testable import SidecarCore

@Suite("Reported build")
struct SidecarBuildTests {
    @Test("names the version and the build, because two builds can share a version")
    func versionAndBuild() {
        #expect(SidecarBuild.describe([
            "CFBundleShortVersionString": "0.1.6",
            "CFBundleVersion": "202609141130",
        ]) == "0.1.6 (202609141130)")
    }

    @Test("does not repeat itself when the two are the same")
    func identical() {
        #expect(SidecarBuild.describe([
            "CFBundleShortVersionString": "0.1.6",
            "CFBundleVersion": "0.1.6",
        ]) == "0.1.6")
    }

    @Test("falls back to whichever half it has")
    func onlyOne() {
        #expect(SidecarBuild.describe(["CFBundleShortVersionString": "0.1.6"]) == "0.1.6")
        #expect(SidecarBuild.describe(["CFBundleVersion": "202609141130"]) == "202609141130")
    }

    @Test("says so rather than inventing a number when there is no bundle to read")
    func noBundle() {
        // The test suite and the bare SwiftPM executable both land here. An operator reads this
        // field to decide whether to trust what a machine is running, so a made-up version would
        // be worse than an honest absence.
        #expect(SidecarBuild.describe(nil) == "unknown")
        #expect(SidecarBuild.describe([:]) == "unknown")
        #expect(SidecarBuild.describe([
            "CFBundleShortVersionString": "  ",
            "CFBundleVersion": "",
        ]) == "unknown")
    }
}
