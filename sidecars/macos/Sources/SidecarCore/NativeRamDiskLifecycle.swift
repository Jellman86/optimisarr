import Darwin
import DiskArbitration
import Foundation
import IOKit

/// An OS call may outlive cancellation. Its physical allocation stays single-flight until
/// the call actually returns, even after the caller's bounded wait has ended.
public final class NativeRamDiskLifecycle: @unchecked Sendable {
    public static let shared = NativeRamDiskLifecycle()
    public static var isSupported: Bool {
        if #available(macOS 16, *) { return true }
        return false
    }
    private struct Key: Hashable { let boot: UUID; let media: UInt64; let backing: UInt64 }
    private final class Slots: @unchecked Sendable {
        let gate = NSLock()
        var inFlight: Set<Key> = []
    }
    private static let slots = Slots()
    private let timeout: TimeInterval
    private let retireOperation: @Sendable (NativeRamDiskIdentity, @Sendable () -> Bool) -> Bool

    public convenience init(timeout: Duration = .seconds(10)) {
        self.init(timeout: timeout, retire: MacNativeRamDiskSystem.retire)
    }

    init(timeout: Duration = .seconds(10),
         retire: @escaping @Sendable (NativeRamDiskIdentity, @Sendable () -> Bool) -> Bool) {
        let parts = timeout.components
        self.timeout = max(0.001, min(30, Double(parts.seconds) + Double(parts.attoseconds) / 1e18))
        retireOperation = retire
    }

    public func capture(_ disk: RamDisk, scope: NativeScratchScope, allocatedBytes: Int64) async -> NativeRamDiskIdentity? {
        guard Self.isSupported, !Task.isCancelled, scope.isValid, allocatedBytes > 0 else { return nil }
        return await perform(fallback: nil) { permitted in
            MacNativeRamDiskSystem.capture(disk, scope: scope, allocatedBytes: allocatedBytes, permitted: permitted)
        }
    }

    public func retire(_ identity: NativeRamDiskIdentity) async -> Bool {
        guard identity.isValid, !Task.isCancelled else { return false }
        let key = key(identity)
        guard Self.slots.gate.withLock({ Self.slots.inFlight.insert(key).inserted }) else { return false }
        return await perform(fallback: false) { permitted in
            defer { Self.slots.gate.withLock { _ = Self.slots.inFlight.remove(key) } }
            guard permitted() else { return false }
            return self.retireOperation(identity, permitted)
        }
    }

    func hasInFlightOperation(_ identity: NativeRamDiskIdentity) -> Bool { Self.slots.gate.withLock { Self.slots.inFlight.contains(key(identity)) } }
    private func key(_ identity: NativeRamDiskIdentity) -> Key {
        Key(boot: identity.bootSession, media: identity.registryEntryId, backing: identity.backingEntryId)
    }

    private func perform<Value: Sendable>(fallback: Value,
        work: @escaping @Sendable (@Sendable () -> Bool) -> Value) async -> Value {
        let attempt = NativeRamAttempt(fallback: fallback)
        return await withTaskCancellationHandler {
            await withCheckedContinuation { continuation in
                attempt.install(continuation)
                DispatchQueue.global(qos: .utility).async {
                    attempt.finish(work { attempt.isPermitted })
                }
                DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + timeout) { attempt.cancel() }
            }
        } onCancel: { attempt.cancel() }
    }
}

private final class NativeRamAttempt<Value: Sendable>: @unchecked Sendable {
    private let gate = NSLock()
    private let fallback: Value
    private var continuation: CheckedContinuation<Value, Never>?
    private var result: Value?
    private var ended = false
    init(fallback: Value) { self.fallback = fallback }
    var isPermitted: Bool { gate.withLock { !ended } }
    func install(_ continuation: CheckedContinuation<Value, Never>) {
        let completed: Value? = gate.withLock {
            if ended { return result }
            self.continuation = continuation
            return nil
        }
        if let completed { continuation.resume(returning: completed) }
    }
    func cancel() { finish(fallback) }
    func finish(_ value: Value) {
        let pending = gate.withLock {
            guard !ended else { return Optional<CheckedContinuation<Value, Never>>.none }
            ended = true; result = value
            let pending = continuation; continuation = nil
            return pending
        }
        pending?.resume(returning: value)
    }
}

