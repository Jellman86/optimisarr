import Darwin
import Foundation
import IOKit

final class MacNativeRamFactoryAllocation: NativeRamFactoryAllocating, @unchecked Sendable {
    struct Unconfirmed: Error {}
    let identity: NativeRamAllocationIdentity
    private var raw: Int32
    private let lease: NativeRamCreationLease

    private init(identity: NativeRamAllocationIdentity, raw: Int32, lease: NativeRamCreationLease) {
        self.identity = identity; self.raw = raw; self.lease = lease
    }
    deinit { endCreation() }

    static func attach(owner: NativeAccountedOwner, boot: UUID, bytes: Int64,
                       control: NativeRamFactoryControl) throws -> any NativeRamFactoryAllocating {
        guard NativeRamDiskLifecycle.isSupported, control.isPermitted,
              MacNativeRamDiskSystem.bootSession() == boot,
              let device = NativeRamPinnedCommand.run("/usr/bin/hdiutil", ["attach", "-nomount", "-drivekey",
                "OptimisarrNativeOwner=\(owner.owner.uuidString)", "ram://\(bytes / 512)"], control: control),
              control.isPermitted,
              let identity = MacNativeRamDiskSystem.captureAllocation(device: device, owner: owner.owner,
                bootSession: boot, allocatedBytes: bytes, permitted: { control.isPermitted }),
              let lease = NativeRamDiskLifecycle.creationLease(identity) else { throw Unconfirmed() }
        do {
            guard control.isPermitted, let media = try MacNativeRamDiskSystem.entry(identity.registryEntryId) else { throw Unconfirmed() }
            defer { IOObjectRelease(media) }
            guard let backing = try MacNativeRamDiskSystem.backing(media) else { throw Unconfirmed() }
            defer { IOObjectRelease(backing) }
            guard try MacNativeRamDiskSystem.id(backing) == identity.backingEntryId,
                  try MacNativeRamDiskSystem.property(backing, "OptimisarrNativeOwner") as? String == owner.owner.uuidString,
                  try MacNativeRamDiskSystem.number(media, "Size") == UInt64(bytes) else { throw Unconfirmed() }
            let raw = try MacNativeRamDiskSystem.pin(device, media: media)
            return MacNativeRamFactoryAllocation(identity: identity, raw: raw, lease: lease)
        } catch { lease.close(); throw error }
    }

    func format(control: NativeRamFactoryControl) -> Bool {
        guard raw >= 0, control.isPermitted else { return false }
        let input = FileHandle(fileDescriptor: raw, closeOnDealloc: false)
        return NativeRamPinnedCommand.run("/sbin/newfs_hfs", ["-v", RamDisk.volumePrefix + identity.owner.uuidString,
            identity.device], input: input, control: control) != nil && control.isPermitted
    }

    func mount(at parent: URL, control: NativeRamFactoryControl) -> NativeRamDiskCreation? {
        do {
            guard raw >= 0, control.isPermitted else { return nil }
            let directory = try NativeRamMountDirectory(parent: parent, owner: identity.owner)
            guard control.isPermitted, directory.isBound,
                  NativeRamPinnedCommand.run("/sbin/mount_hfs", ["-o", "nobrowse,nosuid,nodev,nofollow",
                    identity.device, directory.mountPoint.path], input: FileHandle(fileDescriptor: raw, closeOnDealloc: false),
                    control: control) != nil, control.isPermitted else { return nil }
            let mounted = try directory.capture(identity)
            return NativeRamDiskCreation(disk: RamDisk(mountPoint: directory.mountPoint, device: identity.device),
                identity: mounted, directory: directory)
        } catch { return nil }
    }

    func endCreation() {
        if raw >= 0 { Darwin.close(raw); raw = -1 }
        lease.close()
    }
}

/// Retains the covered leaf on the physical disk; capture opens a NEW descriptor through
/// its original parent after mounting, because the old leaf FD still sees the covered directory.
final class NativeRamMountDirectory: @unchecked Sendable {
    private struct Identity: Equatable { let device: dev_t; let inode: ino_t }
    let mountPoint: URL
    private let parentPath: String
    private let name: String
    private var parent: Int32 = -1
    private var covered: Int32 = -1
    private var parentIdentity: Identity?
    private var coveredIdentity: Identity?

