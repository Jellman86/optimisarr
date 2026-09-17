import Foundation

/// Working directories left behind by a process that did not get to tidy up.
///
/// Each job removes its own scratch in a `defer`, which covers every way a job can end — but not
/// every way a *process* can. A force quit, a crash, a machine put to sleep and woken somewhere
/// else: the process stops between the source arriving and the job ending, and several gigabytes
/// stay on the disk with nothing that will ever remove them. Three were found on a working Mac
/// holding 1.3 GB between them, all from being restarted during a day's development.
///
/// Swept at startup, because that is the one moment this process is certain to hold no lease.
/// Another copy of the app might, though — one was started by accident earlier today — so a
/// directory still being written to is left alone. A live job touches its scratch constantly while
/// a source downloads or a candidate encodes; an orphan's clock stopped when its process did.
public enum OrphanedScratch {
    /// How still a directory must be before it is taken for abandoned. Minutes rather than hours:
    /// long enough that no working job could look this idle, short enough to matter on a laptop.
    public static let stillness: TimeInterval = 15 * 60

    /// The scratch directories under `root` that nothing is using any more.
    public static func abandoned(
        in root: URL,
        now: Date = Date(),
        stillness: TimeInterval = OrphanedScratch.stillness,
        modifiedAt: (URL) -> Date? = { url in
            (try? FileManager.default.attributesOfItem(atPath: url.path)[.modificationDate]) as? Date
        },
        contents: (URL) -> [URL] = { url in
            (try? FileManager.default.contentsOfDirectory(
                at: url, includingPropertiesForKeys: nil)) ?? []
        }
    ) -> [URL] {
        contents(root)
            .filter { $0.lastPathComponent.hasPrefix("lease-") }
            .filter { candidate in
                // Anything whose age cannot be read is left alone. Deleting on a failed stat would
                // be deleting on no evidence at all, and the thing being deleted is a job's work.
                guard let modified = modifiedAt(candidate) else { return false }
                return now.timeIntervalSince(modified) >= stillness
            }
    }

    /// Removes them, and says how much came back. Failures are ignored on purpose: a directory
    /// that will not delete is a reason to carry on starting up, not a reason to refuse to.
    @discardableResult
    public static func sweep(
        in root: URL,
        now: Date = Date(),
        remove: (URL) -> Void = { try? FileManager.default.removeItem(at: $0) }
    ) -> Int {
        let orphans = abandoned(in: root, now: now)
        for orphan in orphans {
            SidecarLog.storage.notice(
                "Removing abandoned working directory \(orphan.lastPathComponent, privacy: .public)")
            remove(orphan)
        }
        return orphans.count
    }
}
