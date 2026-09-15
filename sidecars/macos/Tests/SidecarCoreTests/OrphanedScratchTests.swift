import Foundation
import Testing
@testable import SidecarCore

/// Cleaning up after a process that did not get to.
///
/// Every job removes its own scratch in a `defer`, which covers every way a job can end and none
/// of the ways a process can. Three abandoned directories holding 1.3 GB were found on a working
/// Mac, all from it being restarted during a day's development — and nothing would ever have
/// removed them.
struct OrphanedScratchTests {
    private let root = URL(fileURLWithPath: "/work")
    private let now = Date(timeIntervalSinceReferenceDate: 1_000_000)
    // Named with their type rather than written out at each call. The compiler on the build
    // machine will not read `20 * 60` as a TimeInterval inside a tuple literal, so the whole
    // macOS job failed to build on tests that compile perfectly well on a development Mac.
    private let twentyMinutes: TimeInterval = 20 * 60
    private let anHour: TimeInterval = 60 * 60

    private func abandoned(_ entries: [(String, TimeInterval)]) -> [URL] {
        let urls = entries.map { root.appendingPathComponent($0.0, isDirectory: true) }
        let ages = Dictionary(uniqueKeysWithValues: zip(urls, entries.map(\.1)))
        return OrphanedScratch.abandoned(
            in: root,
            now: now,
            modifiedAt: { ages[$0].map { now.addingTimeInterval(-$0) } },
            contents: { _ in urls })
    }

    @Test("a directory nothing has touched in a quarter of an hour is abandoned")
    func staleIsSwept() {
        let swept = abandoned([("lease-a", twentyMinutes)])
        #expect(swept.map(\.lastPathComponent) == ["lease-a"])
    }

    @Test("a job still working is left alone")
    func liveIsKept() {
        // Another copy of the app may be running — one was started by accident today. A live job
        // writes to its scratch constantly while a source downloads or a candidate encodes, so a
        // recent timestamp is the difference between an orphan and somebody's work in progress.
        #expect(abandoned([("lease-a", 30)]).isEmpty)
        #expect(abandoned([("lease-a", OrphanedScratch.stillness - 1)]).isEmpty)
    }

    @Test("nothing but a lease directory is touched")
    func onlyLeases() {
        // The work root is shared with whatever else the app keeps there, and a sweep that took
        // anything old would eventually take something that mattered.
        let swept = abandoned([
            ("lease-old", anHour),
            ("credentials", anHour),
            ("settings.json", anHour),
        ])

        #expect(swept.map(\.lastPathComponent) == ["lease-old"])
    }

    @Test("a directory whose age cannot be read is left alone")
    func unreadableIsKept() {
        // Deleting on a failed stat is deleting on no evidence, and what would be deleted is a
        // job's work.
        let url = root.appendingPathComponent("lease-a", isDirectory: true)
        let swept = OrphanedScratch.abandoned(
            in: root, now: now, modifiedAt: { _ in nil }, contents: { _ in [url] })

        #expect(swept.isEmpty)
    }

    @Test("sweeping takes the abandoned directory off the disk and leaves the live one")
    func sweepRemoves() throws {
        // On the real file system, with real timestamps, because the point of this is that a
        // gigabyte actually comes back.
        let manager = FileManager.default
        let work = manager.temporaryDirectory
            .appendingPathComponent("optimisarr-sweep-\(UUID().uuidString)", isDirectory: true)
        try manager.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? manager.removeItem(at: work) }

        let stale = work.appendingPathComponent("lease-stale", isDirectory: true)
        let live = work.appendingPathComponent("lease-live", isDirectory: true)
        let keep = work.appendingPathComponent("credentials", isDirectory: true)
        for directory in [stale, live, keep] {
            try manager.createDirectory(at: directory, withIntermediateDirectories: true)
            try Data("a source".utf8).write(to: directory.appendingPathComponent("source"))
        }

        // The abandoned one stopped being written to an hour ago.
        try manager.setAttributes(
            [.modificationDate: Date().addingTimeInterval(-3600)], ofItemAtPath: stale.path)

        let removed = OrphanedScratch.sweep(in: work)

        #expect(removed == 1)
        #expect(!manager.fileExists(atPath: stale.path))
        #expect(manager.fileExists(atPath: live.path))
        #expect(manager.fileExists(atPath: keep.path))
    }
}
