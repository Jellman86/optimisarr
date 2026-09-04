import Foundation

/// Why an assignment's command was refused. Each names the offending token so an operator reading
/// the log can see what the server sent, not merely that it was rejected.
public enum AssignmentCommandError: Error, Equatable, Sendable {
    case empty
    case unknownOption(String)
    case optionWithoutValue(String)
    case inputMustBePlaceholder(String)
    case exactlyOneInputRequired(Int)
    case outputMustBeLast(expected: String)
    case invalidOutputExtension(String)
    case pathLikeValue(String)
    case strayPlaceholder(String)
}

/// The server's argument array, checked before this machine will run it.
///
/// The server is trusted to decide *what* to encode; it is not trusted to name *files* on this
/// machine. A compromised or merely buggy server could otherwise point FFmpeg at anything a
/// worker's user can read, or write over anything they can write. So the command must satisfy a
/// small, explicit contract: every option is one the server's own command builder is known to
/// emit, the only input is the `{{input}}` token, the only output is the `{{output}}` token in
/// last position carrying the promised extension, and no other value looks like a path. The
/// worker then substitutes only its own scratch paths. Anything outside that contract is refused
/// whole rather than repaired, because a repaired command is one nobody wrote.
public struct AssignmentCommand: Sendable, Equatable {
    /// Options that take no value.
    static let flags: Set<String> = ["-y", "-nostats"]

    /// Options that take exactly one value. Drawn from `FfmpegCommandBuilder` and
    /// `EncoderTuningPolicy`; an option the server starts emitting must be added here, which is
    /// deliberate — the failure is a refused job with a named token, never a silently run one.
    /// Device and hardware-decode options are absent on purpose: a remote command decodes in
    /// software, and the server never sends them to a worker.
    static let valued: Set<String> = [
        "-progress", "-threads", "-fflags", "-i", "-map", "-map_metadata", "-metadata",
        "-disposition:v:0", "-c", "-c:v", "-c:v:0", "-c:a", "-c:s", "-filter:v:0", "-vf",
        "-crf", "-preset", "-q:v", "-qp", "-cq", "-rc", "-b:v", "-b:a", "-ac",
        "-global_quality", "-rc_mode", "-quality", "-lossless",
        "-tune", "-maxrate", "-minrate", "-bufsize", "-x264-params", "-x265-params",
        "-spatial-aq", "-temporal-aq", "-fps_mode", "-enc_time_base:v:0",
        "-ss", "-t", "-movflags",
    ]

    public let arguments: [String]
    public let outputExtension: String

    /// Checks the array against the contract above. Throws rather than returning a partial
    /// result, so a caller cannot forget to look.
    public static func validate(_ arguments: [String], outputExtension: String) throws -> AssignmentCommand {
        guard let last = arguments.last else { throw AssignmentCommandError.empty }

        guard Self.isPlainExtension(outputExtension) else {
            throw AssignmentCommandError.invalidOutputExtension(outputExtension)
        }
        let expectedOutput = "\(Assignment.outputPlaceholder).\(outputExtension)"
        guard last == expectedOutput else {
            throw AssignmentCommandError.outputMustBeLast(expected: expectedOutput)
        }

        var index = 0
        var inputs = 0
        let body = arguments.dropLast()
        while index < body.count {
            let token = body[index]
            if flags.contains(token) {
                index += 1
                continue
            }
            guard valued.contains(token) else {
                throw AssignmentCommandError.unknownOption(token)
            }
            guard index + 1 < body.count else {
                throw AssignmentCommandError.optionWithoutValue(token)
            }
            let value = body[index + 1]
            if token == "-i" {
                guard value == Assignment.inputPlaceholder else {
                    throw AssignmentCommandError.inputMustBePlaceholder(value)
                }
                inputs += 1
            } else {
                try Self.checkValue(value)
            }
            index += 2
        }

        guard inputs == 1 else { throw AssignmentCommandError.exactlyOneInputRequired(inputs) }
        return AssignmentCommand(arguments: arguments, outputExtension: outputExtension)
    }

    /// Substitutes this machine's paths for the two tokens. The output must already carry the
    /// promised extension: the extension chooses the muxer and the subtitle codec, so it is part
    /// of the contract rather than a worker preference.
    public func materialise(input: URL, output: URL) -> [String] {
        precondition(output.pathExtension == outputExtension, "output must carry the contract's extension")
        var result = arguments
        result[result.count - 1] = output.path
        for index in result.indices where result[index] == Assignment.inputPlaceholder {
            result[index] = input.path
        }
        return result
    }

    private static func checkValue(_ value: String) throws {
        if value.contains(Assignment.inputPlaceholder) || value.contains(Assignment.outputPlaceholder) {
            throw AssignmentCommandError.strayPlaceholder(value)
        }
        // A filter chain, a metadata marker or a rate control value never needs a path
        // separator or a home shorthand; an argument that has one is trying to name a file.
        if value.contains("/") || value.contains("\\") || value.hasPrefix("~") {
            throw AssignmentCommandError.pathLikeValue(value)
        }
    }

    private static func isPlainExtension(_ extension: String) -> Bool {
        !`extension`.isEmpty
            && `extension`.count <= 8
            && `extension`.allSatisfy { $0.isASCII && ($0.isLetter || $0.isNumber) }
    }
}
