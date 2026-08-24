import Foundation

/// Why a pairing or check-in attempt failed, in terms the menu bar can explain to a person.
///
/// The server distinguishes these cases deliberately, so the client keeps them distinct rather
/// than collapsing everything into "failed". Telling someone their code expired is actionable;
/// telling them "error 401" is not.
public enum SidecarError: Error, Equatable, Sendable {
    /// The URL is not something that can be reached.
    case invalidServerAddress

    /// The PIN was wrong, spent, expired, or entered incorrectly too many times.
    case pairingRejected(reason: String)

    /// This build and the server share no protocol version. Neither side can fix that at runtime.
    case protocolIncompatible(reason: String)

    /// The credential is unknown or was revoked by the operator. Requires pairing again.
    case credentialRejected

    /// Remote workers are switched off on the server.
    case remoteWorkersDisabled(reason: String)

    /// The server answered, but not in a way this build understands.
    case unexpectedResponse(status: Int)

    /// The server could not be reached at all.
    case unreachable(description: String)
}

/// The result of a successful pairing. The credential is returned exactly once and is not
/// recoverable from the server afterwards, so it must be persisted immediately.
public struct PairingResult: Sendable, Equatable {
    public let workerId: Int
    public let credential: String
    public let protocolVersion: Int
}

/// The server's acknowledgement of a check-in.
public struct HeartbeatResult: Sendable, Equatable {
    public let workerId: Int
    public let protocolVersion: Int
    public let heartbeatInterval: TimeInterval
}

/// Performs HTTP so the client can be tested without a network.
public protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
}

public struct URLSessionTransport: HTTPTransport {
    private let session: URLSession

    public init(session: URLSession = .shared) {
        self.session = session
    }

    public func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse else {
            throw SidecarError.unexpectedResponse(status: 0)
        }
        return (data, http)
    }
}

/// Speaks the Optimisarr worker protocol.
///
/// Written against the published contract rather than by reading the server's source, which is the
/// point of having a second implementation: a client that only works because it shares assumptions
/// with the server tests nothing about the contract.
public struct SidecarClient: Sendable {
    private let transport: HTTPTransport

    public init(transport: HTTPTransport = URLSessionTransport()) {
        self.transport = transport
    }

    /// Redeems a PIN and returns the credential. The PIN is single-use: a failure here generally
    /// means the operator must issue a new one rather than retry this call.
    public func pair(
        serverAddress: String,
        pin: String,
        capabilities: SidecarCapabilities
    ) async throws -> PairingResult {
        let url = try Self.endpoint(serverAddress, "/api/workers/pair")

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            // Sent as typed by the operator. The server tolerates the grouping spaces people read
            // aloud, so there is no need to strip them here and risk mangling a valid code.
            "code": pin,
            "name": capabilities.name,
            "operatingSystem": capabilities.operatingSystem,
            "architecture": capabilities.architecture,
            "protocolMinimum": WorkerProtocol.minimum,
            "protocolMaximum": WorkerProtocol.maximum,
            "videoEncoders": capabilities.videoEncoders,
            "hardwareDecoders": capabilities.hardwareDecoders,
            "vmaf": capabilities.vmaf.rawValue,
            "freeScratchBytes": capabilities.freeScratchBytes,
            "maxConcurrency": capabilities.maxConcurrency,
        ])

        let (data, response) = try await perform(request)

        switch response.statusCode {
        case 200:
            guard
                let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                let workerId = body["workerId"] as? Int,
                let credential = body["credential"] as? String,
                let version = body["protocolVersion"] as? Int
            else {
                throw SidecarError.unexpectedResponse(status: 200)
            }
            return PairingResult(workerId: workerId, credential: credential, protocolVersion: version)
        case 401:
            throw SidecarError.pairingRejected(reason: Self.message(data) ?? "That pairing code was not accepted.")
        case 403:
            throw SidecarError.remoteWorkersDisabled(reason: Self.message(data) ?? "Remote workers are turned off.")
        case 409:
            throw SidecarError.protocolIncompatible(reason: Self.message(data) ?? "Incompatible protocol version.")
        case let status:
            throw SidecarError.unexpectedResponse(status: status)
        }
    }

    /// Reports in. The returned interval comes from the server so this app paces itself from the
    /// control plane rather than hard-coding a value that could drift out of step with the
    /// server's offline threshold.
    public func heartbeat(
        serverAddress: String,
        credential: String,
        freeScratchBytes: Int64,
        maxConcurrency: Int
    ) async throws -> HeartbeatResult {
        let url = try Self.endpoint(serverAddress, "/api/workers/heartbeat")

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.setValue("Bearer \(credential)", forHTTPHeaderField: "Authorization")
        request.httpBody = try JSONSerialization.data(withJSONObject: [
            "freeScratchBytes": freeScratchBytes,
            "maxConcurrency": maxConcurrency,
        ])

        let (data, response) = try await perform(request)

        switch response.statusCode {
        case 200:
            guard
                let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                let workerId = body["workerId"] as? Int,
                let version = body["protocolVersion"] as? Int,
                let seconds = body["heartbeatIntervalSeconds"] as? Int
            else {
                throw SidecarError.unexpectedResponse(status: 200)
            }
            return HeartbeatResult(
                workerId: workerId,
                protocolVersion: version,
                // Guarded rather than trusted outright: a zero or negative interval from a
                // malformed response would otherwise become a tight polling loop against the
                // server.
                heartbeatInterval: TimeInterval(max(5, seconds))
            )
        case 401:
            // Unknown or revoked. Either way this credential is finished and the app must stop
            // presenting it and ask to be paired again.
            throw SidecarError.credentialRejected
        case 403:
            throw SidecarError.remoteWorkersDisabled(reason: Self.message(data) ?? "Remote workers are turned off.")
        case let status:
            throw SidecarError.unexpectedResponse(status: status)
        }
    }

    private func perform(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        do {
            return try await transport.send(request)
        } catch let error as SidecarError {
            throw error
        } catch {
            throw SidecarError.unreachable(description: error.localizedDescription)
        }
    }

    /// Builds an endpoint from whatever the operator typed. People paste `192.168.1.10:8787`
    /// without a scheme far more often than not, so assume `http://` rather than failing — this is
    /// a LAN tool, and refusing a schemeless address would be pedantry that helps nobody.
    static func endpoint(_ serverAddress: String, _ path: String) throws -> URL {
        var trimmed = serverAddress.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw SidecarError.invalidServerAddress }

        if !trimmed.lowercased().hasPrefix("http://") && !trimmed.lowercased().hasPrefix("https://") {
            trimmed = "http://" + trimmed
        }
        while trimmed.hasSuffix("/") {
            trimmed.removeLast()
        }

        guard let url = URL(string: trimmed + path), url.host != nil else {
            throw SidecarError.invalidServerAddress
        }
        return url
    }

    /// The server's machine-readable errors carry a human sentence in `error`. Surfacing that
    /// rather than inventing wording keeps the explanation the operator sees consistent with the
    /// one Optimisarr itself would give.
    static func message(_ data: Data) -> String? {
        guard
            let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
            let message = body["error"] as? String,
            !message.isEmpty
        else {
            return nil
        }
        return message
    }
}
