import Foundation

/// What the menu shows for machine load, which is not the same thing as the last sample.
///
/// A sample can fail for reasons that say nothing about the machine: too little time passed between
/// two readings of the kernel's tick counters, the counters wrapped, or IOKit declined to answer.
/// Writing every attempt straight through meant the meters blanked and returned several times a
/// second while a job ran, which reads as the Mac losing track of itself rather than as one missed
/// reading. A figure that could not be taken leaves the last one standing; only the work stopping
/// clears them, because that is the only moment they stop being true.
public struct MachineLoadDisplay: Sendable, Equatable {
    /// Fraction of the last measurable interval the machine spent busy, 0 to 1.
    public private(set) var cpu: Double?
    /// The accelerator's busy figure, 0 to 1.
    public private(set) var gpu: Double?

    public init() {}

    public mutating func observe(_ load: MachineLoad?) {
        if let cpu = load?.cpu { self.cpu = cpu }
        if let gpu = load?.gpu { self.gpu = gpu }
    }

    /// Nothing is running, so there is no figure to stand by.
    public mutating func clear() {
        cpu = nil
        gpu = nil
    }

    public var isEmpty: Bool { cpu == nil && gpu == nil }
}
