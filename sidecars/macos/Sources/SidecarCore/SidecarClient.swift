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

    /// The lease this job runs under is no longer this worker's: it lapsed, was released, or the
    /// job moved on without it. Whatever was being done for it is finished with.
    case leaseLost(reason: String)

    /// A source or candidate transfer did not complete.
    case transferFailed(reason: String)

    /// The server declined the delivered candidate; the reason is its own sentence.
    case deliveryRefused(reason: String)
}

/// The server's acknowledgement of a delivered candidate: accepted for verification, not yet judged.
public struct DeliveryReceipt: Sendable, Equatable {
    public let jobId: Int
    public let bytes: Int64
    public let candidateSha256: String
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

/// Performs HTTP so the client can be tested without a network. Bodies that may be gigabytes —
/// a source coming down, a candidate going up — stream to and from files rather than through
/// memory, hence the two file-shaped calls beside the ordinary one.
public protocol HTTPTransport: Sendable {
    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse)
    func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse
    func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse)
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

    public func download(_ request: URLRequest, to destination: URL) async throws -> HTTPURLResponse {
        let (temporary, response) = try await session.download(for: request)
        guard let http = response as? HTTPURLResponse else {
            try? FileManager.default.removeItem(at: temporary)
            throw SidecarError.unexpectedResponse(status: 0)
        }
        if http.statusCode == 200 {
            try? FileManager.default.removeItem(at: destination)
            try FileManager.default.moveItem(at: temporary, to: destination)
        } else {
            try? FileManager.default.removeItem(at: temporary)
        }
        return http
    }

    public func upload(_ request: URLRequest, fromFile file: URL) async throws -> (Data, HTTPURLResponse) {
        let (data, response) = try await session.upload(for: request, fromFile: file)
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

    /// Asks for work. `nil` is the ordinary answer most of the time: nothing this worker can run
    /// is queued, and that is not an error.
    public func claim(serverAddress: String, credential: String) async throws -> Assignment? {
        var request = try authorised(serverAddress, "/api/workers/claim", credential: credential, method: "POST")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = Data("{}".utf8)

        let (data, response) = try await perform(request)
        switch response.statusCode {
        case 200:
            guard
                let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                let assignment = Assignment(json: body)
            else {
                throw SidecarError.unexpectedResponse(status: 200)
            }
            return assignment
        case 204:
            return nil
        case 401:
            throw SidecarError.credentialRejected
        case 403:
            throw SidecarError.remoteWorkersDisabled(reason: Self.message(data) ?? "Remote workers are turned off.")
        case let status:
            throw SidecarError.unexpectedResponse(status: status)
        }
    }

    /// Extends the claim. Throws `leaseLost` when the server no longer recognises it as ours,
    /// which is the signal to stop the work being done under it.
    public func renew(serverAddress: String, credential: String, leaseId: String) async throws {
        let request = try authorised(
            serverAddress, "/api/workers/leases/\(leaseId)/renew", credential: credential, method: "POST")
        let (data, response) = try await perform(request)
        try Self.checkLease(response.statusCode, data)
    }

    /// Hands the job back so the server can reassign it. Tolerant of a lease already gone.
    public func release(serverAddress: String, credential: String, leaseId: String) async throws {
        let request = try authorised(
            serverAddress, "/api/workers/leases/\(leaseId)/release", credential: credential, method: "POST")
        let (data, response) = try await perform(request)
        try Self.checkLease(response.statusCode, data)
    }

    /// Fetches the source for a lease to a file and returns the hash the server declared for it,
    /// so the caller can prove the transfer arrived intact before encoding a byte of it.
    public func fetchSource(
        serverAddress: String, credential: String, leaseId: String, to destination: URL
    ) async throws -> String {
        let request = try authorised(
            serverAddress, "/api/workers/leases/\(leaseId)/source", credential: credential, method: "GET")
        let response: HTTPURLResponse
        do {
            response = try await transport.download(request, to: destination)
        } catch let error as SidecarError {
            throw error
        } catch {
            throw SidecarError.transferFailed(reason: "The source transfer failed: \(error.localizedDescription)")
        }

        switch response.statusCode {
        case 200:
            guard let hash = response.value(forHTTPHeaderField: "X-Optimisarr-Source-Sha256"), !hash.isEmpty else {
                throw SidecarError.unexpectedResponse(status: 200)
            }
            return hash
        case 401:
            throw SidecarError.credentialRejected
        case 403, 404, 409:
            throw SidecarError.leaseLost(reason: "The source is no longer available for this lease.")
        case let status:
            throw SidecarError.unexpectedResponse(status: status)
        }
    }

    /// Delivers the candidate, declaring both hashes so the server can bind the file to the
    /// exact source it was encoded from and refuse anything it cannot.
    public func deliver(
        serverAddress: String, credential: String, leaseId: String,
        file: URL, sourceSha256: String, candidateSha256: String
    ) async throws -> DeliveryReceipt {
        var request = try authorised(
            serverAddress, "/api/workers/leases/\(leaseId)/result", credential: credential, method: "POST")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue(sourceSha256, forHTTPHeaderField: "X-Optimisarr-Source-Sha256")
        request.setValue(candidateSha256, forHTTPHeaderField: "X-Optimisarr-Candidate-Sha256")

        let data: Data
        let response: HTTPURLResponse
        do {
            (data, response) = try await transport.upload(request, fromFile: file)
        } catch let error as SidecarError {
            throw error
        } catch {
            throw SidecarError.transferFailed(reason: "The candidate upload failed: \(error.localizedDescription)")
        }

        switch response.statusCode {
        case 202:
            guard
                let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                let jobId = (body["jobId"] as? NSNumber)?.intValue,
                let bytes = (body["bytes"] as? NSNumber)?.int64Value,
                let hash = body["candidateSha256"] as? String
            else {
                throw SidecarError.unexpectedResponse(status: 202)
            }
            return DeliveryReceipt(jobId: jobId, bytes: bytes, candidateSha256: hash)
        case 401:
            throw SidecarError.credentialRejected
        case 403, 404:
            throw SidecarError.leaseLost(reason: Self.message(data) ?? "That lease is no longer this worker's.")
        case 400, 409:
            throw SidecarError.deliveryRefused(reason: Self.message(data) ?? "The server declined the candidate.")
        case let status:
            throw SidecarError.unexpectedResponse(status: status)
        }
    }

    private static func checkLease(_ status: Int, _ data: Data) throws {
        switch status {
        case 200, 204:
            return
        case 401:
            throw SidecarError.credentialRejected
        case 403, 404, 409:
            throw SidecarError.leaseLost(reason: message(data) ?? "That lease is no longer this worker's.")
        case let status:
            throw SidecarError.unexpectedResponse(status: status)
        }
    }

    private func authorised(
        _ serverAddress: String, _ path: String, credential: String, method: String
    ) throws -> URLRequest {
        var request = URLRequest(url: try Self.endpoint(serverAddress, path))
        request.httpMethod = method
        request.setValue("Bearer \(credential)", forHTTPHeaderField: "Authorization")
        return request
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
