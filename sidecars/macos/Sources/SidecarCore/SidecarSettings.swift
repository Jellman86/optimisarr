import Foundation

/// The operator's choices that are preferences rather than secrets, so `UserDefaults` rather than
/// the Keychain.
@MainActor
public final class SidecarSettings: ObservableObject {
    private enum Key {
        static let mode = "workLocationMode"
        static let folder = "workLocationFolder"
        static let budget = "memoryBudgetBytes"
    }

    @Published public private(set) var workLocation: WorkLocation

    /// How much memory a RAM disk may take. Configurable because the right answer depends on the
    /// Mac and on what else it is doing, and always held inside what the machine can give up.
    @Published public private(set) var memoryBudgetBytes: Int64

    private let defaults: UserDefaults
    public let physicalMemoryBytes: Int64

    /// What the job runner reads. Updated here, on the main actor, and read from anywhere.
    public let snapshot = SettingsSnapshot()

    public init(defaults: UserDefaults = .standard, physicalMemoryBytes: Int64 = SidecarSettings.physicalMemoryBytes) {
        self.defaults = defaults
        self.physicalMemoryBytes = physicalMemoryBytes

        let stored = defaults.object(forKey: Key.budget) as? NSNumber
        memoryBudgetBytes = WorkLocationPolicy.clampBudget(
            stored?.int64Value ?? WorkLocationPolicy.defaultBudget(physicalBytes: physicalMemoryBytes),
            physicalBytes: physicalMemoryBytes)
        switch defaults.string(forKey: Key.mode) {
        case "memory":
            workLocation = .memory
        case "folder":
            // A folder that has gone away — an ejected drive — falls back rather than failing every
            // job with a path that no longer exists.
            if let path = defaults.string(forKey: Key.folder),
               FileManager.default.fileExists(atPath: path) {
                workLocation = .folder(URL(fileURLWithPath: path))
            } else {
                workLocation = .applicationSupport
            }
        default:
            workLocation = .applicationSupport
        }

        publish()
    }

    /// Pushes the current values into the box the runner reads.
    private func publish() {
        snapshot.update(workLocation: workLocation, memoryBudgetBytes: memoryBudgetBytes)
    }

    public func setWorkLocation(_ location: WorkLocation) {
        workLocation = location
        switch location {
        case .applicationSupport:
            defaults.removeObject(forKey: Key.mode)
            defaults.removeObject(forKey: Key.folder)
        case .memory:
            defaults.set("memory", forKey: Key.mode)
            defaults.removeObject(forKey: Key.folder)
        case let .folder(url):
            defaults.set("folder", forKey: Key.mode)
            defaults.set(url.path, forKey: Key.folder)
        }
        publish()
    }

    public func setMemoryBudget(_ bytes: Int64) {
        memoryBudgetBytes = WorkLocationPolicy.clampBudget(bytes, physicalBytes: physicalMemoryBytes)
        defaults.set(NSNumber(value: memoryBudgetBytes), forKey: Key.budget)
        publish()
    }

    /// Physical memory, for the RAM disk budget.
    public static var physicalMemoryBytes: Int64 { Int64(ProcessInfo.processInfo.physicalMemory) }
}
