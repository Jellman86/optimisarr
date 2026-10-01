import Foundation
import Testing
@testable import SidecarCore

@Suite("Actual candidate format for VMAF v1")
struct CandidateVideoFormatTests {
    let command = ["-lavfi", "cambi.enc_width={{encodedWidth}}:cambi.enc_height={{encodedHeight}}:cambi.enc_bitdepth={{encodedBitDepth}}"]

    @Test(arguments: ["yuv420p", "yuv420p10le"])
    func actualGeometryAndDepth(pixel: String) {
        let json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"\(pixel)\",\"bits_per_raw_sample\":\"0\"}]}"
        let resolved = CandidateVideoFormat.resolve(command, probeJSON: json)
        #expect(resolved?[1].contains("enc_width=1280:cam") == true)
        #expect(resolved?[1].contains("enc_bitdepth=\(pixel == "yuv420p" ? 8 : 10)") == true)
        #expect(resolved?[1].contains("{{") == false)
    }

    @Test(arguments: ["{}", "not json", "{\"streams\":[{\"codec_type\":\"video\",\"width\":0,\"height\":720,\"pix_fmt\":\"yuv420p\"}]}",
        "{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"yuv420p12le\"}]}",
        "{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"yuv420p\",\"bits_per_raw_sample\":\"10\"}]}"])
    func rejectsUnscorableFormat(json: String) {
        #expect(CandidateVideoFormat.resolve(command, probeJSON: json) == nil)
    }

    @Test(arguments: ["invalid", "-1", "8.5"])
    func rejectsInvalidExplicitDepth(raw: String) {
        let json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"yuv420p\",\"bits_per_raw_sample\":\"\(raw)\"}]}"
        #expect(CandidateVideoFormat.resolve(command, probeJSON: json) == nil)
    }

    @Test func coverArtIsNotTheMovingPicture() {
        let json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":100,\"height\":100,\"pix_fmt\":\"yuv420p\",\"disposition\":{\"attached_pic\":1}},{\"codec_type\":\"video\",\"width\":1280,\"height\":720,\"pix_fmt\":\"yuv420p\"}]}"
        #expect(CandidateVideoFormat.resolve(command, probeJSON: json)?[1].contains("enc_width=1280") == true)
        #expect(CandidateVideoFormat.resolve(["legacy"], probeJSON: nil) == ["legacy"])
    }
}
