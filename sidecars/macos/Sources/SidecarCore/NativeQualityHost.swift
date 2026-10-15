import Darwin
import Foundation

public struct NativeQualityHostError: Error, LocalizedError, Sendable {
    public init() {}
    public var errorDescription: String? { "The native quality helper did not return a complete, matching result. Check the server for its recorded outcome." }
}

public struct NativeQualityEvent: Sendable {
    public let type: String
    public let jobId: Int?
    public let stage: String?
    public let encodedSeconds: Double?
    public let detail: String?
}

public struct NativeQualityReply: Sendable {
    public let version: Int
    public let type: String
    public let jobId: Int?
    public let delivered: Bool
    public let capacityBlocked: Bool
    public let capabilities: Data?
    public let nativeMemoryBytes: Int64?
    public let requiredScratchBytes: Int64?
    public let recovery: NativeScratchRecovery?
    public let accountingScope: NativeScratchScope?
    public let retirement: NativeScratchRetirement?
    public let consumption: NativeScratchConsumption?
    public let nativeWorkspaceCreated: Bool?
    public let nativeResourcesRetired: Bool?

    init(type: String, jobId: Int?, delivered: Bool, capacityBlocked: Bool, capabilities: Data?,
         nativeMemoryBytes: Int64? = nil, requiredScratchBytes: Int64? = nil, recovery: NativeScratchRecovery? = nil,
         version: Int = 1, accountingScope: NativeScratchScope? = nil, retirement: NativeScratchRetirement? = nil,
         consumption: NativeScratchConsumption? = nil, nativeWorkspaceCreated: Bool? = nil, nativeResourcesRetired: Bool? = nil) {
        self.version = version
        self.type = type; self.jobId = jobId; self.delivered = delivered
        self.capacityBlocked = capacityBlocked; self.capabilities = capabilities
        self.nativeMemoryBytes = nativeMemoryBytes; self.requiredScratchBytes = requiredScratchBytes; self.recovery = recovery
        self.accountingScope = accountingScope; self.retirement = retirement; self.consumption = consumption
        self.nativeWorkspaceCreated = nativeWorkspaceCreated; self.nativeResourcesRetired = nativeResourcesRetired
    }
}

/// The wire assignment stays intact; the shared engine owns its validation and execution.
/// This bridge never treats a terminal event as proof that the process and its children stopped.
public protocol NativeQualityExchanging: Sendable {
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply
}

public struct NativeQualityHost: NativeQualityExchanging {
    public let executable: URL
    public init(executable: URL) { self.executable = executable }

    public func exchange(request: Data, expectedJobId: Int?,
                         report: @escaping @Sendable (NativeQualityEvent) -> Void = { _ in }) async throws -> NativeQualityReply {
        guard !request.isEmpty, request.count <= 256 * 1024,
              !request.contains(10), !request.contains(13),
              let object = try? JSONSerialization.jsonObject(with: request) as? [String: Any],
              expectedJobId == nil || expectedJobId! > 0 else { throw NativeQualityHostError() }
        struct RequestVersion: Decodable { let version: Int }
        let version: Int
        if object["version"] != nil {
            guard let envelope = try? JSONDecoder().decode(RequestVersion.self, from: request), [1, 2].contains(envelope.version) else {
                throw NativeQualityHostError()
            }
            version = envelope.version
        } else { version = 1 }
        // Preserve the original fields so the shared parser can reject ambiguous duplicates.
        let frame = request + Data([10])
        let owned = OwnedNativeHost(executable: executable)
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                DispatchQueue.global(qos: .utility).async {
                    do { continuation.resume(returning: try owned.exchange(frame, expectedJobId: expectedJobId, expectedVersion: version, report: report)) }
                    catch { continuation.resume(throwing: error) }
                }
            }
        } onCancel: { owned.cancel() }
    }
}

/// Nonblocking descriptors keep request writes and output reads cancellable without one blocked
/// FileHandle operation holding the app's cooperative executor or a resource reservation forever.
private final class OwnedNativeHost: @unchecked Sendable {
    private let gate = NSLock()
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private var cancelled = false
    private var launched = false

    init(executable: URL) {
        process.executableURL = executable
        process.arguments = []
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        input.sealFromOtherChildren()
        output.sealFromOtherChildren()
    }