enum NativeRegistryIterator {
    struct Unconfirmed: Error {}
    static func readSingle(next: () -> UInt32, isValid: () -> Bool, release: (UInt32) -> Void,
                           accepts: (UInt32) throws -> Bool = { _ in true }, maximum: Int = 16_384) throws -> UInt32? {
        var found: UInt32?
        var returned = false
        defer { if !returned, let found { release(found) } }
        for _ in 0..<maximum {
            let object = next()
            if object == 0 {
                guard isValid() else { throw Unconfirmed() }
                returned = true
                return found
            }
            do {
                if try accepts(object) {
                    guard found == nil else { throw Unconfirmed() }
                    found = object
                } else { release(object) }
            } catch { release(object); throw error }
        }
        throw Unconfirmed()
    }
}

enum MacNativeRamDiskSystem {
    private struct Volume { let id: UUID; let mount: URL? }
    private struct Unconfirmed: Error {}

    static func bootSession() -> UUID? {
        var size = 0
        guard sysctlbyname("kern.bootsessionuuid", nil, &size, nil, 0) == 0, size > 1, size <= 128 else { return nil }
        var bytes = [CChar](repeating: 0, count: size)
        guard sysctlbyname("kern.bootsessionuuid", &bytes, &size, nil, 0) == 0,
              let id = UUID(uuidString: string(bytes)), id != NativeWorkLedger.zero else { return nil }
        return id
    }

    static func capture(_ disk: RamDisk, scope: NativeScratchScope, allocatedBytes: Int64,
                        permitted: @Sendable () -> Bool) -> NativeRamDiskIdentity? {
        do {
            guard permitted(), let boot = bootSession(), disk.mountPoint.lastPathComponent.hasPrefix(RamDisk.volumePrefix),
                  let media = try namedMedia(disk.device) else { return nil }
            defer { IOObjectRelease(media) }
            guard let backing = try backing(media) else { return nil }
            defer { IOObjectRelease(backing) }
            guard try number(media, "Size") == UInt64(allocatedBytes) else { return nil }
            let raw = try pin(disk.device, media: media)
            defer { Darwin.close(raw) }
            let root = try rootHandle(disk.mountPoint, device: disk.device, identity: scope.root)
            defer { Darwin.close(root) }
            guard permitted(), let snapshot = RamDisk.run("/usr/bin/hdiutil", ["info", "-plist"], timeout: 5),
                  NativeRamImageProvenance.isRAM(Data(snapshot.utf8), device: disk.device, allocatedBytes: allocatedBytes) else { return nil }
            let volume = try volume(disk.device)
            guard volume.mount?.path == disk.mountPoint.path, permitted() else { return nil }
            return NativeRamDiskIdentity(bootSession: boot, registryEntryId: try id(media), backingEntryId: try id(backing),
                volumeId: volume.id, device: disk.device, mountPoint: disk.mountPoint.path, rootIdentity: scope.root)
        } catch { return nil }
    }

    static func retire(_ identity: NativeRamDiskIdentity, permitted: @Sendable () -> Bool) -> Bool {
        do {
            guard permitted(), let boot = bootSession() else { return false }
            if boot != identity.bootSession { return permitted() }
            if try absent(identity, permitted: permitted) { return permitted() }
            guard NativeRamDiskLifecycle.isSupported, try eject(identity, permitted: permitted) else { return false }
            for _ in 0..<100 {
                if try absent(identity, permitted: permitted) { return permitted() }
                guard permitted() else { return false }
                usleep(10_000)
            }
        } catch {}
        return false
    }

