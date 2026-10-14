import Foundation
import SidecarCore

/// A disposable process using the real sidecar implementation. Pairing stays in memory;
/// neither the menu-bar application's Keychain nor its preferences are touched.
@main
struct AcceptanceWorker {
    @MainActor
    static func main() async throws {
        let env = ProcessInfo.processInfo.environment
        if CommandLine.arguments.contains("--memory-capacity") {
            let value: Any = MacMemoryAvailability.read().map { $0 as Any } ?? NSNull()
            let data = try JSONSerialization.data(withJSONObject: ["availableMemoryBytes": value,
                "physicalBytes": ProcessInfo.processInfo.physicalMemory,
                "reservedMemoryBytes": MemoryWorkBudget.shared.reservedBytes])
            print(String(decoding: data, as: UTF8.self))
            return
        }
        func required(_ key: String) throws -> String {
            guard let value = env[key], !value.isEmpty else {
                throw NSError(domain: "AcceptanceWorker", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Missing \(key)"])
            }
            return value
        }
        if CommandLine.arguments.contains("--native-quality") {
            let executable = URL(fileURLWithPath: try required("OPTIMISARR_NATIVE_HOST"))
            var data = Data()
            while let chunk = try FileHandle.standardInput.read(upToCount: 4096), !chunk.isEmpty {
                data.append(chunk)
                guard data.count <= 256 * 1024 else { throw NativeQualityHostError() }
            }
            if data.last == 10 { data.removeLast() }
            let request = try JSONSerialization.jsonObject(with: data) as? [String: Any]
            let jobId = (request?["assignment"] as? [String: Any])?["jobId"] as? Int
            let reply = try await NativeQualityHost(executable: executable).exchange(request: data, expectedJobId: jobId) { event in
                var message: [String: Any] = ["version": 1, "type": event.type]
                if let jobId = event.jobId { message["jobId"] = jobId }
                if let stage = event.stage { message["stage"] = stage }
                if let seconds = event.encodedSeconds { message["encodedSeconds"] = seconds }
                if let encoded = try? JSONSerialization.data(withJSONObject: message) {
                    try? FileHandle.standardOutput.write(contentsOf: encoded + Data([10]))
                }
            }
            var terminal: [String: Any] = ["version": 1, "type": reply.type]
            if let proof = reply.capabilities { terminal["capabilities"] = try JSONSerialization.jsonObject(with: proof) }
            if let jobId = reply.jobId {
                terminal["jobId"] = jobId
                if reply.type == "completed" {
                    terminal["outcome"] = ["jobId": jobId, "delivered": reply.delivered,
                                           "capacityBlocked": reply.capacityBlocked, "detail": "Check the server for its recorded outcome."]
                }
            }
            if let memory = reply.nativeMemoryBytes { terminal["nativeMemoryBytes"] = memory }
            if let scratch = reply.requiredScratchBytes { terminal["requiredScratchBytes"] = scratch }
            try FileHandle.standardOutput.write(contentsOf: JSONSerialization.data(withJSONObject: terminal) + Data([10]))
            return
        }
        let ffmpeg = URL(fileURLWithPath: try required("OPTIMISARR_FFMPEG"))
        let ffprobe = URL(fileURLWithPath: try required("OPTIMISARR_FFPROBE"))
        let name = env["OPTIMISARR_ACCEPTANCE_NAME"] ?? "macOS acceptance"
        var capabilities = await CapabilityProber(ffmpeg: ffmpeg).probe(name: name, maxConcurrency: 1)
        if CommandLine.arguments.contains("--discover") {
            let data = try JSONSerialization.data(withJSONObject: ["videoEncoders": capabilities.videoEncoders,
                                                                   "operatingSystem": "macos"])
            print(String(decoding: data, as: UTF8.self))
            return
        }
        let encoder = try required("OPTIMISARR_ACCEPTANCE_ENCODER")
        guard capabilities.videoEncoders.contains(encoder) else {
            throw NSError(domain: "AcceptanceWorker", code: 2,
                          userInfo: [NSLocalizedDescriptionKey: "Encoder did not pass the production capability probe: \(encoder)"])
        }
        // A single proved capability makes the production scheduler deterministic without
        // adding a test-only dispatch path to the application.
        capabilities.videoEncoders = [encoder]
        let server = try required("OPTIMISARR_ACCEPTANCE_SERVER")
        let scratch = URL(fileURLWithPath: try required("OPTIMISARR_ACCEPTANCE_SCRATCH"))
        guard !FileManager.default.fileExists(atPath: scratch.path) else {
            throw NSError(domain: "AcceptanceWorker", code: 3,
                          userInfo: [NSLocalizedDescriptionKey: "Scratch directory must be new"])
        }
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        let client = SidecarClient()
        let runner = JobRunner(client: client, ffmpeg: ffmpeg, ffprobe: ffprobe, scratchRoot: scratch)
        // Exercise the same recovery and concurrent heartbeat loop as the installed app.
        let session = SidecarSession(client: client, store: InMemoryCredentialStore(),
            capabilities: capabilities, prober: nil, executor: runner,
            scratchCapacity: { JobRunner.availableScratchBytes(at: scratch) ?? 0 },
            jobConcurrency: 1, persistConcurrency: { _ in })
        await session.pair(serverAddress: server, pin: try required("OPTIMISARR_ACCEPTANCE_PIN"))
        guard session.isPaired else {
            throw NSError(domain: "AcceptanceWorker", code: 4,
                          userInfo: [NSLocalizedDescriptionKey: "Pairing failed: \(session.status)"])
        }
        var previous: SidecarStatus?
        while !Task.isCancelled && session.isPaired {
            if session.status != previous {
                print("Session: \(session.status)")
                previous = session.status
            }
            try await Task.sleep(for: .seconds(1))
        }
        await session.prepareToQuit()
    }
}
