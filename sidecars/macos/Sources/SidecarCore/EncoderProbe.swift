import Foundation

/// Parses `ffmpeg -encoders` output.
///
/// Listing is only the cheap first pass. The server's `HardwareCapabilityService` takes the same
/// two-stage approach — parse, then confirm each hardware encoder with a real test encode — because
/// ffmpeg lists what it was compiled with, not what this machine can actually open. A VideoToolbox
/// encoder is present in every build for Apple platforms and can still fail at runtime, which is
/// exactly why the roadmap says to probe rather than assume.
public enum EncoderListParser {
    /// Encoders worth reporting. Deliberately narrow: advertising an encoder the server would never
    /// ask for only invites work this sidecar has no better claim to than the container.
    static let interesting: Set<String> = [
        "libx264", "libx265", "libsvtav1",
        "h264_videotoolbox", "hevc_videotoolbox",
    ]

    /// `ffmpeg -encoders` prints a header, a separator line of dashes, then one row per encoder:
    ///
    ///     V....D hevc_videotoolbox    VideoToolbox H.265 Encoder
    ///
    /// The leading flags column starts with the media type, so video encoders begin with `V`.
    public static func parse(_ output: String) -> [String] {
        var found: [String] = []

        for line in output.split(separator: "\n", omittingEmptySubsequences: true) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)

            // Skip the header block; rows only start after the dashed separator, and every real row
            // carries a flags column followed by the name.
            let parts = trimmed.split(separator: " ", maxSplits: 2, omittingEmptySubsequences: true)
            guard parts.count >= 2 else { continue }

            let flags = String(parts[0])
            let name = String(parts[1])

            guard flags.hasPrefix("V"), interesting.contains(name), !found.contains(name) else {
                continue
            }
            found.append(name)
        }

        return found
    }

    /// True when the encoder runs on the GPU and therefore needs proving rather than trusting.
    /// CPU encoders are taken from the listing, matching how the server treats them.
    public static func needsConfirmation(_ encoder: String) -> Bool {
        encoder.hasSuffix("_videotoolbox")
    }
}

/// Builds the throwaway encode that proves an encoder actually opens on this machine.
///
/// Mirrors the server's `EncoderProbeCommand`: a few frames of a synthetic source to the null
/// muxer, at a resolution large enough to clear encoder minimums rather than a thumbnail. A clean
/// exit means the encoder opened and produced packets.
public enum EncoderProbeCommand {
    public static func arguments(for encoder: String) -> [String] {
        [
            "-hide_banner", "-v", "error",
            "-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.2",
            "-frames:v", "3",
            "-c:v", encoder,
            "-f", "null", "-",
        ]
    }
}

/// Parses `ffmpeg -filters` to decide whether VMAF can be measured.
///
/// Apple GPUs have no VMAF compute backend, so the only honest answer here is CPU or nothing —
/// there is no arrangement of Apple hardware that yields `Cuda`.
public enum VmafSupportParser {
    public static func parse(_ filtersOutput: String) -> VmafCapability {
        filtersOutput.contains("libvmaf") ? .cpu : .none
    }
}

/// Parses `ffmpeg -hwaccels`.
///
/// On Apple the only entry that matters is `videotoolbox`, which covers hardware decode. Unlike an
/// encoder there is no per-codec decoder name to advertise — decode is requested as
/// `-hwaccel videotoolbox` — so that is the name reported, and it is reported only once a real
/// decode has been shown to work.
public enum HardwareAcceleratorParser {
    public static func parse(_ output: String) -> [String] {
        output
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { $0 == "videotoolbox" }
    }
}

/// Builds the round trip that proves hardware decode actually works.
///
/// Listing `videotoolbox` under `-hwaccels` only says ffmpeg was compiled for it. Proving it needs
/// something real to decode, so a short clip is encoded first and then decoded back with the
/// accelerator engaged. Both halves have to succeed, which is stricter than either alone.
public enum HardwareDecodeProbeCommand {
    /// Encodes a throwaway clip to a file, using the hardware encoder already proved.
    public static func encodeArguments(to path: String, using encoder: String) -> [String] {
        [
            "-hide_banner", "-v", "error", "-y",
            "-f", "lavfi", "-i", "testsrc=s=320x240:r=25:d=1",
            "-c:v", encoder,
            path,
        ]
    }

    /// Decodes it back with the accelerator engaged, to the null muxer.
    public static func decodeArguments(from path: String) -> [String] {
        [
            "-hide_banner", "-v", "error",
            "-hwaccel", "videotoolbox",
            "-i", path,
            "-f", "null", "-",
        ]
    }
}
