import Foundation
import Testing
@testable import SidecarCore

@Suite("What a hand-back tells the server")
struct HandbackFailureTests {
    @Test("a short report is sent as it is, trimmed")
    func short() {
        #expect(SidecarClient.boundedFailure("  FFmpeg exited with code 234.\nConversion failed!\n") ==
            "FFmpeg exited with code 234.\nConversion failed!")
    }

    @Test("a long report keeps its end, where FFmpeg explains itself")
    func long() {
        let report = String(repeating: "frame=1 ", count: 2000) + "Non-monotonic DTS; Error submitting a packet to the muxer"
        let bounded = SidecarClient.boundedFailure(report)
        #expect(bounded.count == SidecarClient.maximumFailureLength)
        #expect(bounded.hasSuffix("Error submitting a packet to the muxer"))
    }
}
