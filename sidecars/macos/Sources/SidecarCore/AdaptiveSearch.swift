import Foundation

/// One candidate quality the server wants measured on this machine.
///
/// The search itself belongs to the control plane: it measures the library's quality, then brackets
/// up or down depending on the result, then bisects. This machine never chooses a candidate — it
/// encodes the sample windows it is given, scores them with the commands it is given, and reports.
/// That is deliberate. A worker choosing its own candidates would be running a different search
/// from the one the library's quality was defined against.
///
/// Why it happens here at all: a quality proven by measuring one encoder means nothing on another.
/// The search has to run on the encoder that will do the real encode, which is this one.
public struct AdaptiveSearchStep: Sendable, Equatable {
    /// Echoed back with the report, so a measurement and the question that prompted it cannot drift.
    public let quality: Int
    /// One sample encode per measurement window, carrying the usual path placeholders.
    public let sampleCommands: [[String]]
    /// How to score each sample, in the same shape a finished candidate is measured with.
    public let measurement: QualityRequirement

    public init(quality: Int, sampleCommands: [[String]], measurement: QualityRequirement) {
        self.quality = quality
        self.sampleCommands = sampleCommands
        self.measurement = measurement
    }

    init?(json: [String: Any]) {
        guard
            let quality = (json["quality"] as? NSNumber)?.intValue,
            let samples = json["sampleCommands"] as? [[String]],
            let measurementJson = json["measurement"] as? [String: Any],
            let measurement = QualityRequirement(measurementJson: measurementJson)
        else { return nil }
        self.init(quality: quality, sampleCommands: samples, measurement: measurement)
    }
}

/// The server's answer to a reported measurement: measure this next, or stop and encode.
public struct AdaptiveSearchDirection: Sendable, Equatable {
    public let nextStep: AdaptiveSearchStep?
    public let selectedQuality: Int?
    /// The encode to run once the search is over.
    ///
    /// Replaces the arguments the assignment arrived with, which were built before any quality
    /// existed and name the library's value. Encoding with those would run the whole title at the
    /// baseline and throw away everything the search just measured.
    public let arguments: [String]?

    public init(nextStep: AdaptiveSearchStep?, selectedQuality: Int?, arguments: [String]?) {
        self.nextStep = nextStep
        self.selectedQuality = selectedQuality
        self.arguments = arguments
    }

    init?(json: [String: Any]) {
        self.init(
            nextStep: (json["nextStep"] as? [String: Any]).flatMap(AdaptiveSearchStep.init(json:)),
            selectedQuality: (json["selectedQuality"] as? NSNumber)?.intValue,
            arguments: json["arguments"] as? [String])
    }

    /// True once there is nothing left to measure.
    public var isComplete: Bool { nextStep == nil }
}
