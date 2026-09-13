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

    @Test("every encoder must be proved, CPU ones included")
    func confirmationPolicy() {
        // ffmpeg lists what it was compiled with. Every Apple build lists VideoToolbox whether or
        // not this machine can open it, and a bundled libx265 that segfaults on its first frame
        // (seen 2026-09-13) is listed just the same. Trusting the listing for CPU encoders let the
        // sidecar advertise an encoder that could never finish a job.
        #expect(EncoderListParser.needsConfirmation("hevc_videotoolbox"))
        #expect(EncoderListParser.needsConfirmation("h264_videotoolbox"))
        #expect(EncoderListParser.needsConfirmation("libx265"))
        #expect(EncoderListParser.needsConfirmation("libx264"))
        #expect(EncoderListParser.needsConfirmation("libsvtav1"))
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

@Suite("Audio encoder listing")
struct AudioEncoderListingTests {
    private let listing = """
     Encoders:
      V..... = Video
      ------
     A....D aac                  AAC (Advanced Audio Coding)
     A....D ac3                  ATSC A/52A AC-3
     A....D pcm_s16le            PCM signed 16-bit little-endian
     V....D libx265              libx265 H.265 / HEVC
    """

    @Test("only the encoders the server can ask for are reported")
    func picksTheServersTargets() {
        // Optimisarr targets AAC, Opus or MP3 and emits them as aac, libopus and libmp3lame.
        // Reporting the hundred other things FFmpeg lists would be noise the server cannot use.
        let found = AudioEncoderListParser.parse(listing)

        #expect(found.contains("aac"))
        #expect(found.contains("ac3"))
        #expect(!found.contains("pcm_s16le"))
        #expect(!found.contains("libx265"))
    }

    @Test("the bundled build's missing encoders are simply absent")
    func absentEncodersAreNotInvented() {
        // The bundled FFmpeg links no external audio libraries, so it has neither libopus nor
        // libmp3lame. The library form offers Opus and MP3 regardless, and the server decides
        // whether to offer the job — but only if this list is honest.
        let found = AudioEncoderListParser.parse(listing)

        #expect(!found.contains("libopus"))
        #expect(!found.contains("libmp3lame"))
    }

    @Test("the probe encodes real silence rather than trusting the listing")
    func probeShape() {
        let arguments = AudioEncoderProbeCommand.arguments(for: "libopus")

        #expect(arguments.contains("anullsrc=r=48000:cl=stereo:d=0.2"))
        #expect(arguments[arguments.firstIndex(of: "-c:a")! + 1] == "libopus")
        #expect(arguments.last == "-")
    }
}