    init(parent url: URL, owner: UUID) throws {
        guard url.isFileURL, let resolved = realpath(url.path, nil) else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        defer { free(resolved) }
        parentPath = String(cString: resolved)
        name = owner.uuidString
        mountPoint = URL(fileURLWithPath: parentPath).appendingPathComponent(name)
        do {
            parent = Darwin.open(parentPath, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard parent >= 0 else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
            parentIdentity = try Self.identity(parent)
            var filesystem = statfs()
            guard fstatfs(parent, &filesystem) == 0, filesystem.f_flags & UInt32(MNT_LOCAL) != 0 else {
                throw MacNativeRamFactoryAllocation.Unconfirmed()
            }
            guard mkdirat(parent, name, 0o700) == 0 else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
            covered = openat(parent, name, O_EVTONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard covered >= 0 else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
            coveredIdentity = try Self.identity(covered)
            guard isBound else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        } catch { cleanup(); throw error }
    }
    deinit { cleanup() }

    var isBound: Bool {
        guard let parentIdentity, let coveredIdentity, (try? Self.identity(parent)) == parentIdentity,
              Self.namedIdentity(parentPath) == parentIdentity else { return false }
        var leaf = stat()
        return fstatat(parent, name, &leaf, AT_SYMLINK_NOFOLLOW) == 0
            && leaf.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR)
            && Identity(device: leaf.st_dev, inode: leaf.st_ino) == coveredIdentity
    }

    func capture(_ allocation: NativeRamAllocationIdentity) throws -> NativeRamDiskIdentity {
        guard let parentIdentity, (try? Self.identity(parent)) == parentIdentity,
              Self.namedIdentity(parentPath) == parentIdentity else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        let root = openat(parent, name, O_EVTONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard root >= 0 else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        defer { Darwin.close(root) }
        var info = stat(), block = stat(), filesystem = statfs()
        guard fstat(root, &info) == 0, lstat(allocation.device, &block) == 0, info.st_dev == block.st_rdev,
              fstatfs(root, &filesystem) == 0,
              withUnsafeBytes(of: filesystem.f_mntfromname, { bytes in
                  String(decoding: bytes.prefix(while: { $0 != 0 }), as: UTF8.self) == allocation.device
              }), let media = try MacNativeRamDiskSystem.entry(allocation.registryEntryId) else {
            throw MacNativeRamFactoryAllocation.Unconfirmed()
        }
        defer { IOObjectRelease(media) }
        guard let backing = try MacNativeRamDiskSystem.backing(media) else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        defer { IOObjectRelease(backing) }
        guard try MacNativeRamDiskSystem.id(backing) == allocation.backingEntryId,
              try MacNativeRamDiskSystem.property(backing, "OptimisarrNativeOwner") as? String == allocation.owner.uuidString,
              Self.namedMountedRoot(parent, name) == Identity(device: info.st_dev, inode: info.st_ino) else {
            throw MacNativeRamFactoryAllocation.Unconfirmed()
        }
        let volume = try NativeRamMountedVolume.id(root)
        return NativeRamDiskIdentity(bootSession: allocation.bootSession, registryEntryId: allocation.registryEntryId,
            backingEntryId: allocation.backingEntryId, volumeId: volume, device: allocation.device,
            mountPoint: mountPoint.path, rootIdentity: "darwin:\(UInt32(bitPattern: info.st_dev)):\(info.st_ino)")
    }

    private static func identity(_ descriptor: Int32) throws -> Identity {
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR),
              info.st_uid == geteuid(), info.st_mode & 0o077 == 0 else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        return Identity(device: info.st_dev, inode: info.st_ino)
    }
    private static func namedIdentity(_ path: String) -> Identity? {
        var info = stat()
        guard lstat(path, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR),
              info.st_uid == geteuid(), info.st_mode & 0o077 == 0 else { return nil }
        return Identity(device: info.st_dev, inode: info.st_ino)
    }
    private static func namedMountedRoot(_ parent: Int32, _ name: String) -> Identity? {
        var info = stat()
        guard fstatat(parent, name, &info, AT_SYMLINK_NOFOLLOW) == 0,
              info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR) else { return nil }
        return Identity(device: info.st_dev, inode: info.st_ino)
    }
    private func cleanup() {
        if isBound { _ = unlinkat(parent, name, AT_REMOVEDIR) }
        if covered >= 0 { Darwin.close(covered); covered = -1 }
        if parent >= 0 { Darwin.close(parent); parent = -1 }
    }
}

enum NativeRamMountedVolume {
    private struct Attributes {
        var length: UInt32 = 0
        var uuid: uuid_t = (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
    }
    static func id(_ root: Int32) throws -> UUID {
        var attributes = attrlist()
        attributes.bitmapcount = UInt16(ATTR_BIT_MAP_COUNT)
        attributes.volattr = attrgroup_t(ATTR_VOL_INFO) | attrgroup_t(ATTR_VOL_UUID)
        var result = Attributes()
        guard fgetattrlist(root, &attributes, &result, MemoryLayout<Attributes>.size, UInt32(FSOPT_NOFOLLOW)) == 0,
              result.length == MemoryLayout<Attributes>.size else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        let volume = UUID(uuid: result.uuid)
        guard volume != NativeWorkLedger.zero else { throw MacNativeRamFactoryAllocation.Unconfirmed() }
        return volume
    }
}
