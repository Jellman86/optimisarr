import Foundation
import IOKit

/// What the GPU is doing right now, as a fraction from 0 to 1.
public struct GpuUsage: Sendable, Equatable {
    /// The accelerator's overall busy figure.
    public let device: Double
    /// Bytes of system memory the accelerator currently holds.
    public let memoryInUse: Int64

    public init(device: Double, memoryInUse: Int64) {
        self.device = device
        self.memoryInUse = memoryInUse
    }
}

/// Reads `PerformanceStatistics` out of an IOAccelerator registry entry.
///
/// Split from the IOKit walk so the arithmetic and the missing-key cases can be tested without a
/// GPU. Apple publishes no contract for these keys, so every one of them is treated as optional
/// and a dictionary that does not carry a utilisation figure yields nothing rather than a zero
/// that would read as "idle".
public enum GpuStatisticsParser {
    public static func parse(_ statistics: [String: Any]) -> GpuUsage? {
        guard let percent = number(statistics["Device Utilization %"]) else { return nil }
        return GpuUsage(
            device: min(max(percent / 100, 0), 1),
            memoryInUse: Int64(number(statistics["In use system memory"]) ?? 0))
    }

    /// The registry hands these back as `NSNumber`, but a build that reports a string should not
    /// take the reading down with it.
    private static func number(_ value: Any?) -> Double? {
        switch value {
        case let n as NSNumber: return n.doubleValue
        case let s as String: return Double(s)
        default: return nil
        }
    }
}

/// Samples the GPU, so the menu can say whether this Mac is actually working.
///
/// **What this does not cover.** VideoToolbox encodes on the media engine, a fixed-function block
/// that is not the GPU's shader cores and is not exposed by any public interface. A hardware
/// encode can therefore read low here while the Mac is entirely busy. The number is honest about
/// the GPU; the menu says which encoder is running so the two are read together.
public enum GpuMonitor {
    public static func sample() -> GpuUsage? {
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(
            kIOMainPortDefault, IOServiceMatching("IOAccelerator"), &iterator) == KERN_SUCCESS
        else { return nil }
        defer { IOObjectRelease(iterator) }

        // A Mac can list more than one accelerator. The first that reports a utilisation figure is
        // taken; on Apple Silicon there is exactly one that does.
        while case let entry = IOIteratorNext(iterator), entry != 0 {
            defer { IOObjectRelease(entry) }
            var properties: Unmanaged<CFMutableDictionary>?
            guard IORegistryEntryCreateCFProperties(
                entry, &properties, kCFAllocatorDefault, 0) == KERN_SUCCESS,
                let dictionary = properties?.takeRetainedValue() as? [String: Any],
                let statistics = dictionary["PerformanceStatistics"] as? [String: Any],
                let usage = GpuStatisticsParser.parse(statistics)
            else { continue }
            return usage
        }
        return nil
    }
}