    func cancel() {
        gate.withLock {
            cancelled = true
            if launched && process.isRunning { process.terminate() }
        }
    }
    private var isCancelled: Bool { gate.withLock { cancelled } }

    func exchange(_ frame: Data, expectedJobId: Int?, expectedVersion: Int, report: @Sendable (NativeQualityEvent) -> Void) throws -> NativeQualityReply {
        var inputOpen = true
        defer {
            if inputOpen { try? input.fileHandleForWriting.close() }
            try? input.fileHandleForReading.close()
            try? output.fileHandleForReading.close()
            try? output.fileHandleForWriting.close()
        }
        do {
            try gate.withLock {
                guard !cancelled else { throw CancellationError() }
                try process.run()
                launched = true
            }
        } catch is CancellationError { throw CancellationError() }
        catch { throw NativeQualityHostError() }

        try? input.fileHandleForReading.close()
        try? output.fileHandleForWriting.close()
        let incoming = output.fileHandleForReading.fileDescriptor
        let outgoing = input.fileHandleForWriting.fileDescriptor
        guard fcntl(incoming, F_SETFL, fcntl(incoming, F_GETFL) | O_NONBLOCK) == 0,
              fcntl(outgoing, F_SETFL, fcntl(outgoing, F_GETFL) | O_NONBLOCK) == 0,
              fcntl(outgoing, F_SETNOSIGPIPE, 1) == 0 else {
            stopOwnedHelper(); throw NativeQualityHostError()
        }
        var frames = NativeQualityFrames(expectedJobId: expectedJobId, expectedVersion: expectedVersion)
        var sent = 0
        var ended = false
        var exitDeadline: ContinuousClock.Instant?
        var buffer = [UInt8](repeating: 0, count: 16 * 1024)
        do {
            while true {
                if isCancelled && inputOpen { try? input.fileHandleForWriting.close(); inputOpen = false }
                if isCancelled { throw CancellationError() }
                while !ended {
                    if isCancelled { throw CancellationError() }
                    let count = read(incoming, &buffer, buffer.count)
                    if count > 0 {
                        for event in try frames.append(Data(buffer.prefix(count))) { report(event) }
                    } else if count == 0 { ended = true }
                    else if errno == EAGAIN || errno == EWOULDBLOCK { break }
                    else if errno != EINTR { throw NativeQualityHostError() }
                }
                if frames.terminal != nil && inputOpen {
                    guard sent == frame.count else { throw NativeQualityHostError() }
                    try? input.fileHandleForWriting.close(); inputOpen = false
                }
                if !process.isRunning {
                    if exitDeadline == nil { exitDeadline = .now.advanced(by: .seconds(2)) }
                    if ended || ContinuousClock.now >= exitDeadline! {
                        if isCancelled { throw CancellationError() }
                        return try frames.finish(exitCode: process.terminationStatus)
                    }
                }
                if ended && process.isRunning && frames.terminal == nil { throw NativeQualityHostError() }
                var descriptors = [pollfd(fd: ended ? -1 : incoming, events: Int16(POLLIN), revents: 0),
                                   pollfd(fd: inputOpen && sent < frame.count ? outgoing : -1, events: Int16(POLLOUT), revents: 0)]
                let ready = poll(&descriptors, nfds_t(descriptors.count), 100)
                if ready < 0 && errno != EINTR { throw NativeQualityHostError() }
                if inputOpen && sent < frame.count && descriptors[1].revents & Int16(POLLOUT) != 0 {
                    let written = frame.withUnsafeBytes { bytes in
                        write(outgoing, bytes.baseAddress!.advanced(by: sent), min(8192, frame.count - sent))
                    }
                    if written > 0 { sent += written }
                    else if written < 0 && errno != EAGAIN && errno != EWOULDBLOCK && errno != EINTR { throw NativeQualityHostError() }
                }
            }
        } catch {
            if inputOpen { try? input.fileHandleForWriting.close(); inputOpen = false }
            // An unconfirmed helper may leave inherited writers behind. Its exit cannot retire
            // their grants, but a blocked helper must not strand the control task indefinitely.
            stopOwnedHelper()
            if isCancelled { throw CancellationError() }
            throw NativeQualityHostError()
        }
    }

