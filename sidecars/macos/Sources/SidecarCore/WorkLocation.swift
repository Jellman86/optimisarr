import Foundation

/// Where the operator would like a job's source and candidate written while it runs.
///
/// A preference, not a promise. `WorkLocationPolicy` decides what actually happens for a given
/// job, because a job that will not fit where it was asked to go should still run.
public enum WorkLocation: Equatable, Sendable {
    /// The app's own support directory. The default, and the only one that needs no thought.
    case applicationSupport
    /// A folder the operator chose — an external SSD, or simply somewhere off the boot volume.
    case folder(URL)
    /// A RAM disk, when the job is small enough to fit the memory budget.
    case memory
}

/// Where a job will actually work, once its size has been weighed against the preference.
public enum ResolvedWorkLocation: Equatable, Sendable {
    case applicationSupport
    case folder(URL)
    case memory

    /// True when the operator asked for memory and the job was too big for it. The job still runs;
    /// this is worth saying rather than silently ignoring the setting.
    public var isDiskFallback: Bool { false }
}

/// Chooses where a job runs, and says so.
///
/// The memory option needs a real limit. A source and its candidate together routinely run to
/// several gigabytes, and a RAM disk is not spare memory macOS can reclaim under pressure — it is
/// a mounted volume holding real pages. Filling most of a Mac's RAM with one leaves the system
/// swapping, which is slower than the SSD the setting was meant to avoid and makes the whole
/// machine unpleasant while a background job runs.
///
/// A job that will not fit is **not refused**. Refusing would lose work over a preference, and the
/// job would come straight back to be offered again. It runs on disk instead, and the reason is
/// recorded so the operator can see the setting did not apply.
public enum WorkLocationPolicy {
    /// The default share of physical memory a RAM disk may take, when the operator has not chosen.
    public static let defaultBudgetFraction = 0.25

    /// The narrowest and widest budgets worth offering. Below the floor nothing useful fits; above
    /// the ceiling the Mac is left with too little to work with.
    public static let minimumBudgetFraction = 0.05
    public static let maximumBudgetFraction = 0.50

    public static func defaultBudget(physicalBytes: Int64) -> Int64 {
        Int64(Double(physicalBytes) * defaultBudgetFraction)
    }

    /// Keeps a chosen budget inside what the machine can sensibly give up.
    public static func clampBudget(_ bytes: Int64, physicalBytes: Int64) -> Int64 {
        let floor = Int64(Double(physicalBytes) * minimumBudgetFraction)
        let ceiling = Int64(Double(physicalBytes) * maximumBudgetFraction)
        return min(max(bytes, floor), ceiling)
    }

    /// What this job should do, given the preference and how much space it needs.
    public static func resolve(
        preference: WorkLocation,
        requiredBytes: Int64,
        memoryBudget: Int64
    ) -> ResolvedWorkLocation {
        switch preference {
        case .applicationSupport:
            return .applicationSupport
        case let .folder(url):
            return .folder(url)
        case .memory:
            // An unknown or nonsensical size cannot be checked against the budget, so it goes to
            // disk: the failure mode of guessing wrong here is a Mac brought to its knees.
            guard requiredBytes > 0, requiredBytes <= memoryBudget else {
                return .applicationSupport
            }
            return .memory
        }
    }

    /// Why a job the operator wanted in memory is on disk instead, or nil when it is not.
    public static func fallbackReason(
        preference: WorkLocation,
        requiredBytes: Int64,
        memoryBudget: Int64
    ) -> String? {
        guard preference == .memory else { return nil }
        guard resolve(preference: preference, requiredBytes: requiredBytes, memoryBudget: memoryBudget) != .memory
        else { return nil }

        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        guard requiredBytes > 0 else {
            return "Its size was not known, so it ran on disk rather than in memory."
        }
        return "It needs \(formatter.string(fromByteCount: requiredBytes)) of working space, "
            + "more than the \(formatter.string(fromByteCount: memoryBudget)) memory budget, "
            + "so it ran on disk instead."
    }
}
