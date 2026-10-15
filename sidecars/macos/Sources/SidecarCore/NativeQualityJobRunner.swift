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
    private let workspace: @Sendable (URL) -> NativeDiskCapacity?
    private let diskBudget: DiskWorkBudget
    private let makeRamDisk: @Sendable (Int64) -> RamDisk?
    private let destroyRamDisk: @Sendable (RamDisk) -> Bool
    private let availableScratch: @Sendable (URL) -> Int64?

    public init(client: SidecarClient = SidecarClient(), tools: NativeQualityTools,
                helper: (any NativeQualityExchanging)? = nil, scratchRoot: URL = JobRunner.defaultScratchRoot(),
                settings: SettingsSnapshot = SettingsSnapshot(), memoryBudget: MemoryWorkBudget = .shared,
                availableMemory: @escaping @Sendable () -> Int64? = MacMemoryAvailability.read,
                workspace: (@Sendable (URL) -> NativeDiskCapacity?)? = nil, diskBudget: DiskWorkBudget = .shared,
                makeRamDisk: @escaping @Sendable (Int64) -> RamDisk? = RamDisk.create,
                destroyRamDisk: @escaping @Sendable (RamDisk) -> Bool = { $0.destroy() },
                availableScratch: @escaping @Sendable (URL) -> Int64? = JobRunner.availableScratchBytes) {
        self.client = client; self.tools = tools; self.helper = helper ?? NativeQualityHost(executable: tools.host)
        self.scratchRoot = scratchRoot; self.settings = settings; self.memoryBudget = memoryBudget
        self.availableMemory = availableMemory; self.workspace = workspace ?? MacNativeWorkspace.read
        self.diskBudget = diskBudget; self.makeRamDisk = makeRamDisk
        self.destroyRamDisk = destroyRamDisk; self.availableScratch = availableScratch
    }

    public func execute(_ assignment: Assignment, pairing: StoredPairing,
                        progress: @escaping @Sendable (JobProgress) -> Void,
                        preview: @escaping @Sendable (Data) -> Void = { _ in },
                        storage: @escaping @Sendable (WorkStorage) -> Void = { _ in }) async -> JobOutcome {
        guard let native = assignment.nativeQuality, native.jobId == assignment.jobId, native.leaseId == assignment.leaseId else {
            return await release(assignment, pairing, "The native assignment identity could not be established.")
        }
        var reservation: UUID?
        var ram: RamDisk?
        var diskReservation: UUID?
        defer {
            if let diskReservation { diskBudget.release(diskReservation) }
            let ejected = ram.map(destroyRamDisk) ?? true
            if let reservation { memoryBudget.release(reservation, retainRam: !ejected) }
        }
        do {
            try Task.checkCancellation()
            try await client.renew(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId)
            let request = try native.request(mode: "requirements", tools: tools, scratchRoot: scratchRoot)
            let requirements = try await requirements(request, jobId: assignment.jobId)
            guard requirements.type == "requirements", requirements.jobId == assignment.jobId,
                  let peak = requirements.nativeMemoryBytes, peak > 0,
                  let scratch = requirements.requiredScratchBytes, scratch > 0 else { throw NativeQualityHostError() }
            let snapshot = settings.current
            var root = scratchRoot
            var fallback: String?
            if case let .folder(folder) = snapshot.workLocation { root = folder }
            do { _ = try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root) }
            catch is CancellationError { throw CancellationError() }
            catch {
                return await release(assignment, pairing, "Native working-file ownership could not be checked; existing files were retained.", capacity: true)
            }
            if snapshot.workLocation == .memory {
                if let allocated = RamDisk.allocationBytes(for: scratch),
                   let token = memoryBudget.reserve(nativeBytes: peak, ramBytes: allocated,
                        ramLimit: snapshot.memoryBudgetBytes, availableMemory: availableMemory()) {
                    reservation = token
                    if let volume = makeRamDisk(scratch) { ram = volume; root = volume.mountPoint }
                    else { memoryBudget.release(token); reservation = nil }
                }
                if ram == nil { fallback = "RAM storage is unavailable or exceeds current capacity; this job uses disk." }
            }
            if ram == nil {
                guard let disk = workspace(root),
                      let diskToken = diskBudget.reserve(volume: disk.volume, bytes: scratch, available: disk.availableBytes) else {
                    return await release(assignment, pairing, "Disk working space is insufficient or its volume cannot be established.", capacity: true)
                }
                diskReservation = diskToken
                guard let token = memoryBudget.reserve(nativeBytes: peak, ramBytes: 0,
                        ramLimit: snapshot.memoryBudgetBytes, availableMemory: availableMemory()) else {
                    return await release(assignment, pairing, "Native memory or disk working space is insufficient or cannot be established.", capacity: true)
                }
                reservation = token
            } else if availableScratch(root).map({ $0 >= scratch }) != true {
                return await release(assignment, pairing, "The owned RAM volume cannot hold the required working space.", capacity: true)
            }
            try Task.checkCancellation()
            guard let reservation, memoryBudget.remainsAdmitted(reservation, availableMemory: availableMemory()) else {
                return await release(assignment, pairing, "Available memory changed while preparing working space.", capacity: true)
            }
            storage(WorkStorage(inMemory: ram != nil, path: root.path, fallbackReason: fallback))
            let grant = peak.addingReportingOverflow(ram == nil ? 0 : RamDisk.allocationBytes(for: scratch) ?? Int64.max)
            guard !grant.overflow else { throw NativeQualityHostError() }
            try await client.renew(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId)
            let run = try native.request(mode: "run", tools: tools, scratchRoot: root, pairing: pairing, grantedMemoryBytes: grant.partialValue)
            let result = try await helper.exchange(request: run, expectedJobId: assignment.jobId) { event in
                if let value = Self.progress(event) { progress(value) }
            }
            guard result.type == "completed", result.jobId == assignment.jobId else { throw NativeQualityHostError() }
            if result.delivered { return .delivered(jobId: assignment.jobId, bytes: nil) }
            if result.capacityBlocked { return .capacityBlocked(jobId: assignment.jobId, reason: "Runtime capacity changed; the server acknowledged the hand-back.") }
            return await release(assignment, pairing, "The shared engine stopped this attempt. Check the server's retained outcome.")
        } catch let error as SidecarError where error.endsTheLease {
            return .leaseLost(jobId: assignment.jobId, reason: "The server ended this worker's lease before media work could proceed.")
        } catch is CancellationError {
            return await release(assignment, pairing, "Native work was cancelled on this Mac.")
        } catch {
            return await release(assignment, pairing, "The shared native helper did not complete this assignment. Check its recorded server outcome.")
        }
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
        do {
            try await client.release(serverAddress: pairing.serverAddress, credential: pairing.credential, leaseId: assignment.leaseId, capacityUnavailable: capacity)
            return capacity ? .capacityBlocked(jobId: assignment.jobId, reason: reason) : .released(jobId: assignment.jobId, reason: reason)
        } catch let error as SidecarError where error.endsTheLease {
            return .leaseLost(jobId: assignment.jobId, reason: reason)
        } catch { return .unconfirmed(jobId: assignment.jobId, reason: "\(reason) The hand-back was not acknowledged.") }
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