    private func stopOwnedHelper() {
        if process.isRunning { process.terminate() }
        waitForOwnedExit(until: .now.advanced(by: .seconds(1)))
        if process.isRunning {
            _ = Darwin.kill(process.processIdentifier, SIGKILL)
            waitForOwnedExit(until: .now.advanced(by: .seconds(1)))
        }
        if !process.isRunning { process.waitUntilExit() }
    }

    private func waitForOwnedExit(until deadline: ContinuousClock.Instant) {
        while process.isRunning && ContinuousClock.now < deadline { usleep(10_000) }
    }
}

struct NativeQualityFrames {
    private struct WireOutcome: Decodable {
        let jobId: Int
        let delivered: Bool
        let capacityBlocked: Bool?
    }
    private struct WireEvent: Decodable {
        let version: Int
        let type: String
        let jobId: Int?
        let stage: String?
        let encodedSeconds: Double?
        let detail: String?
        let outcome: WireOutcome?
        let nativeMemoryBytes: Int64?
        let requiredScratchBytes: Int64?
        let recovery: NativeScratchRecovery?
        let accountingScope: NativeScratchScope?
        let retirement: NativeScratchRetirement?
        let consumption: NativeScratchConsumption?
        let nativeWorkspaceCreated: Bool?
        let nativeResourcesRetired: Bool?

        var hasNoRetirementProof: Bool {
            accountingScope == nil && retirement == nil && consumption == nil
                && nativeWorkspaceCreated == nil && nativeResourcesRetired == nil
        }
    }
    let expectedJobId: Int?
    let expectedVersion: Int
    private var pending = Data()
    private(set) var terminal: NativeQualityReply?

    init(expectedJobId: Int?, expectedVersion: Int = 1) {
        self.expectedJobId = expectedJobId; self.expectedVersion = expectedVersion
    }

