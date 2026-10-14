import Foundation
import Testing
@testable import SidecarCore

@Suite("Shared RAM storage")
struct MemoryWorkTests {
    @Test("candidate space follows its limit and accounts for filesystem overhead")
    func sizing() throws {
        let plan = try #require(MemoryWorkPlan.make(sourceBytes: 100_000_000, maximumCandidateBytes: 90_000_000))
        #expect(plan.workingBytes >= 190_000_000)
        #expect(plan.allocatedBytes > plan.workingBytes)
        #expect(plan.allocatedBytes % 512 == 0)
    }

    @Test("unbounded, invalid and overflowing sizes cannot allocate RAM")
    func invalidSizes() {
        #expect(MemoryWorkPlan.make(sourceBytes: 100, maximumCandidateBytes: nil) == nil)
        #expect(MemoryWorkPlan.make(sourceBytes: 100, maximumCandidateBytes: 0) == nil)
        #expect(MemoryWorkPlan.make(sourceBytes: -1, maximumCandidateBytes: 100) == nil)
        #expect(MemoryWorkPlan.make(sourceBytes: Int64.max, maximumCandidateBytes: 100) == nil)
        #expect(RamDisk.allocationBytes(for: Int64.max) == nil)
    }

    @Test("simultaneous reservations share one limit and release is idempotent")
    func concurrentReservations() async {
        let budget = MemoryWorkBudget()
        let reservations = await withTaskGroup(of: UUID?.self, returning: [UUID].self) { group in
            for _ in 0..<20 { group.addTask { budget.reserve(nativeBytes: 0, ramBytes: 30, ramLimit: 100, availableMemory: 100) } }
            var held: [UUID] = []
            for await token in group { if let token { held.append(token) } }
            return held
        }
        #expect(reservations.count == 3)
        #expect(budget.reservedBytes == 90)
        for token in reservations { budget.release(token); budget.release(token) }
        #expect(budget.reservedBytes == 0)
        #expect(budget.reserve(nativeBytes: 0, ramBytes: 100, ramLimit: 100, availableMemory: 100) != nil)
        #expect(budget.reserve(nativeBytes: 0, ramBytes: 1, ramLimit: 50, availableMemory: 50) == nil)
    }
}

@Suite("Native and RAM admission")
struct NativeMemoryWorkTests {
    @Test("native peaks and legacy RAM storage cannot spend the same live memory")
    func sharedAdmission() throws {
        let budget = MemoryWorkBudget()
        let ram = try #require(budget.reserve(nativeBytes: 0, ramBytes: 30, ramLimit: 100, availableMemory: 100))
        #expect(budget.reserve(nativeBytes: 80, ramBytes: 0, ramLimit: 100, availableMemory: 100) == nil)
        let native = try #require(budget.reserve(nativeBytes: 60, ramBytes: 0, ramLimit: 100, availableMemory: 100))
        #expect(budget.reserve(nativeBytes: 0, ramBytes: 11, ramLimit: 100, availableMemory: 100) == nil)
        #expect(budget.reservedBytes == 90)
        #expect(budget.reservedRamBytes == 30)
        budget.release(native)
        budget.release(ram)
        #expect(budget.reservedBytes == 0)
    }

    @Test("native RAM scratch charges its entire allocation and retains RAM after failed ejection")
    func retainedRam() throws {
        let budget = MemoryWorkBudget()
        let token = try #require(budget.reserve(nativeBytes: 60, ramBytes: 30, ramLimit: 40, availableMemory: 100))
        budget.release(token, retainRam: true)
        #expect(budget.reservedBytes == 30)
        #expect(budget.reservedRamBytes == 30)
        #expect(budget.reserve(nativeBytes: 0, ramBytes: 11, ramLimit: 40, availableMemory: 100) == nil)
        budget.release(token)
        #expect(budget.reservedBytes == 0)
    }

    @Test("lowering the RAM storage preference does not block a native job working on disk")
    func nativeOnlyDoesNotSpendRamPreference() throws {
        let budget = MemoryWorkBudget()
        let ram = try #require(budget.reserve(nativeBytes: 0, ramBytes: 30, ramLimit: 100, availableMemory: 100))
        let native = try #require(budget.reserve(nativeBytes: 60, ramBytes: 0, ramLimit: 0, availableMemory: 100))
        budget.release(native)
        #expect(budget.reservedBytes == 30)
        budget.release(ram)
    }

    @Test("unknown, negative or overflowing memory cannot grant native work")
    func unknownCapacity() {
        let budget = MemoryWorkBudget()
        #expect(budget.reserve(nativeBytes: 1, ramBytes: 0, ramLimit: 10, availableMemory: nil) == nil)
        #expect(budget.reserve(nativeBytes: 1, ramBytes: 0, ramLimit: 10, availableMemory: -1) == nil)
        #expect(budget.reserve(nativeBytes: Int64.max, ramBytes: 1, ramLimit: Int64.max, availableMemory: Int64.max) == nil)
        #expect(budget.reserve(nativeBytes: -1, ramBytes: 2, ramLimit: 10, availableMemory: 10) == nil)
    }

