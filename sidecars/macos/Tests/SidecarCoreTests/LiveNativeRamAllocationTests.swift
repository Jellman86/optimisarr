import Darwin
import Foundation
import Testing
@testable import SidecarCore

extension LiveRamDiskTestSuite {
@Suite("Live tagged native RAM allocations", .serialized,
    .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_RAMDISK"] != nil && NativeRamDiskLifecycle.isSupported))
struct LiveNativeRamAllocationTests {
    private let bytes: Int64 = 48 * 1024 * 1024
    private struct RecoveryEvidence: Encodable {
        let owner: UUID
        let boot: UUID
        let device: String
        let allocatedBytes: Int64
        let allocation: NativeRamAllocationIdentity?
    }

    private func cleanup(device: String, owner: UUID, boot: UUID, captured: NativeRamAllocationIdentity?) {
        let receipt = captured ?? MacNativeRamDiskSystem.captureAllocation(device: device, owner: owner,
            bootSession: boot, allocatedBytes: bytes, permitted: { true })
        let retired = receipt.map { MacNativeRamDiskSystem.retireAllocation($0, permitted: { true }) } ?? false
        if !retired {
            let recovery = FileManager.default.temporaryDirectory.appendingPathComponent("optimisarr-ram-recovery-\(owner.uuidString).json")
            let evidence = RecoveryEvidence(owner: owner, boot: boot, device: device, allocatedBytes: bytes, allocation: receipt)
            try? JSONEncoder().encode(evidence).write(to: recovery, options: .atomic)
            Issue.record("Owned RAM cleanup unconfirmed; recovery intent retained at \(recovery.path)")
        }
        #expect(retired)
    }

    @Test("an unformatted tagged allocation can be captured and retired without a volume UUID")
    func unformattedAllocation() async throws {
        let owner = UUID(), boot = try #require(MacNativeRamDiskSystem.bootSession())
        let device = try #require(RamDisk.run("/usr/bin/hdiutil", ["attach", "-nomount", "-drivekey",
            "OptimisarrNativeOwner=\(owner.uuidString)", "ram://\(bytes / 512)"]))
        var captured: NativeRamAllocationIdentity?
        defer { cleanup(device: device, owner: owner, boot: boot, captured: captured) }
        let service = NativeRamDiskLifecycle()
        let allocation = try #require(await service.captureAllocation(device: device, owner: owner, bootSession: boot, allocatedBytes: bytes))
        captured = allocation
        let wrongOwner = NativeRamAllocationIdentity(bootSession: boot, owner: UUID(), registryEntryId: allocation.registryEntryId,
            backingEntryId: allocation.backingEntryId, device: device, allocatedBytes: bytes)
        let wrongSize = NativeRamAllocationIdentity(bootSession: boot, owner: owner, registryEntryId: allocation.registryEntryId,
            backingEntryId: allocation.backingEntryId, device: device, allocatedBytes: bytes + 512)
        #expect(await !service.retire(wrongOwner))
        #expect(await !service.retire(wrongSize))
        #expect(await service.captureAllocation(device: device, owner: UUID(), bootSession: boot, allocatedBytes: bytes) == nil)
        #expect(await service.captureAllocation(device: device, owner: owner, bootSession: UUID(), allocatedBytes: bytes) == nil)
        #expect(await service.captureAllocation(device: device, owner: owner, bootSession: boot, allocatedBytes: bytes + 512) == nil)
        #expect(allocation.owner == owner && allocation.allocatedBytes == bytes)
        #expect(await service.retire(allocation))
        #expect(await service.retire(allocation))
    }

    @Test("a retired partial receipt cannot act on a new allocation reusing its BSD name")
    func reusedAllocationName() async throws {
        let boot = try #require(MacNativeRamDiskSystem.bootSession()), firstOwner = UUID()
        let firstDevice = try #require(RamDisk.run("/usr/bin/hdiutil", ["attach", "-nomount", "-drivekey",
            "OptimisarrNativeOwner=\(firstOwner.uuidString)", "ram://\(bytes / 512)"]))
        var firstCaptured: NativeRamAllocationIdentity?
        defer { cleanup(device: firstDevice, owner: firstOwner, boot: boot, captured: firstCaptured) }
        let service = NativeRamDiskLifecycle()
        let first = try #require(await service.captureAllocation(device: firstDevice, owner: firstOwner, bootSession: boot, allocatedBytes: bytes))
        firstCaptured = first
        try #require(await service.retire(first))
        let nextOwner = UUID()
        let nextDevice = try #require(RamDisk.run("/usr/bin/hdiutil", ["attach", "-nomount", "-drivekey",
            "OptimisarrNativeOwner=\(nextOwner.uuidString)", "ram://\(bytes / 512)"]))
        var nextCaptured: NativeRamAllocationIdentity?
        defer { cleanup(device: nextDevice, owner: nextOwner, boot: boot, captured: nextCaptured) }
        let next = try #require(await service.captureAllocation(device: nextDevice, owner: nextOwner, bootSession: boot, allocatedBytes: bytes))
        nextCaptured = next
        #expect(nextDevice == firstDevice && next.registryEntryId != first.registryEntryId && next.backingEntryId != first.backingEntryId)
        #expect(await service.retire(first))
        #expect(await service.captureAllocation(device: nextDevice, owner: nextOwner, bootSession: boot, allocatedBytes: bytes) == next)
    }

    @Test("a partial receipt cannot unmount a later filesystem or eject a redirected allocation")
    func mountedAndRedirectedAllocation() async throws {
        let owner = UUID(), boot = try #require(MacNativeRamDiskSystem.bootSession())
        let device = try #require(RamDisk.run("/usr/bin/hdiutil", ["attach", "-nomount", "-drivekey",
            "OptimisarrNativeOwner=\(owner.uuidString)", "ram://\(bytes / 512)"]))
        var captured: NativeRamAllocationIdentity?
        defer { cleanup(device: device, owner: owner, boot: boot, captured: captured) }
        let service = NativeRamDiskLifecycle()
        let allocation = try #require(await service.captureAllocation(device: device, owner: owner, bootSession: boot, allocatedBytes: bytes))
        captured = allocation
        try #require(RamDisk.run("/sbin/newfs_hfs", ["-v", RamDisk.volumePrefix + owner.uuidString, device]) != nil)
        let canonical = try #require(realpath(FileManager.default.temporaryDirectory.path, nil))
        defer { free(canonical) }
        let mount = URL(fileURLWithPath: String(cString: canonical)).appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: mount, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        defer { _ = Darwin.rmdir(mount.path) }
        try #require(RamDisk.run("/sbin/mount_hfs", ["-o", "nobrowse,nosuid,nodev,nofollow", device, mount.path]) != nil)
        var root = Darwin.open(mount.path, O_EVTONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
        try #require(root >= 0)
        var unmounted = false
        defer {
            if !unmounted, #available(macOS 16, *) {
                var info = stat(), block = stat()
                if fstat(root, &info) == 0, lstat(device, &block) == 0, info.st_dev == block.st_rdev {
                    #expect(funmount(root, 0) == 0)
                }
            }
            if root >= 0 { Darwin.close(root) }
        }
        var rootInfo = stat(), deviceInfo = stat()
        try #require(fstat(root, &rootInfo) == 0 && lstat(device, &deviceInfo) == 0 && rootInfo.st_dev == deviceInfo.st_rdev)
        let sentinel = mount.appendingPathComponent("owned-copy")
        try Data("keep this disposable copy".utf8).write(to: sentinel)
        #expect(await !service.retire(allocation))
        #expect(try Data(contentsOf: sentinel) == Data("keep this disposable copy".utf8))
        let redirected = NativeRamAllocationIdentity(bootSession: boot, owner: UUID(), registryEntryId: allocation.registryEntryId,
            backingEntryId: allocation.backingEntryId, device: device, allocatedBytes: bytes)
        #expect(await !service.retire(redirected))
        if #available(macOS 16, *) { unmounted = funmount(root, 0) == 0; #expect(unmounted) }
        Darwin.close(root); root = -1
        // Unmount completion need not end every transient OS opener. Replay the same
        // receipt; neither a delay nor a refreshed BSD-name lookup authorizes ejection.
        let deadline = ContinuousClock.now.advanced(by: .seconds(5))
        var retired = await service.retire(allocation)
        while !retired && ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(25))
            retired = await service.retire(allocation)
        }
        #expect(retired)
    }
}

}
