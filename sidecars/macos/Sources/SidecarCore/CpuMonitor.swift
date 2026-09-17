import Darwin
import Foundation

/// A reading of the machine's cumulative CPU ticks, as the kernel counts them.
///
/// Cumulative rather than instantaneous: the kernel counts ticks since boot, so a single reading
/// says nothing on its own and two readings a moment apart say everything. Kept as a value so the
/// arithmetic below can be tested without a machine to be busy.
public struct CpuTicks: Sendable, Equatable {
    public let user: UInt64
    public let system: UInt64
    public let idle: UInt64
    public let nice: UInt64

    public init(user: UInt64, system: UInt64, idle: UInt64, nice: UInt64) {
        self.user = user
        self.system = system
        self.idle = idle
        self.nice = nice
    }

    public var busy: UInt64 { user &+ system &+ nice }
    public var total: UInt64 { busy &+ idle }
}

public enum CpuLoadCalculator {
    /// The fraction of the interval between two readings that was spent doing something, 0 to 1.
    ///
    /// Returns nil rather than a number wherever one would be a lie: no time passed between the
    /// readings, or the counters went backwards because the machine slept or the values wrapped.
    /// A sidecar reporting "0% busy" for those cases would read as an idle Mac, which is a
    /// different claim entirely from "cannot say".
    public static func busyFraction(from previous: CpuTicks, to current: CpuTicks) -> Double? {
        guard current.total >= previous.total, current.busy >= previous.busy else { return nil }
        let total = current.total - previous.total
        guard total > 0 else { return nil }
        let busy = current.busy - previous.busy
        return min(max(Double(busy) / Double(total), 0), 1)
    }
}

/// Machine-wide CPU busyness, sampled against the previous reading.
///
/// Whole machine rather than this process: a job's work is done by a separate FFmpeg process, and
/// the question an operator is asking of the Workers tab — can this Mac take more? — is about the
/// machine, not about the menu bar app.
public final class CpuMonitor: @unchecked Sendable {
    private let lock = NSLock()
    private var previous: CpuTicks?
    private let read: @Sendable () -> CpuTicks?

    public init(read: @escaping @Sendable () -> CpuTicks? = CpuMonitor.readHostTicks) {
        self.read = read
    }

    /// The busy fraction since the last call, or nil until there are two readings to compare.
    public func sample() -> Double? {
        guard let current = read() else { return nil }
        return lock.withLock {
            defer { previous = current }
            guard let previous else { return nil }
            return CpuLoadCalculator.busyFraction(from: previous, to: current)
        }
    }

    /// Ticks summed across every core, from `host_statistics`.
    public static func readHostTicks() -> CpuTicks? {
        var info = host_cpu_load_info()
        var count = mach_msg_type_number_t(
            MemoryLayout<host_cpu_load_info>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count)
            }
        }
        guard result == KERN_SUCCESS else { return nil }
        return CpuTicks(
            user: UInt64(info.cpu_ticks.0),
            system: UInt64(info.cpu_ticks.1),
            idle: UInt64(info.cpu_ticks.2),
            nice: UInt64(info.cpu_ticks.3))
    }
}

/// What the machine is doing right now, for the server to show beside the worker.
///
/// Both figures are optional and separately so: a Mac can report its CPU while having no readable
/// accelerator, and either can be momentarily unmeasurable. Absent means "no answer", which the
/// Workers tab shows as such rather than as zero.
public struct MachineLoad: Sendable, Equatable {
    /// Fraction of the last interval the machine spent busy, 0 to 1.
    public let cpu: Double?
    /// The accelerator's busy figure, 0 to 1.
    ///
    /// On Apple silicon a VideoToolbox encode runs on a dedicated media engine that is not the
    /// GPU's shader cores and is not exposed by any public interface, so this reads low while the
    /// Mac is entirely busy encoding. The server is told which encoder is running, so the two are
    /// read together there as they are in the sidecar's own menu.
    public let gpu: Double?

    public init(cpu: Double?, gpu: Double?) {
        self.cpu = cpu
        self.gpu = gpu
    }

    public var isEmpty: Bool { cpu == nil && gpu == nil }
}

/// Takes both readings together, so a reported pair describes one moment.
///
/// Each consumer holds its own: the check-in loop and a job's lease renewals run at different
/// cadences, and a CPU figure is the busy fraction *since that sampler last looked*. Sharing one
/// would leave each caller reporting whatever window the other happened to leave behind.
public struct MachineLoadSampler: Sendable {
    private let cpu: CpuMonitor
    private let gpu: @Sendable () -> GpuUsage?

    public init(
        cpu: CpuMonitor = CpuMonitor(),
        gpu: @escaping @Sendable () -> GpuUsage? = { GpuMonitor.sample() }
    ) {
        self.cpu = cpu
        self.gpu = gpu
    }

    /// Nil when neither figure could be read, so a caller sends nothing at all rather than a body
    /// full of absent fields.
    public func sample() -> MachineLoad? {
        let load = MachineLoad(cpu: cpu.sample(), gpu: gpu()?.device)
        return load.isEmpty ? nil : load
    }
}