    @Test("live memory reads retain host headroom and never add speculative pages twice")
    func memoryEstimate() {
        let gib: UInt64 = 1024 * 1024 * 1024
        #expect(MacMemoryAvailability.estimate(freePages: 2 * gib / 4096, inactivePages: gib / 4096,
            pageBytes: 4096, physicalBytes: 8 * gib, pressureLevel: 1) == Int64(2 * gib))
        #expect(MacMemoryAvailability.estimate(freePages: 1, inactivePages: 0,
            pageBytes: 4096, physicalBytes: 8 * gib, pressureLevel: 1) == 0)
        for pressure: Int32 in [0, 2, 4, 99] {
            #expect(MacMemoryAvailability.estimate(freePages: gib, inactivePages: 0,
                pageBytes: 4096, physicalBytes: 8 * gib, pressureLevel: pressure) == nil)
        }
        #expect(MacMemoryAvailability.estimate(freePages: UInt64.max, inactivePages: 1,
            pageBytes: 4096, physicalBytes: 8 * gib, pressureLevel: 1) == nil)
        #expect(MacMemoryAvailability.estimate(freePages: 1, inactivePages: 1,
            pageBytes: 0, physicalBytes: 8 * gib, pressureLevel: 1) == nil)
    }
}

@Suite("Live Mac memory", .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_MEMORY"] == "1"))
struct LiveMacMemoryTests {
    @Test("the actual kernel provider returns a bounded admission estimate")
    func liveReading() throws {
        let bytes = try #require(MacMemoryAvailability.read())
        #expect(bytes >= 0)
        #expect(UInt64(bytes) < ProcessInfo.processInfo.physicalMemory)
    }
}

@Suite("Native disk workspace admission")
struct NativeDiskWorkTests {
    @Test("folders and aliases on one volume share planned workspace while independent volumes do not")
    func sharedVolume() throws {
        let budget = DiskWorkBudget()
        let first = try #require(budget.reserve(volume: "container-1", bytes: 60, available: 100))
        #expect(budget.reserve(volume: "container-1", bytes: 41, available: 100) == nil)
        let second = try #require(budget.reserve(volume: "container-1", bytes: 40, available: 100))
        let other = try #require(budget.reserve(volume: "container-2", bytes: 100, available: 100))
        budget.release(first); budget.release(first)
        #expect(budget.reserve(volume: "container-1", bytes: 61, available: 100) == nil)
        budget.release(second); budget.release(other)
        #expect(budget.reserve(volume: "container-1", bytes: 100, available: 100) != nil)
    }

    @Test("unproved, invalid and overflowing workspace never becomes unlimited")
    func unknownDisk() {
        let budget = DiskWorkBudget()
        #expect(budget.reserve(volume: nil, bytes: 1, available: 100) == nil)
        #expect(budget.reserve(volume: "", bytes: 1, available: 100) == nil)
        #expect(budget.reserve(volume: "disk", bytes: -1, available: 100) == nil)
        #expect(budget.reserve(volume: "disk", bytes: 1, available: nil) == nil)
        #expect(budget.reserve(volume: "disk", bytes: Int64.max, available: Int64.max) != nil)
        #expect(budget.reserve(volume: "disk", bytes: 1, available: Int64.max) == nil)
    }

    @Test("unknown, read-only, image and RAM storage cannot masquerade as a proved physical disk")
    func diskClassification() {
        #expect(MacNativeWorkspace.isPhysicalDisk(["WritableVolume": true, "BusProtocol": "Apple Fabric"]))
        #expect(MacNativeWorkspace.isPhysicalDisk(["WritableVolume": true, "BusProtocol": "USB"]))
        for info: [String: Any] in [[:], ["WritableVolume": false, "BusProtocol": "USB"],
            ["WritableVolume": true, "BusProtocol": "Disk Image"],
            ["WritableVolume": true, "BusProtocol": "RAM"],
            ["WritableVolume": true, "BusProtocol": "Future bus"],
            ["WritableVolume": true, "BusProtocol": "USB", "DiskImage": true]] {
            #expect(!MacNativeWorkspace.isPhysicalDisk(info))
        }
    }
    @Test("kernel mount devices are read without confusing the logical root, paths or remote mounts")
    func kernelDeviceIdentity() {
        #expect(MacNativeWorkspace.deviceName(Data("/dev/disk3s5\0trailing".utf8)) == "/dev/disk3s5")
        #expect(MacNativeWorkspace.deviceName(Data("/dev/disk3s1s1\0".utf8)) == "/dev/disk3s1s1")
        for value in ["/dev/disk", "/dev/disk3s5", "/dev/disk3/../disk4\0", "host:/folder\0", "/System/Volumes/Data\0"] {
            #expect(MacNativeWorkspace.deviceName(Data(value.utf8)) == nil)
        }
    }

}
