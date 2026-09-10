import Foundation
import Testing
@testable import SidecarCore

/// A stub server. Records what the client sent so the request shape can be asserted — the point of
/// a second implementation is to catch contract drift, which only works if the request itself is
/// checked rather than just the happy-path response.
final class StubTransport: HTTPTransport, @unchecked Sendable {
    var status: Int
    var body: Data
    private(set) var lastRequest: URLRequest?

    init(status: Int = 200, json: [String: Any] = [:]) {
        self.status = status
        self.body = (try? JSONSerialization.data(withJSONObject: json)) ?? Data()
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        lastRequest = request
        let response = HTTPURLResponse(
            url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
        return (body, response)
    }

    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        lastRequest = request
        try body.write(to: destination)
        return HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: nil, headerFields: nil)!
    }

    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        try await send(request)
    }

    var sentJSON: [String: Any] {
        guard
            let body = lastRequest?.httpBody,
            let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any]
        else { return [:] }
        return object
    }
}

@Suite("Pairing")
struct PairingTests {
    private let capabilities = SidecarCapabilities.provenToday(name: "Test Mac")

    @Test("a successful pairing returns the credential")
    func pairSucceeds() async throws {
        let transport = StubTransport(status: 200, json: [
            "workerId": 7, "credential": "abc123", "protocolVersion": 1,
        ])

        let result = try await SidecarClient(transport: transport)
            .pair(serverAddress: "localhost:8787", pin: "12345678", capabilities: capabilities)

        #expect(result.workerId == 7)
        #expect(result.credential == "abc123")
        #expect(result.protocolVersion == 1)
    }

    @Test("capabilities are sent as names, never ordinals")
    func capabilitiesAreNamed() async throws {
        let transport = StubTransport(status: 200, json: [
            "workerId": 1, "credential": "c", "protocolVersion": 1,
        ])

        _ = try await SidecarClient(transport: transport)
            .pair(serverAddress: "localhost:8787", pin: "12345678", capabilities: capabilities)

        // Guards the contract from both sides. If either end ever reverts to an integer, this
        // fails rather than silently misreporting what this machine can do.
        #expect(transport.sentJSON["vmaf"] as? String == "None")
        #expect(transport.sentJSON["vmaf"] as? Int == nil)
    }

    @Test("the advertised protocol range is sent so the server can negotiate")
    func sendsProtocolRange() async throws {
        let transport = StubTransport(status: 200, json: [
            "workerId": 1, "credential": "c", "protocolVersion": 1,
        ])

        _ = try await SidecarClient(transport: transport)
            .pair(serverAddress: "localhost:8787", pin: "12345678", capabilities: capabilities)

        #expect(transport.sentJSON["protocolMinimum"] as? Int == WorkerProtocol.minimum)
        #expect(transport.sentJSON["protocolMaximum"] as? Int == WorkerProtocol.maximum)
    }

    @Test("this build claims no capability it cannot prove")
    func claimsNothingUnproven() async throws {
        // No encoders are bundled, so reporting any would invite work this app cannot do. Zero
        // concurrency reads as drained on the server, which is the honest state.
        let proven = SidecarCapabilities.provenToday(name: "Test Mac")

        #expect(proven.videoEncoders.isEmpty)
        #expect(proven.hardwareDecoders.isEmpty)
        #expect(proven.vmaf == .none)
        #expect(proven.maxConcurrency == 0)
    }

