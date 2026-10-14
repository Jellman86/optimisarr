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
    public let type: String
    public let jobId: Int?
    public let delivered: Bool
    public let capacityBlocked: Bool
    public let capabilities: Data?
    public let nativeMemoryBytes: Int64?
    public let requiredScratchBytes: Int64?

    init(type: String, jobId: Int?, delivered: Bool, capacityBlocked: Bool, capabilities: Data?,
         nativeMemoryBytes: Int64? = nil, requiredScratchBytes: Int64? = nil) {
        self.type = type; self.jobId = jobId; self.delivered = delivered
        self.capacityBlocked = capacityBlocked; self.capabilities = capabilities
        self.nativeMemoryBytes = nativeMemoryBytes; self.requiredScratchBytes = requiredScratchBytes
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
              let object = try? JSONSerialization.jsonObject(with: request), object is [String: Any],
              expectedJobId == nil || expectedJobId! > 0 else { throw NativeQualityHostError() }
        // Preserve the original fields so the shared parser can reject ambiguous duplicates.
        let frame = request + Data([10])
        let owned = OwnedNativeHost(executable: executable)
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { continuation in
                DispatchQueue.global(qos: .utility).async {
                    do { continuation.resume(returning: try owned.exchange(frame, expectedJobId: expectedJobId, report: report)) }
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

    func exchange(_ frame: Data, expectedJobId: Int?, report: @Sendable (NativeQualityEvent) -> Void) throws -> NativeQualityReply {
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
            process.terminate(); process.waitUntilExit(); throw NativeQualityHostError()
        }
        var frames = NativeQualityFrames(expectedJobId: expectedJobId)
        var sent = 0
        var ended = false
        var exitDeadline: ContinuousClock.Instant?
        var buffer = [UInt8](repeating: 0, count: 16 * 1024)
        do {
            while true {
                if isCancelled && inputOpen { try? input.fileHandleForWriting.close(); inputOpen = false }
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
            if process.isRunning { process.terminate() }
            // The helper owns native children and scratch. Keep the parent's allocation until
            // its process exits, including when its terminal event was missing or malformed.
            process.waitUntilExit()
            if isCancelled { throw CancellationError() }
            throw NativeQualityHostError()
        }
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
    }
    let expectedJobId: Int?
    private var pending = Data()
    private(set) var terminal: NativeQualityReply?

    init(expectedJobId: Int?) { self.expectedJobId = expectedJobId }

    mutating func append(_ data: Data) throws -> [NativeQualityEvent] {
        guard terminal == nil || data.isEmpty else { throw NativeQualityHostError() }
        pending.append(data)
        var events: [NativeQualityEvent] = []
        while let newline = pending.firstIndex(of: 10) {
            let line = pending[..<newline]
            guard !line.isEmpty, line.count <= 16 * 1024, terminal == nil,
                  NativeWireJSON.hasUniqueProperties(Data(line)),
                  let value = try? JSONDecoder().decode(WireEvent.self, from: line),
                  value.version == 1 else { throw NativeQualityHostError() }
            let jobId = value.jobId
            switch value.type {
            case "status", "progress":
                guard jobId == expectedJobId, expectedJobId != nil else { throw NativeQualityHostError() }
                let seconds = value.encodedSeconds
                guard seconds == nil || seconds!.isFinite && seconds! >= 0 else { throw NativeQualityHostError() }
                events.append(NativeQualityEvent(type: value.type, jobId: jobId, stage: value.stage,
                    encodedSeconds: seconds, detail: value.detail.map { String($0.prefix(512)) }))
            case "completed":
                guard jobId == expectedJobId, expectedJobId != nil,
                      let outcome = value.outcome, outcome.jobId == jobId,
                      !outcome.delivered || outcome.capacityBlocked != true else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: jobId, delivered: outcome.delivered,
                    capacityBlocked: outcome.capacityBlocked ?? false, capabilities: nil)
            case "requirements":
                guard jobId == expectedJobId, expectedJobId != nil, value.outcome == nil,
                      let memory = value.nativeMemoryBytes, memory > 0,
                      let scratch = value.requiredScratchBytes, scratch > 0 else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: jobId, delivered: false,
                    capacityBlocked: false, capabilities: nil, nativeMemoryBytes: memory, requiredScratchBytes: scratch)
            case "capabilities":
                guard expectedJobId == nil, jobId == nil, let raw = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                      let proof = raw["capabilities"] as? [String: Any] else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: nil, delivered: false, capacityBlocked: false,
                    capabilities: try JSONSerialization.data(withJSONObject: proof))
            case "refused":
                guard jobId == nil, value.outcome == nil else { throw NativeQualityHostError() }
                terminal = NativeQualityReply(type: value.type, jobId: nil, delivered: false, capacityBlocked: false, capabilities: nil)
            default: throw NativeQualityHostError()
            }
            pending.removeSubrange(...newline)
        }
        guard pending.count <= 16 * 1024, terminal == nil || pending.isEmpty else { throw NativeQualityHostError() }
        return events
    }

    func finish(exitCode: Int32) throws -> NativeQualityReply {
        guard pending.isEmpty, let terminal else { throw NativeQualityHostError() }
        let expectedExit = ["capabilities", "requirements"].contains(terminal.type) || terminal.delivered ? exitCode == 0
            : terminal.type == "refused" ? [1, 2].contains(exitCode) : exitCode == 1
        guard expectedExit else { throw NativeQualityHostError() }
        return terminal
    }
}
