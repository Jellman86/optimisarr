import Foundation
import Testing
@testable import SidecarCore

/// Real `ffmpeg -encoders` output from an Apple Silicon build, trimmed to the rows that matter plus
/// enough noise to prove the parser is not just matching substrings anywhere in the text.
private let encoderListing = """
Encoders:
 V..... = Video
 A..... = Audio
 S..... = Subtitle
 ------
 V....D h264_videotoolbox    VideoToolbox H.264 Encoder (codec h264)
 V....D hevc_videotoolbox    VideoToolbox H.265 Encoder (codec hevc)
 V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC
 V....D libx265              libx265 H.265 / HEVC
 A....D aac                  AAC (Advanced Audio Coding)
 S..... mov_text             3GPP Timed Text subtitle
 V....D wrapped_avframe      AVFrame to AVPacket passthrough
"""

@Suite("Encoder listing")
struct EncoderListParserTests {
    @Test("finds the video encoders worth advertising")
    func findsEncoders() {
        let found = EncoderListParser.parse(encoderListing)

        #expect(found.contains("hevc_videotoolbox"))
        #expect(found.contains("h264_videotoolbox"))
        #expect(found.contains("libx264"))
        #expect(found.contains("libx265"))
    }

    @Test("ignores audio and subtitle encoders")
    func ignoresNonVideo() {
        let found = EncoderListParser.parse(encoderListing)

        #expect(!found.contains("aac"))
        #expect(!found.contains("mov_text"))
    }

    @Test("ignores video encoders that are not worth offering")
    func ignoresUninteresting() {
        // Advertising everything ffmpeg can emit would invite work this sidecar has no better claim
        // to than the container already running it.
        #expect(!EncoderListParser.parse(encoderListing).contains("wrapped_avframe"))
    }

    @Test("returns nothing for empty or unparseable output")
    func handlesJunk() {
        #expect(EncoderListParser.parse("").isEmpty)
        #expect(EncoderListParser.parse("ffmpeg: command not found").isEmpty)
    }

    @Test("hardware encoders must be proved, CPU encoders may be trusted")
    func confirmationPolicy() {
        // The distinction that matters: ffmpeg lists what it was compiled with, and every Apple
        // build lists VideoToolbox whether or not this machine can actually open it.
        #expect(EncoderListParser.needsConfirmation("hevc_videotoolbox"))
        #expect(EncoderListParser.needsConfirmation("h264_videotoolbox"))
        #expect(!EncoderListParser.needsConfirmation("libx265"))
        #expect(!EncoderListParser.needsConfirmation("libx264"))
    }
}

@Suite("Encoder probe command")
struct EncoderProbeCommandTests {
    @Test("encodes a few frames of a synthetic source to null")
    func probeShape() {
        let args = EncoderProbeCommand.arguments(for: "hevc_videotoolbox")

        #expect(args.contains("lavfi"))
        #expect(args.contains("-frames:v"))
        #expect(args.contains("hevc_videotoolbox"))
        // Null muxer: the probe must prove the encoder opens without leaving a file behind.
        #expect(args.contains("null"))
    }

    @Test("uses a resolution that clears encoder minimums")
    func probeResolution() {
        // A thumbnail-sized probe is rejected by some encoders, which would report a working
        // encoder as broken. The server's probe uses the same 320x240 for this reason.
        let args = EncoderProbeCommand.arguments(for: "hevc_videotoolbox").joined(separator: " ")

        #expect(args.contains("320x240"))
    }
}

@Suite("VMAF support")
struct VmafSupportParserTests {
    @Test("reports CPU when libvmaf is present")
    func detectsVmaf() {
        let filters = " ... T.. libvmaf           VV->V     Calculate the VMAF between two video streams."
        #expect(VmafSupportParser.parse(filters) == .cpu)
    }

    @Test("reports none when libvmaf is absent")
    func absentVmaf() {
        #expect(VmafSupportParser.parse(" ... T.. scale  V->V  Scale the input video size.") == .none)
    }

    @Test("never reports CUDA, because Apple GPUs have no VMAF backend")
    func neverCuda() {
        // Guards the honesty of the capability rather than the parser. Claiming CUDA here would
        // have the server schedule work on a measurement path that cannot exist on this hardware.
        #expect(VmafSupportParser.parse("libvmaf libvmaf_cuda") != .cuda)
    }
}
