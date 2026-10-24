import Darwin
import Foundation

/// The shared helper owns lease renewal, transfers, native tools and final evidence. The app
/// owns admission and keeps its reservation until the helper and owned RAM volume have ended.
public struct NativeQualityJobRunner: WorkExecutor {
    private let client: SidecarClient
    private let tools: NativeQualityTools
    private let helper: any NativeQualityExchanging
    private let scratchRoot: URL
    private let settings: SettingsSnapshot
    private let memoryBudget: MemoryWorkBudget
    private let availableMemory: @Sendable () -> Int64?
    private let workspace: @Sendable (URL, NativeScratchScope) -> NativeDiskCapacity?
    private let runtime: NativeWorkRuntime
    private let ramFactory: NativeRamDiskFactory
    private let mountParent: URL?
    private let availableScratch: @Sendable (URL) -> Int64?

    public init(client: SidecarClient = SidecarClient(), tools: NativeQualityTools,
                helper: (any NativeQualityExchanging)? = nil, scratchRoot: URL = JobRunner.defaultScratchRoot(),
                settings: SettingsSnapshot = SettingsSnapshot(), memoryBudget: MemoryWorkBudget = .shared,
                runtime: NativeWorkRuntime = .shared,
                availableMemory: @escaping @Sendable () -> Int64? = MacMemoryAvailability.read,
                workspace: (@Sendable (URL, NativeScratchScope) -> NativeDiskCapacity?)? = nil,
                ramFactory: NativeRamDiskFactory = NativeRamDiskFactory(), mountParent: URL? = nil,
                availableScratch: @escaping @Sendable (URL) -> Int64? = JobRunner.availableScratchBytes) {
        self.client = client; self.tools = tools; self.helper = helper ?? NativeQualityHost(executable: tools.host)
        self.scratchRoot = scratchRoot; self.settings = settings; self.memoryBudget = memoryBudget
        self.availableMemory = availableMemory; self.workspace = workspace ?? MacNativeWorkspace.read
        self.runtime = runtime; self.ramFactory = ramFactory
        self.mountParent = mountParent; self.availableScratch = availableScratch
    }

    public func execute(_ assignment: Assignment, pairing: StoredPairing,
                        progress: @escaping @Sendable (JobProgress) -> Void,
                        preview: @escaping @Sendable (Data) -> Void = { _ in },
                        storage: @escaping @Sendable (WorkStorage) -> Void = { _ in }) async -> JobOutcome {
        guard let native = assignment.nativeQuality, native.jobId == assignment.jobId, native.leaseId == assignment.leaseId else {
            return await release(assignment, pairing, "The native assignment identity could not be established.")
        }
        if Task.isCancelled { return await interrupt(assignment, pairing, "Native work was cancelled on this Mac.") }
        let snapshot = settings.current
        let root: URL
        if case let .folder(folder) = snapshot.workLocation { root = folder } else { root = scratchRoot }
        guard let attempt = await runtime.begin(roots: [scratchRoot, root, FileManager.default.temporaryDirectory]) else {
            return await release(assignment, pairing, "Native accounting or working-file recovery is not ready; existing work was retained.", capacity: true)
        }
        var owner: NativeAccountedOwner?
        var terminal: NativeQualityReply?
        var creation: NativeRamDiskCreation?
        let outcome = await executeAttempt(assignment, native: native, pairing: pairing, root: root, snapshot: snapshot,
            attempt: attempt, owner: &owner, terminal: &terminal, ram: &creation, progress: progress, storage: storage)
        let finalOwner = owner, finalReply = terminal, runtime = self.runtime
        _ = await Task.detached(priority: .utility) {
            await runtime.end(attempt, owner: finalOwner, reply: finalReply)
        }.value
        withExtendedLifetime(creation) {}
        return outcome
    }

