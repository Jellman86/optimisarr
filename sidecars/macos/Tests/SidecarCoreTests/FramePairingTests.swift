import Foundation
import Testing
@testable import SidecarCore

@Suite("Frame windows inside the counted source")
struct FramePairingTests {
    private func command(_ filter: String) -> [String] {
        ["-nostdin", "-lavfi", filter, "-f", "null", "-"]
    }

    @Test("a window ending within the counted frames fits")
    func inside() {
        #expect(FramePairing.fitsWithin(command("[0:v]trim=start_frame=33358:end_frame=34379[d]"), frames: 37635))
        #expect(FramePairing.fitsWithin(command("[0:v]trim=start_frame=37635:end_frame=37635[d]"), frames: 37635))
    }

    @Test("a window past the last frame of a variable-frame-rate source does not fit")
    func outside() {
        #expect(!FramePairing.fitsWithin(command("[0:v]trim=start_frame=39171:end_frame=40370[d]"), frames: 37635))
    }

    @Test("a complete-file comparison has no window and always fits")
    func complete() {
        #expect(FramePairing.fitsWithin(command("[0:v]settb=AVTB,setpts=N*41708[d]"), frames: 10))
    }
}
