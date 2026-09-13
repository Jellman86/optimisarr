import Foundation
import os

/// Where the sidecar says what it is doing.
///
/// It said nothing at all until now. A menu-bar app with no window is the worst place for that: if
/// it crashes or quietly stops taking work, there is no window to look at, no terminal it was
/// started from, and nothing in the interface to explain. macOS's unified log solves it — the
/// entries survive the process, are timestamped, and can be read after the fact:
///
///     log show --last 30m --predicate 'subsystem == "uk.optimisarr.sidecar"' --info
///
/// Categories keep the noise separable, and nothing here ever takes a credential or a pairing
/// code: `%{private}` would only hide it from a console, while not logging it at all is the
/// actual guarantee.
public enum SidecarLog {
    public static let subsystem = "uk.optimisarr.sidecar"

    /// Pairing, check-ins, and the state machine around them.
    public static let session = Logger(subsystem: subsystem, category: "session")
    /// One job's progress from claim to delivery.
    public static let job = Logger(subsystem: subsystem, category: "job")
    /// What this machine proved it can do.
    public static let capability = Logger(subsystem: subsystem, category: "capability")
    /// Scratch space, chosen folders and RAM disks.
    public static let storage = Logger(subsystem: subsystem, category: "storage")
}