    private func executeAttempt(_ assignment: Assignment, native: NativeQualityAssignment, pairing: StoredPairing,
                                root diskRoot: URL, snapshot: (workLocation: WorkLocation, memoryBudgetBytes: Int64), attempt: NativeWorkAttempt,
                                owner: inout NativeAccountedOwner?, terminal: inout NativeQualityReply?,
                                ram: inout NativeRamDiskCreation?,
                                progress: @escaping @Sendable (JobProgress) -> Void,
                                storage: @escaping @Sendable (WorkStorage) -> Void) async -> JobOutcome {
        do {
            try Task.checkCancellation()
            guard let leaseId = UUID(uuidString: assignment.leaseId) else { throw NativeWorkRegistryError.invalid }
            try await client.renew(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId)
            let request = try native.request(mode: "requirements", tools: tools, scratchRoot: scratchRoot, version: 2)
            let requirements = try await requirements(request, jobId: assignment.jobId)
            guard requirements.version == 2, requirements.type == "requirements", requirements.jobId == assignment.jobId,
                  let peak = requirements.nativeMemoryBytes, peak > 0,
                  let scratch = requirements.requiredScratchBytes, scratch > 0 else { throw NativeQualityHostError() }
            var root = diskRoot
            var fallback: String?
            let service = NativeResourceRetirementService(tools: tools, helper: helper)
            var preparedScope: NativeScratchScope?
            var reservedDisk: NativeDiskCapacity?
            if snapshot.workLocation == .memory, let allocated = RamDisk.allocationBytes(for: scratch) {
                do {
                    owner = try await attempt.registry.reserve(jobId: assignment.jobId, leaseId: leaseId,
                        nativeBytes: peak, scratchBytes: scratch, ramBytes: allocated, ramLimit: snapshot.memoryBudgetBytes,
                        availableMemory: availableMemory(), disk: nil)
                } catch NativeWorkRegistryError.capacity { /* Fresh disk admission is still allowed before allocation. */ }
                if let ramOwner = owner {
                    let parent = try await prepareMountParent()
                    ram = try await attempt.registry.createRamStorage(owner: ramOwner, factory: ramFactory, mountParent: parent)
                    try Task.checkCancellation()
                    if ram == nil {
                        guard try await attempt.registry.finish(owner: ramOwner) else {
                            return await release(assignment, pairing, "RAM creation cleanup remains unconfirmed; its reservation is retained.", capacity: true)
                        }
                        owner = nil
                    }
                }
                if let ram { root = ram.disk.mountPoint }
                else { fallback = "RAM storage is unavailable or exceeds current capacity; this job uses disk." }
            }
            if owner == nil {
                let scope = try await service.prepareScope(root: root)
                let recovered = try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root, scope: scope)
                guard recovered.busy == 0, recovered.deferred == 0, recovered.invalid == 0,
                      !recovered.scanLimited, recovered.nextOwner == nil, let disk = workspace(root, scope) else {
                    return await release(assignment, pairing, "Disk working space is insufficient or its volume cannot be established.", capacity: true)
                }
                preparedScope = scope; reservedDisk = disk
                do {
                    owner = try await attempt.registry.reserve(jobId: assignment.jobId, leaseId: leaseId,
                        nativeBytes: peak, scratchBytes: scratch, ramBytes: 0, ramLimit: snapshot.memoryBudgetBytes,
                        availableMemory: availableMemory(), disk: disk)
                } catch NativeWorkRegistryError.capacity {
                    return await release(assignment, pairing, "Native memory or disk working space is insufficient or cannot be established.", capacity: true)
                }
            }
            guard let owner else { throw NativeWorkRegistryError.unavailable }
            if ram != nil, availableScratch(root).map({ $0 >= scratch }) != true {
                return await release(assignment, pairing, "The owned RAM volume cannot hold the required working space.", capacity: true)
            }
            let scope: NativeScratchScope
            if let preparedScope { scope = preparedScope }
            else { scope = try await service.prepareScope(root: root) }
            try await attempt.registry.prepare(owner: owner, root: root, scope: scope, ramIdentity: ram?.identity)
            try Task.checkCancellation()
            guard memoryBudget.remainsAdmitted(owner.owner, availableMemory: availableMemory()) else {
                return await release(assignment, pairing, "Available memory changed while preparing working space.", capacity: true)
            }
            storage(WorkStorage(inMemory: ram != nil, path: root.path, fallbackReason: fallback))
            let grant = peak.addingReportingOverflow(ram == nil ? 0 : RamDisk.allocationBytes(for: scratch) ?? Int64.max)
            guard !grant.overflow else { throw NativeQualityHostError() }
            try await client.renew(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId)
            let run = try native.request(mode: "run", tools: tools, scratchRoot: root, pairing: pairing,
                grantedMemoryBytes: grant.partialValue, version: 2, owner: owner, accountingScope: scope)
            try Task.checkCancellation()
            guard try await service.prepareScope(root: root) == scope else { throw NativeWorkRegistryError.invalid }
            if let reservedDisk {
                guard let current = workspace(root, scope), current.volume == reservedDisk.volume,
                      current.availableBytes >= scratch else {
                    return await release(assignment, pairing, "The working volume changed before encoding; existing work was retained.", capacity: true)
                }
            }
            try Task.checkCancellation()
            guard memoryBudget.remainsAdmitted(owner.owner, availableMemory: availableMemory()) else {
                return await release(assignment, pairing, "Available memory changed before encoding; the reservation was retained through cleanup.", capacity: true)
            }
            try await attempt.registry.markRunning(owner: owner)
            let result = try await helper.exchange(request: run, expectedJobId: assignment.jobId) { event in
                if let value = Self.progress(event) { progress(value) }
            }
            guard result.version == 2, result.type == "completed", result.jobId == assignment.jobId,
                  result.nativeWorkspaceCreated != nil, result.nativeResourcesRetired != nil else { throw NativeQualityHostError() }
            terminal = result
            if result.delivered { return .delivered(jobId: assignment.jobId, bytes: nil) }
            if result.capacityBlocked { return .capacityBlocked(jobId: assignment.jobId, reason: "Runtime capacity changed; the server acknowledged the hand-back.") }
            return await release(assignment, pairing, "The shared engine stopped this attempt. Check the server's retained outcome.")
        } catch let error as SidecarError where error.endsTheLease {
            return .leaseLost(jobId: assignment.jobId, reason: "The server ended this worker's lease before media work could proceed.")
        } catch is CancellationError {
            return await interrupt(assignment, pairing, "Native work was cancelled on this Mac.")
        } catch {
            return await interrupt(assignment, pairing, "The shared native helper did not complete this assignment. Check its recorded server outcome.")
        }
    }

    private func prepareMountParent() async throws -> URL {
        if let mountParent { return mountParent }
        return try await Task.detached(priority: .utility) {
            guard let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first else {
                throw NativeWorkRegistryError.unavailable
            }
            let root = base.appendingPathComponent("OptimisarrSidecar/native-ram", isDirectory: true)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            return root
        }.value
    }

    public static func availableDiskWorkspace(at root: URL) -> NativeDiskCapacity? { MacNativeWorkspace.read(root) }

    static func progress(_ event: NativeQualityEvent) -> JobProgress? {
        guard event.type == "progress", let raw = event.stage, let stage = NativeWorkStage(rawValue: raw) else { return nil }
        if stage == .encoding, let seconds = event.encodedSeconds { return .encoding(encodedSeconds: seconds) }
        return .stageOnly(stage)
    }

    private func requirements(_ request: Data, jobId: Int) async throws -> NativeQualityReply {
        try await withThrowingTaskGroup(of: NativeQualityReply.self) { group in
            group.addTask { try await helper.exchange(request: request, expectedJobId: jobId, report: { _ in }) }
            group.addTask { try await Task.sleep(for: .seconds(10)); throw NativeQualityHostError() }
            defer { group.cancelAll() }
            guard let reply = try await group.next() else { throw NativeQualityHostError() }
            return reply
        }
    }

    private func release(_ assignment: Assignment, _ pairing: StoredPairing, _ reason: String, capacity: Bool = false) async -> JobOutcome {
        if Task.isCancelled { return await interrupt(assignment, pairing, "Native work was cancelled on this Mac.") }
        do {
            try await client.release(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId, capacityUnavailable: capacity)
            return capacity ? .capacityBlocked(jobId: assignment.jobId, reason: reason) : .released(jobId: assignment.jobId, reason: reason)
        } catch let error as SidecarError where error.endsTheLease {
            return .leaseLost(jobId: assignment.jobId, reason: reason)
        } catch {
            if Task.isCancelled { return await interrupt(assignment, pairing, "Native work was cancelled on this Mac.") }
            return .unconfirmed(jobId: assignment.jobId, reason: "\(reason) The hand-back was not acknowledged.")
        }
    }

    private func interrupt(_ assignment: Assignment, _ pairing: StoredPairing, _ reason: String) async -> JobOutcome {
        // A cancelled job cannot inherit cancellation into its final control-plane request.
        // URLSession still imposes the ordinary bounded request timeout on this independent task.
        await Task.detached {
            do {
                try await client.releaseForInterruption(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId)
                return .released(jobId: assignment.jobId, reason: reason)
            } catch let error as SidecarError where error.endsTheLease {
                return .leaseLost(jobId: assignment.jobId, reason: reason)
            } catch { return .unconfirmed(jobId: assignment.jobId, reason: "\(reason) The hand-back was not acknowledged.") }
        }.value
    }
}