    mutating func append(_ data: Data) throws -> [NativeQualityEvent] {
        guard terminal == nil || data.isEmpty else { throw NativeQualityHostError() }
        pending.append(data)
        var events: [NativeQualityEvent] = []
        while let newline = pending.firstIndex(of: 10) {
            let line = pending[..<newline]
            guard !line.isEmpty, line.count <= 16 * 1024, terminal == nil,
                  NativeWireJSON.hasUniqueProperties(Data(line)),
                  let value = try? JSONDecoder().decode(WireEvent.self, from: line),
                  value.version == expectedVersion, [1, 2].contains(expectedVersion),
                  expectedVersion == 2 || value.hasNoRetirementProof,
                  let raw = try? JSONSerialization.jsonObject(with: line) as? [String: Any] else { throw NativeQualityHostError() }
            let jobId = value.jobId
            let noCapabilities = raw["capabilities"] == nil || raw["capabilities"] is NSNull
            if expectedVersion == 2 {
                let known = Set(["version", "type", "jobId", "stage", "encodedSeconds", "detail", "outcome", "capabilities",
                    "nativeMemoryBytes", "requiredScratchBytes", "recovery", "accountingScope", "retirement", "consumption",
                    "nativeWorkspaceCreated", "nativeResourcesRetired"])
                guard Set(raw.keys).isSubset(of: known) else { throw NativeQualityHostError() }
                for (key, fields) in [("accountingScope", Set(["root", "control", "gate"])),
                                      ("retirement", Set(["retired", "busy"])),
                                      ("consumption", Set(["consumed", "alreadyAbsent"]))] {
                    if let object = raw[key] as? [String: Any], Set(object.keys) != fields { throw NativeQualityHostError() }
                }
            }
            switch value.type {
            case "status", "progress":
                guard jobId == expectedJobId, expectedJobId != nil, value.hasNoRetirementProof else { throw NativeQualityHostError() }
                let seconds = value.encodedSeconds
                guard seconds == nil || seconds!.isFinite && seconds! >= 0 else { throw NativeQualityHostError() }
                events.append(NativeQualityEvent(type: value.type, jobId: jobId, stage: value.stage,
                    encodedSeconds: seconds, detail: value.detail.map { String($0.prefix(512)) }))
            case "completed":
                guard jobId == expectedJobId, expectedJobId != nil,
                      let outcome = value.outcome, outcome.jobId == jobId,
                      !outcome.delivered || outcome.capacityBlocked != true,
                      value.accountingScope == nil, value.retirement == nil, value.consumption == nil,
                      value.recovery == nil, value.nativeMemoryBytes == nil, value.requiredScratchBytes == nil, noCapabilities,
                      expectedVersion == 1 || value.nativeWorkspaceCreated != nil && value.nativeResourcesRetired != nil,
                      value.nativeResourcesRetired != true || value.nativeWorkspaceCreated == true else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: jobId, delivered: outcome.delivered,
                    capacityBlocked: outcome.capacityBlocked ?? false, capabilities: nil, version: expectedVersion,
                    nativeWorkspaceCreated: value.nativeWorkspaceCreated, nativeResourcesRetired: value.nativeResourcesRetired)
            case "requirements":
                guard jobId == expectedJobId, expectedJobId != nil, value.outcome == nil, value.hasNoRetirementProof, value.recovery == nil, noCapabilities,
                      let memory = value.nativeMemoryBytes, memory > 0,
                      let scratch = value.requiredScratchBytes, scratch > 0 else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: jobId, delivered: false,
                    capacityBlocked: false, capabilities: nil, nativeMemoryBytes: memory, requiredScratchBytes: scratch, version: expectedVersion)
            case "scope":
                guard expectedVersion == 2, expectedJobId == nil, jobId == nil, value.outcome == nil,
                      let scope = value.accountingScope, scope.isValid, value.retirement == nil, value.consumption == nil,
                      value.nativeWorkspaceCreated == nil, value.nativeResourcesRetired == nil, value.recovery == nil,
                      value.nativeMemoryBytes == nil, value.requiredScratchBytes == nil, noCapabilities else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: nil, delivered: false, capacityBlocked: false,
                    capabilities: nil, version: 2, accountingScope: scope)
            case "retired", "forgotten":
                guard expectedVersion == 2, expectedJobId != nil, jobId == expectedJobId, value.outcome == nil,
                      value.accountingScope == nil, value.nativeWorkspaceCreated == nil, value.nativeResourcesRetired == nil,
                      value.recovery == nil, value.nativeMemoryBytes == nil, value.requiredScratchBytes == nil, noCapabilities else { throw NativeQualityHostError() }
                if value.type == "retired" {
                    guard value.retirement?.isValid == true, value.consumption == nil else { throw NativeQualityHostError() }
                } else {
                    guard value.consumption?.isValid == true, value.retirement == nil else { throw NativeQualityHostError() }
                }
                terminal = NativeQualityReply(type: value.type, jobId: jobId, delivered: false, capacityBlocked: false,
                    capabilities: nil, version: 2, retirement: value.retirement, consumption: value.consumption)
            case "recovered":
                guard expectedVersion == 1, expectedJobId == nil, jobId == nil, value.outcome == nil,
                      value.nativeMemoryBytes == nil, value.requiredScratchBytes == nil,
                      let recovery = value.recovery, recovery.isValidPage,
                      let raw = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                      raw["capabilities"] == nil || raw["capabilities"] is NSNull else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: nil, delivered: false,
                    capacityBlocked: false, capabilities: nil, recovery: recovery)
            case "capabilities":
                guard expectedVersion == 1, expectedJobId == nil, jobId == nil,
                      let proof = raw["capabilities"] as? [String: Any] else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: nil, delivered: false, capacityBlocked: false,
                    capabilities: try JSONSerialization.data(withJSONObject: proof))
            case "refused":
                guard jobId == nil, value.outcome == nil, value.hasNoRetirementProof else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: nil, delivered: false, capacityBlocked: false, capabilities: nil, version: expectedVersion)
            default: throw NativeQualityHostError()
            }
            pending.removeSubrange(...newline)
        }
        guard pending.count <= 16 * 1024, terminal == nil || pending.isEmpty else { throw NativeQualityHostError() }
        return events
    }

    func finish(exitCode: Int32) throws -> NativeQualityReply {
        guard pending.isEmpty, let terminal else { throw NativeQualityHostError() }
        let expectedExit = ["capabilities", "requirements", "recovered", "scope", "retired", "forgotten"].contains(terminal.type) || terminal.delivered ? exitCode == 0
            : terminal.type == "refused" ? [1, 2].contains(exitCode) : exitCode == 1
        guard expectedExit else { throw NativeQualityHostError() }
        return terminal
    }
}