    @Test("a rejected PIN surfaces the server's own wording")
    func rejectedPin() async throws {
        let transport = StubTransport(status: 401, json: [
            "code": "worker.pairing.tooManyAttempts",
            "error": "That pairing code was entered incorrectly too many times and is no longer valid. Generate a new one.",
        ])

        await #expect(throws: SidecarError.pairingRejected(
            reason: "That pairing code was entered incorrectly too many times and is no longer valid. Generate a new one."
        )) {
            try await SidecarClient(transport: transport)
                .pair(serverAddress: "localhost:8787", pin: "00000000", capabilities: capabilities)
        }
    }

    @Test("an incompatible protocol is reported as such, not as a bad PIN")
    func incompatibleProtocol() async throws {
        let transport = StubTransport(status: 409, json: [
            "code": "worker.protocol.incompatible",
            "error": "The worker requires a newer protocol than this build speaks.",
        ])

        await #expect(throws: SidecarError.protocolIncompatible(
            reason: "The worker requires a newer protocol than this build speaks."
        )) {
            try await SidecarClient(transport: transport)
                .pair(serverAddress: "localhost:8787", pin: "12345678", capabilities: capabilities)
        }
    }

    @Test("the feature being switched off is distinguished from a bad code")
    func remoteWorkersOff() async throws {
        let transport = StubTransport(status: 403, json: [
            "code": "workers.disabled",
            "error": "Remote workers are turned off. Enable them in Settings to pair a sidecar.",
        ])

        await #expect(throws: SidecarError.remoteWorkersDisabled(
            reason: "Remote workers are turned off. Enable them in Settings to pair a sidecar."
        )) {
            try await SidecarClient(transport: transport)
                .pair(serverAddress: "localhost:8787", pin: "12345678", capabilities: capabilities)
        }
    }
}

@Suite("Heartbeat")
struct HeartbeatTests {
    @Test("a check-in presents the credential as a bearer token")
    func sendsBearer() async throws {
        let transport = StubTransport(status: 200, json: [
            "workerId": 3, "protocolVersion": 1, "heartbeatIntervalSeconds": 30,
        ])

        let result = try await SidecarClient(transport: transport).heartbeat(
            serverAddress: "localhost:8787", credential: "secret", freeScratchBytes: 0, maxConcurrency: 0)

        #expect(transport.lastRequest?.value(forHTTPHeaderField: "Authorization") == "Bearer secret")
        #expect(result.heartbeatInterval == 30)
    }

    @Test("a revoked credential is reported distinctly so the app can stop using it")
    func revoked() async throws {
        let transport = StubTransport(status: 401, json: [
            "code": "worker.credential.invalid", "error": "Unknown or revoked worker credential.",
        ])

        await #expect(throws: SidecarError.credentialRejected) {
            try await SidecarClient(transport: transport).heartbeat(
                serverAddress: "localhost:8787", credential: "stale", freeScratchBytes: 0, maxConcurrency: 0)
        }
    }

    @Test("a nonsensical interval cannot become a tight polling loop")
    func guardsAgainstZeroInterval() async throws {
        let transport = StubTransport(status: 200, json: [
            "workerId": 3, "protocolVersion": 1, "heartbeatIntervalSeconds": 0,
        ])

        let result = try await SidecarClient(transport: transport).heartbeat(
            serverAddress: "localhost:8787", credential: "secret", freeScratchBytes: 0, maxConcurrency: 0)

        #expect(result.heartbeatInterval >= 5)
    }
}

@Suite("Server address")
struct EndpointTests {
    @Test("a schemeless address is assumed to be http, because this is a LAN tool")
    func assumesHttp() throws {
        let url = try SidecarClient.endpoint("192.168.1.10:8787", "/api/workers/pair")
        #expect(url.absoluteString == "http://192.168.1.10:8787/api/workers/pair")
    }

    @Test("an explicit scheme is respected")
    func keepsExplicitScheme() throws {
        let url = try SidecarClient.endpoint("https://optimisarr.example.com", "/api/workers/pair")
        #expect(url.absoluteString == "https://optimisarr.example.com/api/workers/pair")
    }

    @Test("a trailing slash does not produce a doubled path")
    func stripsTrailingSlash() throws {
        let url = try SidecarClient.endpoint("http://host:8787///", "/api/workers/heartbeat")
        #expect(url.absoluteString == "http://host:8787/api/workers/heartbeat")
    }

    @Test("an empty address is refused rather than guessed at", arguments: ["", "   "])
    func refusesEmpty(address: String) throws {
        #expect(throws: SidecarError.invalidServerAddress) {
            try SidecarClient.endpoint(address, "/api/workers/pair")
        }
    }
}