/// Custom disk-image/RAM/network or unknown volumes cannot borrow a physical-disk assumption.
/// Owned RAM volumes are admitted separately with their complete allocation charged above.
public struct NativeDiskCapacity: Sendable {
    public let volume: String
    public let availableBytes: Int64
    public init(volume: String, availableBytes: Int64) { self.volume = volume; self.availableBytes = availableBytes }
}

enum MacNativeWorkspace {
    static func read(_ root: URL, scope: NativeScratchScope) -> NativeDiskCapacity? {
        guard root.isFileURL, let canonical = realpath(root.path, nil) else { return nil }
        defer { free(canonical) }
        let path = String(cString: canonical)
        let descriptor = Darwin.open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
        guard descriptor >= 0 else { return nil }
        defer { Darwin.close(descriptor) }
        var identity = stat(), filesystem = statfs()
        guard fstat(descriptor, &identity) == 0,
              "darwin:\(UInt32(bitPattern: identity.st_dev)):\(identity.st_ino)" == scope.root,
              fstatfs(descriptor, &filesystem) == 0, filesystem.f_flags & UInt32(MNT_LOCAL) != 0,
              let device = withUnsafeBytes(of: filesystem.f_mntfromname, { deviceName(Data($0)) }),
              let output = RamDisk.run("/usr/sbin/diskutil", ["info", "-plist", device], timeout: 5),
              let plist = try? PropertyListSerialization.propertyList(from: Data(output.utf8), format: nil) as? [String: Any],
              isPhysicalDisk(plist), let volume = (plist["ParentWholeDisk"] ?? plist["DeviceIdentifier"]) as? String,
              !volume.isEmpty, fstatfs(descriptor, &filesystem) == 0 else { return nil }
        // Capacity comes from the retained original root, never a second pathname lookup.
        let bytes = UInt64(filesystem.f_bavail).multipliedReportingOverflow(by: UInt64(filesystem.f_bsize))
        guard !bytes.overflow, bytes.partialValue <= UInt64(Int64.max) else { return nil }
        return NativeDiskCapacity(volume: volume, availableBytes: Int64(bytes.partialValue))
    }

