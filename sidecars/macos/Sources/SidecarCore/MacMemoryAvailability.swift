import Darwin
import Foundation

/// Admission estimate, not total installed RAM. No swap or compressed pages fund native work.
/// Apple counts speculative pages inside free_count; never add them a second time.
public enum MacMemoryAvailability {
    public static func read() -> Int64? {
        guard pressureLevel() == 1 else { return nil }
        let host = mach_host_self()
        defer { mach_port_deallocate(mach_task_self_, host) }
        var pageBytes: vm_size_t = 0
        guard host_page_size(host, &pageBytes) == KERN_SUCCESS else { return nil }
        var statistics = vm_statistics64_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &statistics) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(host, HOST_VM_INFO64, $0, &count)
            }
        }
        // Only the first three natural_t counters are used. Later kernel revisions can return
        // a shorter statistics record than the build SDK without invalidating those counters.
        guard result == KERN_SUCCESS, count >= 3, let pressure = pressureLevel() else { return nil }
        return estimate(freePages: UInt64(statistics.free_count), inactivePages: UInt64(statistics.inactive_count),
                        pageBytes: UInt64(pageBytes), physicalBytes: ProcessInfo.processInfo.physicalMemory,
                        pressureLevel: pressure)
    }

    static func estimate(freePages: UInt64, inactivePages: UInt64, pageBytes: UInt64,
                         physicalBytes: UInt64, pressureLevel: Int32) -> Int64? {
        guard pressureLevel == 1, pageBytes >= 4096, pageBytes <= 1024 * 1024,
              pageBytes.nonzeroBitCount == 1, physicalBytes > 0, physicalBytes <= Int64.max else { return nil }
        let pages = freePages.addingReportingOverflow(inactivePages)
        guard !pages.overflow else { return nil }
        let bytes = pages.partialValue.multipliedReportingOverflow(by: pageBytes)
        guard !bytes.overflow, bytes.partialValue <= physicalBytes else { return nil }
        // Inactive pages are an estimate of reclaimable memory, not a guarantee. Leave the host
        // at least 1 GiB or 10% of physical RAM and refuse every non-normal pressure state.
        let headroom = max(1024 * 1024 * 1024, physicalBytes / 10)
        return bytes.partialValue > headroom ? Int64(bytes.partialValue - headroom) : 0
    }

    private static func pressureLevel() -> Int32? {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        guard sysctlbyname("kern.memorystatus_vm_pressure_level", &value, &size, nil, 0) == 0,
              size == MemoryLayout<Int32>.size else { return nil }
        return value
    }
}
