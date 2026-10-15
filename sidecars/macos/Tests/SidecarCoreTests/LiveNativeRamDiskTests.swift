import Darwin
import Foundation
import Testing
@testable import SidecarCore

extension LiveRamDiskTestSuite {
@Suite("Live identity-bound native RAM retirement", .serialized,
    .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_RAMDISK"] != nil && NativeRamDiskLifecycle.isSupported))
struct LiveNativeRamDiskTests {
    private func cleanup(_ disk: RamDisk, identity: NativeRamDiskIdentity?) {
        if let identity {
            let retired = MacNativeRamDiskSystem.retire(identity, permitted: { true })
            #expect(retired)
        }
        else { #expect(disk.destroy()) }
    }
    private func scope(_ disk: RamDisk) throws -> NativeScratchScope {
        var info = stat()
        #expect(lstat(disk.mountPoint.path, &info) == 0)
        return NativeScratchScope(root: "darwin:\(UInt32(bitPattern: info.st_dev)):\(info.st_ino)", control: "fixture", gate: "fixture")
    }

    @Test("a busy writer vetoes handle-bound unmount, then retirement and replay succeed")
    func busyAndReplay() async throws {
        let bytes: Int64 = 16 * 1024 * 1024
        let disk = try #require(RamDisk.create(bytes: bytes))
        var captured: NativeRamDiskIdentity?
        defer { cleanup(disk, identity: captured) }
        let service = NativeRamDiskLifecycle()
        let identity = try #require(await service.capture(disk, scope: scope(disk), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        captured = identity
        let source = disk.mountPoint.appendingPathComponent("owned-copy")
        try Data("disposable copy".utf8).write(to: source)
        let writer = try FileHandle(forUpdating: source)
        defer { try? writer.close() }
        #expect(await !service.retire(identity))
        #expect(try Data(contentsOf: source) == Data("disposable copy".utf8))
        try writer.close()
        #expect(await service.retire(identity))
        #expect(!FileManager.default.fileExists(atPath: disk.mountPoint.path))
        #expect(await service.retire(identity))
    }

    @Test("wrong volume or root identity retains the exact original RAM allocation")
    func changedIdentity() async throws {
        let bytes: Int64 = 16 * 1024 * 1024
        let disk = try #require(RamDisk.create(bytes: bytes))
        var captured: NativeRamDiskIdentity?
        defer { cleanup(disk, identity: captured) }
        let service = NativeRamDiskLifecycle()
        let identity = try #require(await service.capture(disk, scope: scope(disk), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        captured = identity
        let changedVolume = NativeRamDiskIdentity(bootSession: identity.bootSession, registryEntryId: identity.registryEntryId,
            backingEntryId: identity.backingEntryId, volumeId: UUID(), device: identity.device,
            mountPoint: identity.mountPoint, rootIdentity: identity.rootIdentity)
        let changedRoot = NativeRamDiskIdentity(bootSession: identity.bootSession, registryEntryId: identity.registryEntryId,
            backingEntryId: identity.backingEntryId, volumeId: identity.volumeId, device: identity.device,
            mountPoint: identity.mountPoint, rootIdentity: "darwin:1:2")
        #expect(await !service.retire(changedVolume))
        #expect(await !service.retire(changedRoot))
        #expect(FileManager.default.fileExists(atPath: disk.mountPoint.path))
        #expect(await service.retire(identity))
    }

    @Test("a reused device name cannot redirect a delayed receipt to another RAM disk")
    func reusedName() async throws {
        let bytes: Int64 = 16 * 1024 * 1024
        let first = try #require(RamDisk.create(bytes: bytes))
        var firstIdentity: NativeRamDiskIdentity?
        defer { cleanup(first, identity: firstIdentity) }
        let service = NativeRamDiskLifecycle()
        let old = try #require(await service.capture(first, scope: scope(first), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        firstIdentity = old
        #expect(await service.retire(old))
        let second = try #require(RamDisk.create(bytes: bytes))
        var secondIdentity: NativeRamDiskIdentity?
        defer { cleanup(second, identity: secondIdentity) }
        let current = try #require(await service.capture(second, scope: scope(second), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        secondIdentity = current
        #expect(first.device == second.device)
        #expect(current.registryEntryId != old.registryEntryId && current.backingEntryId != old.backingEntryId)
        let source = second.mountPoint.appendingPathComponent("keep-copy")
        try Data("keep second allocation".utf8).write(to: source)
        #expect(await service.retire(old))
        #expect(try Data(contentsOf: source) == Data("keep second allocation".utf8))
        #expect(await service.retire(current))
    }

    @Test("redirected device fields and a changed backing identity cannot eject either live allocation")
    func redirectedAllocation() async throws {
        let bytes: Int64 = 16 * 1024 * 1024
        let first = try #require(RamDisk.create(bytes: bytes))
        var firstIdentity: NativeRamDiskIdentity?
        defer { cleanup(first, identity: firstIdentity) }
        let second = try #require(RamDisk.create(bytes: bytes))
        var secondIdentity: NativeRamDiskIdentity?
        defer { cleanup(second, identity: secondIdentity) }
        let service = NativeRamDiskLifecycle()
        let original = try #require(await service.capture(first, scope: scope(first), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        firstIdentity = original
        let other = try #require(await service.capture(second, scope: scope(second), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        secondIdentity = other
        let firstFile = first.mountPoint.appendingPathComponent("first-copy"), secondFile = second.mountPoint.appendingPathComponent("second-copy")
        try Data("first disposable copy".utf8).write(to: firstFile)
        try Data("second disposable copy".utf8).write(to: secondFile)
        let redirected = NativeRamDiskIdentity(bootSession: original.bootSession, registryEntryId: original.registryEntryId,
            backingEntryId: original.backingEntryId, volumeId: original.volumeId, device: second.device,
            mountPoint: second.mountPoint.path, rootIdentity: original.rootIdentity)
        let wrongBacking = NativeRamDiskIdentity(bootSession: original.bootSession, registryEntryId: original.registryEntryId,
            backingEntryId: other.backingEntryId, volumeId: original.volumeId, device: original.device,
            mountPoint: original.mountPoint, rootIdentity: original.rootIdentity)
        #expect(await !service.retire(redirected))
        #expect(await !service.retire(wrongBacking))
        #expect(try Data(contentsOf: firstFile) == Data("first disposable copy".utf8))
        #expect(try Data(contentsOf: secondFile) == Data("second disposable copy".utf8))
    }

    @Test("permission withdrawn after unmount retains the allocation and a later replay finishes ejection")
    func interruptedAfterUnmount() async throws {
        let bytes: Int64 = 16 * 1024 * 1024
        let disk = try #require(RamDisk.create(bytes: bytes))
        var captured: NativeRamDiskIdentity?
        defer { cleanup(disk, identity: captured) }
        let service = NativeRamDiskLifecycle()
        let identity = try #require(await service.capture(disk, scope: scope(disk), allocatedBytes: RamDisk.allocationBytes(for: bytes)!))
        captured = identity
        let partial = MacNativeRamDiskSystem.retire(identity, permitted: { FileManager.default.fileExists(atPath: disk.mountPoint.path) })
        #expect(!partial)
        #expect(!FileManager.default.fileExists(atPath: disk.mountPoint.path))
        #expect(await service.retire(identity))
        #expect(await service.retire(identity))
    }

    @Test("a file-backed image cannot borrow RAM provenance from matching names and size")
    func fileBackedImageRefused() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let image = root.appendingPathComponent("owned-fixture.dmg")
        let name = RamDisk.volumePrefix + String(UUID().uuidString.prefix(8))
        _ = try #require(RamDisk.run("/usr/bin/hdiutil", ["create", "-size", "48m", "-fs", "HFS+", "-layout", "NONE", "-volname", name, image.path]))
        let device = try #require(RamDisk.run("/usr/bin/hdiutil", ["attach", "-nomount", image.path]))
        defer { #expect(RamDisk.run("/usr/bin/hdiutil", ["detach", device]) != nil) }
        _ = try #require(RamDisk.run("/usr/sbin/diskutil", ["mount", "-nobrowse", device]))
        let text = try #require(RamDisk.run("/usr/sbin/diskutil", ["info", "-plist", device]))
        let info = try #require(try PropertyListSerialization.propertyList(from: Data(text.utf8), format: nil) as? [String: Any])
        let path = try #require(info["MountPoint"] as? String)
        let size = try #require(info["TotalSize"] as? Int64)
        let fake = RamDisk(mountPoint: URL(fileURLWithPath: path), device: device)
        let captured = await NativeRamDiskLifecycle().capture(fake, scope: try scope(fake), allocatedBytes: size)
        #expect(captured == nil)
        #expect(FileManager.default.fileExists(atPath: path))
    }
}

}