    static func read(_ root: URL) -> NativeDiskCapacity? {
        var existing = root.resolvingSymlinksInPath()
        while !FileManager.default.fileExists(atPath: existing.path) {
            let parent = existing.deletingLastPathComponent()
            guard parent != existing else { return nil }
            existing = parent
        }
        guard let values = try? existing.resourceValues(forKeys: [.volumeIsLocalKey]),
              values.volumeIsLocal == true, let mountedDevice = device(at: existing),
              let output = RamDisk.run("/usr/sbin/diskutil", ["info", "-plist", mountedDevice], timeout: 5),
              let plist = try? PropertyListSerialization.propertyList(from: Data(output.utf8), format: nil) as? [String: Any],
              isPhysicalDisk(plist),
              let device = (plist["ParentWholeDisk"] ?? plist["DeviceIdentifier"]) as? String, !device.isEmpty,
              let free = JobRunner.availableScratchBytes(at: existing) else { return nil }
        // APFS volumes in one container share space, including when addressed through aliases.
        return NativeDiskCapacity(volume: device, availableBytes: free)
    }

    static func device(at root: URL) -> String? {
        let descriptor = Darwin.open(root.path, O_RDONLY | O_CLOEXEC)
        guard descriptor >= 0 else { return nil }
        defer { Darwin.close(descriptor) }
        var info = statfs()
        guard Darwin.fstatfs(descriptor, &info) == 0 else { return nil }
        return withUnsafeBytes(of: info.f_mntfromname) { deviceName(Data($0)) }
    }

    static func deviceName(_ bytes: Data) -> String? {
        guard let end = bytes.firstIndex(of: 0), let name = String(data: bytes[..<end], encoding: .utf8),
              name.hasPrefix("/dev/disk"), name.dropFirst(9).allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }),
              name.count > 9 else { return nil }
        return name
    }

    static func isPhysicalDisk(_ plist: [String: Any]) -> Bool {
        guard plist["WritableVolume"] as? Bool == true, let bus = plist["BusProtocol"] as? String,
              ["Apple Fabric", "PCI-Express", "SATA", "USB", "Thunderbolt", "FireWire", "ATAPI", "NVMe", "SAS", "SCSI"].contains(bus),
              plist["DiskImage"] as? Bool != true else { return false }
        return true
    }
}
