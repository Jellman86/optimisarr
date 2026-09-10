import Foundation

/// Why a measurement command was refused, naming the token so the log says what the server sent.
public enum MeasurementCommandError: Error, Equatable, Sendable {
    case empty
    case unknownOption(String)
    case optionWithoutValue(String)
    case inputsMustBePlaceholders([String])
    case mustEndWithNullOutput
    case logPlaceholderMissing
    case pathLikeValue(String)
    case strayPlaceholder(String)
}

/// The server's libvmaf command, checked before this machine will run it.
///
/// Same contract as `AssignmentCommand`, for the same reason: the server decides what to measure,
/// never which files on this machine to read. The two inputs must be the `{{distorted}}` and
/// `{{reference}}` tokens in that order (libvmaf wants distorted first), the filter must name the
/// `{{log}}` token exactly once, the command must end in the null muxer so nothing is written but
/// the log, and no other value may look like a path.
public struct MeasurementCommand: Sendable, Equatable {
    public static let distortedPlaceholder = "{{distorted}}"
    public static let referencePlaceholder = "{{reference}}"
    public static let logPlaceholder = "{{log}}"

    static let flags: Set<String> = ["-nostdin", "-stats", "-y", "-nostats"]
    /// From `QualityScoreCommandBuilder`'s CPU path. Device and hardware-decode options are absent
    /// on purpose: a worker's measurement decodes in software, and the server never sends them.
    static let valued: Set<String> = ["-v", "-ss", "-t", "-i", "-lavfi", "-f", "-threads"]

    public let arguments: [String]

    public static func validate(_ arguments: [String]) throws -> MeasurementCommand {
        guard arguments.count >= 3 else { throw MeasurementCommandError.empty }
        guard arguments.suffix(3) == ["-f", "null", "-"] else {
            throw MeasurementCommandError.mustEndWithNullOutput
        }

        var index = 0
        var inputs: [String] = []
        var logPlaceholders = 0
        let body = arguments.dropLast(3)
        while index < body.count {
            let token = body[index]
            if flags.contains(token) {
                index += 1
                continue
            }
            guard valued.contains(token) else { throw MeasurementCommandError.unknownOption(token) }
            guard index + 1 < body.count else { throw MeasurementCommandError.optionWithoutValue(token) }
            let value = body[index + 1]
            switch token {
            case "-i":
                inputs.append(value)
            case "-lavfi":
                logPlaceholders += value.components(separatedBy: logPlaceholder).count - 1
                try checkValue(value, allowingLog: true)
            default:
                try checkValue(value, allowingLog: false)
            }
            index += 2
        }

        guard inputs == [distortedPlaceholder, referencePlaceholder] else {
            throw MeasurementCommandError.inputsMustBePlaceholders(inputs)
        }
        guard logPlaceholders == 1 else { throw MeasurementCommandError.logPlaceholderMissing }
        return MeasurementCommand(arguments: arguments)
    }

    /// Substitutes this machine's paths for the three tokens.
    public func materialise(distorted: URL, reference: URL, log: URL) -> [String] {
        arguments.map { argument in
            switch argument {
            case Self.distortedPlaceholder: return distorted.path
            case Self.referencePlaceholder: return reference.path
            default: return argument.replacingOccurrences(of: Self.logPlaceholder, with: log.path)
            }
        }
    }

    private static func checkValue(_ value: String, allowingLog: Bool) throws {
        if value.contains(distortedPlaceholder) || value.contains(referencePlaceholder)
            || (!allowingLog && value.contains(logPlaceholder)) {
            throw MeasurementCommandError.strayPlaceholder(value)
        }
        if value.contains("/") || value.hasPrefix("~") || AssignmentCommand.hasPathLikeBackslash(value) {
            throw MeasurementCommandError.pathLikeValue(value)
        }
    }
}
