import Foundation
import Testing
@testable import SidecarCore

/// Creates and destroys a real RAM disk, so the hdiutil/newfs_hfs/diskutil sequence is proved
/// rather than assumed. Skipped unless asked for, because a test that mounts volumes on someone's
/// machine — or a CI runner — should be a deliberate act:
///
///     OPTIMISARR_LIVE_RAMDISK=1 swift test --filter LiveRamDisk
@Suite("Live RAM disk", .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_RAMDISK"] != nil))
struct LiveRamDiskTests {
    @Test("a real volume is created, holds a file, and leaves nothing behind")
    func roundTrip() throws {
        let disk = try #require(RamDisk.create(bytes: 64 * 1024 * 1024), "the Mac would not create a RAM disk")
        #expect(disk.mountPoint.lastPathComponent.hasPrefix(RamDisk.volumePrefix))
        #expect(FileManager.default.fileExists(atPath: disk.mountPoint.path))

        // The volume has to be big enough for what it was asked to hold, which is the part the
        // filesystem overhead allowance exists for.
        let file = disk.mountPoint.appendingPathComponent("probe.bin")
        try Data(repeating: 3, count: 32 * 1024 * 1024).write(to: file)
        #expect(FileManager.default.fileExists(atPath: file.path))

        disk.destroy()
        // Leaving one behind holds real memory until the Mac reboots, with nothing on screen to
        // say so, which is the failure this whole type is written to avoid.
        #expect(!FileManager.default.fileExists(atPath: disk.mountPoint.path))
    }

    @Test("sweeping removes one left behind by a crash")
    func sweepsStrays() throws {
        let disk = try #require(RamDisk.create(bytes: 32 * 1024 * 1024))
        let path = disk.mountPoint.path
        // Deliberately not destroyed: this is what a crash leaves.
        RamDisk.sweepStrays()

        #expect(!FileManager.default.fileExists(atPath: path))
    }
}
