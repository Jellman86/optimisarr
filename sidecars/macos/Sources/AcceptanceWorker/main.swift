import Foundation
import SidecarCore

private struct AcceptancePairingRequest: Decodable {
    let serverAddress: String
    let pin: String
}

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
        if CommandLine.arguments.contains("--native-workspace") {
            let root = URL(fileURLWithPath: try required("OPTIMISARR_ACCEPTANCE_SCRATCH"))
            let disk = NativeQualityJobRunner.availableDiskWorkspace(at: root)
            let data = try JSONSerialization.data(withJSONObject: ["volume": disk.map { $0.volume as Any } ?? NSNull(),
                "availableDiskBytes": disk.map { $0.availableBytes as Any } ?? NSNull()])
            print(String(decoding: data, as: UTF8.self))
            return
        }
        if CommandLine.arguments.contains("--native-capabilities") || CommandLine.arguments.contains("--native-capability-pair") {
            let scratch = URL(fileURLWithPath: try required("OPTIMISARR_ACCEPTANCE_SCRATCH"))
            guard !FileManager.default.fileExists(atPath: scratch.path) else { throw NativeQualityHostError() }
            try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let tools = NativeQualityTools(host: URL(fileURLWithPath: try required("OPTIMISARR_NATIVE_HOST")),
                ffmpeg: URL(fileURLWithPath: try required("OPTIMISARR_FFMPEG")),
                imageMetric: env["OPTIMISARR_IMAGE_QUALITY"].map { URL(fileURLWithPath: $0) },
                audioMetric: env["OPTIMISARR_AUDIO_QUALITY"].map { URL(fileURLWithPath: $0) },
                metadataTool: env["OPTIMISARR_EXIFTOOL"].map { URL(fileURLWithPath: $0) })
            let native = NativeQualityCapabilityProber(tools: tools)
            let proved = await CapabilityProber(ffmpeg: tools.ffmpeg, scratchDirectory: scratch, native: native).probe(name: "Isolated capability acceptance")
            let fresh = NativeWorkerResources(scratchRoot: scratch).refresh(proved)
            let proof: Any = try fresh.perceptual.map { try JSONSerialization.jsonObject(with: JSONEncoder().encode($0)) } ?? NSNull()
            var output: [String: Any] = ["name": fresh.name, "operatingSystem": fresh.operatingSystem,
                "architecture": fresh.architecture, "videoEncoders": fresh.videoEncoders, "audioEncoders": fresh.audioEncoders,
                "hardwareDecoders": fresh.hardwareDecoders, "vmaf": fresh.vmaf.rawValue, "freeScratchBytes": fresh.freeScratchBytes,
                "maxConcurrency": fresh.maxConcurrency, "perceptual": proof, "protocolMinimum": WorkerProtocol.minimum,
                "protocolMaximum": WorkerProtocol.maximum]
            if CommandLine.arguments.contains("--native-capability-pair") {
                var data = Data()
                while let chunk = try FileHandle.standardInput.read(upToCount: 4096), !chunk.isEmpty {
                    data.append(chunk)
                    guard data.count <= 4096 else { throw NativeQualityHostError() }
                }
                let request = try JSONDecoder().decode(AcceptancePairingRequest.self, from: data)
                let client = SidecarClient()
                let paired = try await client.pair(serverAddress: request.serverAddress, pin: request.pin, capabilities: fresh)
                let current = NativeWorkerResources(scratchRoot: scratch).refresh(proved)
                let beat = try await client.heartbeat(serverAddress: request.serverAddress, credential: paired.credential,
                    freeScratchBytes: current.freeScratchBytes, maxConcurrency: current.maxConcurrency, capabilities: current)
                output["workerId"] = paired.workerId
                output["negotiatedProtocol"] = paired.protocolVersion
                output["heartbeatProtocol"] = beat.protocolVersion
                output["heartbeatPerceptual"] = try current.perceptual.map { try JSONSerialization.jsonObject(with: JSONEncoder().encode($0)) } ?? NSNull()
            }
            try FileHandle.standardOutput.write(contentsOf: JSONSerialization.data(withJSONObject: output) + Data([10]))
            return
        }
        if CommandLine.arguments.contains("--native-claim") {
            var data = Data()
            while let chunk = try FileHandle.standardInput.read(upToCount: 4096), !chunk.isEmpty {
                data.append(chunk)
                guard data.count <= 4096 else { throw NativeQualityHostError() }
            }
            let pairing = try JSONDecoder().decode(StoredPairing.self, from: data)
            let scratch = URL(fileURLWithPath: try required("OPTIMISARR_ACCEPTANCE_SCRATCH"))
            guard !FileManager.default.fileExists(atPath: scratch.path) else { throw NativeQualityHostError() }
            let tools = NativeQualityTools(host: URL(fileURLWithPath: try required("OPTIMISARR_NATIVE_HOST")),
                ffmpeg: URL(fileURLWithPath: try required("OPTIMISARR_FFMPEG")),
                imageMetric: env["OPTIMISARR_IMAGE_QUALITY"].map { URL(fileURLWithPath: $0) },
                audioMetric: env["OPTIMISARR_AUDIO_QUALITY"].map { URL(fileURLWithPath: $0) },
                metadataTool: env["OPTIMISARR_EXIFTOOL"].map { URL(fileURLWithPath: $0) })
            let client = SidecarClient()
            guard let assignment = try await client.claim(serverAddress: pairing.serverAddress, credential: pairing.credential),
                  assignment.nativeQuality != nil else { throw NativeQualityHostError() }
            let settings = SettingsSnapshot(workLocation: env["OPTIMISARR_ACCEPTANCE_RAM"] == "1" ? .memory : .applicationSupport,
                memoryBudgetBytes: WorkLocationPolicy.defaultBudget(physicalBytes: Int64(ProcessInfo.processInfo.physicalMemory)))
            let runner = JobRunner(client: client, scratchRoot: scratch, settings: settings, nativeTools: tools)
            let outcome = await runner.execute(assignment, pairing: pairing) { value in
                if let event = try? JSONSerialization.data(withJSONObject: ["type": "progress", "jobId": assignment.jobId, "stage": value.summary]) {
                    try? FileHandle.standardOutput.write(contentsOf: event + Data([10]))
                }
            } storage: { location in
                var event: [String: Any] = ["type": "storage", "inMemory": location.inMemory, "path": location.path]
                if let reason = location.fallbackReason { event["fallbackReason"] = reason }
                if let data = try? JSONSerialization.data(withJSONObject: event) {
                    try? FileHandle.standardOutput.write(contentsOf: data + Data([10]))
                }
            }
            let state: String
            switch outcome {
            case .delivered: state = "delivered"
            case .capacityBlocked: state = "capacityBlocked"
            case .released: state = "released"
            case .unconfirmed: state = "unconfirmed"
            case .failed: state = "failed"
            case .leaseLost: state = "leaseLost"
            }
            let result = try JSONSerialization.data(withJSONObject: ["type": "completed", "jobId": assignment.jobId,
                "state": state, "liveMemoryProvider": true, "reservedMemoryBytes": MemoryWorkBudget.shared.reservedBytes])
            try FileHandle.standardOutput.write(contentsOf: result + Data([10]))
            return
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