    private static func eject(_ identity: NativeRamDiskIdentity, permitted: @Sendable () -> Bool) throws -> Bool {
        guard let media = try entry(identity.registryEntryId) else { return false }
        defer { IOObjectRelease(media) }
        guard let backing = try backing(media) else { return false }
        defer { IOObjectRelease(backing) }
        guard try id(backing) == identity.backingEntryId else { return false }
        let raw = try pin(identity.device, media: media)
        defer { Darwin.close(raw) }
        let volume = try volume(identity.device)
        guard volume.id == identity.volumeId else { return false }
        if let mount = volume.mount {
            guard mount.path == identity.mountPoint else { return false }
            let root = try rootHandle(mount, device: identity.device, identity: identity.rootIdentity)
            let unmounted: Bool
            if #available(macOS 16, *) { unmounted = permitted() && funmount(root, 0) == 0 }
            else { unmounted = false }
            Darwin.close(root)
            guard unmounted else { return false }
        }
        guard permitted() else { return false }
        // sys/disk.h DKIOCEJECT: _IO('d', 21). The retained descriptor binds the original
        // minor/media; reopening its BSD name after validation would lose that guarantee.
        return ioctl(raw, UInt(0x20006415), 0) == 0
    }

    private static func absent(_ identity: NativeRamDiskIdentity, permitted: @Sendable () -> Bool) throws -> Bool {
        let media = try entry(identity.registryEntryId)
        defer { if let media { IOObjectRelease(media) } }
        if media != nil { return false }
        let backing = try entry(identity.backingEntryId)
        defer { if let backing { IOObjectRelease(backing) } }
        if backing != nil { return false }
        // Optimized ID lookups can return success+null even when their iterator could not
        // be allocated. A complete valid global scan must confirm both original IDs absent.
        var iterator: io_iterator_t = 0
        guard permitted(), let query = IOServiceMatching("IOService"),
              IOServiceGetMatchingServices(kIOMainPortDefault, query, &iterator) == KERN_SUCCESS, iterator != 0 else { throw Unconfirmed() }
        defer { IOObjectRelease(iterator) }
        let found = try NativeRegistryIterator.readSingle(next: { IOIteratorNext(iterator) },
            isValid: { permitted() && IOIteratorIsValid(iterator) != 0 }, release: { IOObjectRelease($0) }, accepts: {
                guard permitted() else { throw Unconfirmed() }
                let value = try id($0)
                return value == identity.registryEntryId || value == identity.backingEntryId
            })
        defer { if let found { IOObjectRelease(found) } }
        return found == nil
    }

    private static func pin(_ device: String, media: io_service_t) throws -> Int32 {
        guard device.hasPrefix("/dev/disk"), device.dropFirst(9).allSatisfy({ $0.isASCII && $0.isNumber }),
              !device.dropFirst(9).isEmpty, try property(media, "BSD Name") as? String == String(device.dropFirst(5)),
              IOObjectConformsTo(media, "IOMedia") != 0 else { throw Unconfirmed() }
        let original = try id(media)
        let rawName = "/dev/r" + device.dropFirst(5)
        let descriptor = Darwin.open(String(rawName), O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW)
        guard descriptor >= 0 else { throw Unconfirmed() }
        do {
            var info = stat(), block = stat()
            var name = [CChar](repeating: 0, count: 128)
            guard fstat(descriptor, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFCHR),
                  devname_r(info.st_rdev, mode_t(S_IFCHR), &name, Int32(name.count)) != nil,
                  string(name) == String(rawName.dropFirst(5)), lstat(device, &block) == 0,
                  block.st_mode & mode_t(S_IFMT) == mode_t(S_IFBLK),
                  UInt64(UInt32(bitPattern: block.st_rdev) >> 24) == (try number(media, "BSD Major")),
                  UInt64(UInt32(bitPattern: block.st_rdev) & 0x00ffffff) == (try number(media, "BSD Minor")),
                  UInt64(UInt32(bitPattern: info.st_rdev) & 0x00ffffff) == (try number(media, "BSD Minor")),
                  let current = try namedMedia(device) else { throw Unconfirmed() }
            defer { IOObjectRelease(current) }
            guard try id(current) == original else { throw Unconfirmed() }
            return descriptor
        } catch { Darwin.close(descriptor); throw error }
    }

    private static func rootHandle(_ path: URL, device: String, identity: String) throws -> Int32 {
        let descriptor = Darwin.open(path.path, O_RDONLY | O_DIRECTORY | O_EVTONLY | O_CLOEXEC | O_NOFOLLOW)
        guard descriptor >= 0 else { throw Unconfirmed() }
        var info = stat(), filesystem = statfs()
        guard fstat(descriptor, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR),
              "darwin:\(UInt32(bitPattern: info.st_dev)):\(info.st_ino)" == identity,
              fstatfs(descriptor, &filesystem) == 0,
              withUnsafeBytes(of: filesystem.f_mntfromname, { bytes in
                  String(decoding: bytes.prefix(while: { $0 != 0 }), as: UTF8.self) == device
              }) else { Darwin.close(descriptor); throw Unconfirmed() }
        return descriptor
    }

    private static func volume(_ device: String) throws -> Volume {
        guard let session = DASessionCreate(kCFAllocatorDefault),
              let disk = DADiskCreateFromBSDName(kCFAllocatorDefault, session, device),
              let description = DADiskCopyDescription(disk),
              let raw = CFDictionaryGetValue(description, Unmanaged.passUnretained(kDADiskDescriptionVolumeUUIDKey).toOpaque())
        else { throw Unconfirmed() }
        let object = Unmanaged<CFTypeRef>.fromOpaque(raw).takeUnretainedValue()
        guard CFGetTypeID(object) == CFUUIDGetTypeID(),
              let id = UUID(uuidString: CFUUIDCreateString(kCFAllocatorDefault, unsafeDowncast(object, to: CFUUID.self)) as String)
        else { throw Unconfirmed() }
        var mount: URL?
        if let raw = CFDictionaryGetValue(description, Unmanaged.passUnretained(kDADiskDescriptionVolumePathKey).toOpaque()) {
            let object = Unmanaged<CFTypeRef>.fromOpaque(raw).takeUnretainedValue()
            guard CFGetTypeID(object) == CFURLGetTypeID() else { throw Unconfirmed() }
            mount = unsafeDowncast(object, to: CFURL.self) as URL
            guard mount?.isFileURL == true else { throw Unconfirmed() }
        }
        return Volume(id: id, mount: mount)
    }

    private static func entry(_ target: UInt64) throws -> io_service_t? { try matching(IORegistryEntryIDMatching(target)) }
    private static func namedMedia(_ device: String) throws -> io_service_t? {
        try matching(IOBSDNameMatching(kIOMainPortDefault, 0, String(device.dropFirst(5))))
    }
    private static func matching(_ query: CFMutableDictionary?) throws -> io_service_t? {
        guard let query else { throw Unconfirmed() }
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, query, &iterator) == KERN_SUCCESS else { throw Unconfirmed() }
        if iterator == 0 { return nil }
        defer { IOObjectRelease(iterator) }
        return try NativeRegistryIterator.readSingle(next: { IOIteratorNext(iterator) },
            isValid: { IOIteratorIsValid(iterator) != 0 }, release: { IOObjectRelease($0) }, maximum: 16)
    }
    private static func id(_ object: io_service_t) throws -> UInt64 {
        var id: UInt64 = 0
        guard IORegistryEntryGetRegistryEntryID(object, &id) == KERN_SUCCESS, id > 0 else { throw Unconfirmed() }
        return id
    }
    private static func backing(_ media: io_service_t) throws -> io_service_t? {
        guard IOObjectRetain(media) == KERN_SUCCESS else { throw Unconfirmed() }
        var current = media
        for _ in 0..<8 {
            if IOObjectConformsTo(current, "IOHDIXHDDriveOutKernel") != 0 || IOObjectConformsTo(current, "IOHDIXHDDriveInKernel") != 0 {
                return current
            }
            var parent: io_registry_entry_t = 0
            let status = IORegistryEntryGetParentEntry(current, kIOServicePlane, &parent)
            IOObjectRelease(current)
            guard status == KERN_SUCCESS else { return nil }
            current = parent
        }
        IOObjectRelease(current)
        return nil
    }
    private static func property(_ object: io_service_t, _ key: String) throws -> CFTypeRef {
        guard let value = IORegistryEntryCreateCFProperty(object, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() else {
            throw Unconfirmed()
        }
        return value
    }
    private static func number(_ object: io_service_t, _ key: String) throws -> UInt64 {
        let value = try property(object, key)
        guard CFGetTypeID(value) == CFNumberGetTypeID(), let number = value as? NSNumber, number.int64Value >= 0 else {
            throw Unconfirmed()
        }
        return UInt64(number.int64Value)
    }
    private static func string(_ bytes: [CChar]) -> String {
        String(decoding: bytes.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
    }
}
