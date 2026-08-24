import Foundation

/// What the menu bar shows, and what the app is actually doing.
public enum SidecarStatus: Equatable, Sendable {
    /// No credential stored. The operator needs to pair.
    case unpaired

    /// A pairing attempt is in flight.
    case pairing

    /// Paired and checking in successfully.
    case connected(workerId: Int, lastCheckIn: Date)

    /// Paired, but the last check-in did not get through. The credential is still believed good,
    /// so this recovers on its own — distinct from `revoked`, which never will.
    case unreachable(reason: String)

    /// The server refused the credential. Someone revoked this worker, or the database was
    /// replaced. Only re-pairing fixes it, so the stored credential is discarded.
    case revoked

    /// Remote workers are switched off on the server. Nothing is wrong with this app; it simply
    /// has nothing to do until an operator turns the feature on.
    case disabledOnServer(reason: String)

    /// Pairing failed for a reason the operator needs to read.
    case pairingFailed(reason: String)
}

/// Drives pairing and the check-in loop, and owns the one piece of state the UI renders.
///
/// Deliberately free of SwiftUI so the whole lifecycle — pair, beat, get revoked, recover — can be
/// tested without a menu bar. `@MainActor` because the UI observes it directly and there is no
/// reason for this to be concurrent; the work it does is network I/O, not computation.
@MainActor
public final class SidecarSession: ObservableObject {
    @Published public private(set) var status: SidecarStatus = .unpaired
    @Published public private(set) var serverAddress: String = ""

    private let client: SidecarClient
    private let store: CredentialStore
    private let capabilities: SidecarCapabilities
    private let sleep: @Sendable (TimeInterval) async throws -> Void

    private var pairing: StoredPairing?
    private var heartbeatTask: Task<Void, Never>?

    public init(
        client: SidecarClient = SidecarClient(),
        store: CredentialStore = KeychainCredentialStore(),
        capabilities: SidecarCapabilities = .provenToday(name: Host.current().localizedName ?? "Mac"),
        sleep: @escaping @Sendable (TimeInterval) async throws -> Void = { seconds in
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
        }
    ) {
        self.client = client
        self.store = store
        self.capabilities = capabilities
        self.sleep = sleep
    }

    /// Restores a previous pairing, if there is one, and starts checking in.
    public func restore() {
        guard let stored = try? store.load() else {
            status = .unpaired
            return
        }
        pairing = stored
        serverAddress = stored.serverAddress
        startHeartbeat()
    }

    /// Redeems a PIN. On success the credential is persisted immediately — the server returns it
    /// exactly once and cannot reissue it, so losing it here would mean pairing again.
    public func pair(serverAddress address: String, pin: String) async {
        status = .pairing
        serverAddress = address

        do {
            let result = try await client.pair(
                serverAddress: address, pin: pin, capabilities: capabilities)

            let stored = StoredPairing(
                serverAddress: address, credential: result.credential, workerId: result.workerId)
            try store.save(stored)
            pairing = stored

            startHeartbeat()
        } catch let error as SidecarError {
            status = Self.describe(error)
        } catch {
            status = .pairingFailed(reason: error.localizedDescription)
        }
    }

    /// Forgets the pairing locally. This does not revoke anything server-side: only an operator
    /// can do that, and pretending otherwise would overstate what this app controls.
    public func unpair() {
        heartbeatTask?.cancel()
        heartbeatTask = nil
        try? store.clear()
        pairing = nil
        status = .unpaired
    }

    private func startHeartbeat() {
        heartbeatTask?.cancel()
        heartbeatTask = Task { [weak self] in
            await self?.heartbeatLoop()
        }
    }

    private func heartbeatLoop() async {
        // The server tells us how often to check in, so the two stay in step. Until it has, use a
        // conservative interval rather than hammering it.
        var interval: TimeInterval = 30

        while !Task.isCancelled {
            guard let pairing else { return }

            do {
                let beat = try await client.heartbeat(
                    serverAddress: pairing.serverAddress,
                    credential: pairing.credential,
                    freeScratchBytes: capabilities.freeScratchBytes,
                    maxConcurrency: capabilities.maxConcurrency)

                interval = beat.heartbeatInterval
                status = .connected(workerId: beat.workerId, lastCheckIn: Date())
            } catch SidecarError.credentialRejected {
                // Terminal. Retrying cannot help, and holding a dead secret on disk serves no
                // purpose, so drop it and tell the operator plainly.
                try? store.clear()
                self.pairing = nil
                status = .revoked
                return
            } catch let SidecarError.remoteWorkersDisabled(reason) {
                // Not terminal: an operator can switch the feature back on, and the credential is
                // still valid, so keep checking in rather than unpairing.
                status = .disabledOnServer(reason: reason)
            } catch let error as SidecarError {
                status = .unreachable(reason: Self.describe(error).shortReason)
            } catch {
                status = .unreachable(reason: error.localizedDescription)
            }

            try? await sleep(interval)
        }
    }

    static func describe(_ error: SidecarError) -> SidecarStatus {
        switch error {
        case .invalidServerAddress:
            return .pairingFailed(reason: "That server address could not be understood.")
        case let .pairingRejected(reason):
            return .pairingFailed(reason: reason)
        case let .protocolIncompatible(reason):
            return .pairingFailed(reason: reason)
        case .credentialRejected:
            return .revoked
        case let .remoteWorkersDisabled(reason):
            return .disabledOnServer(reason: reason)
        case let .unexpectedResponse(status):
            return .pairingFailed(reason: "The server replied unexpectedly (HTTP \(status)).")
        case let .unreachable(description):
            return .pairingFailed(reason: description)
        }
    }
}

extension SidecarStatus {
    /// A short line for the menu bar, without the surrounding case.
    var shortReason: String {
        switch self {
        case let .pairingFailed(reason): return reason
        case let .unreachable(reason): return reason
        case let .disabledOnServer(reason): return reason
        default: return ""
        }
    }

    /// What the menu bar shows at a glance.
    public var summary: String {
        switch self {
        case .unpaired: return "Not paired"
        case .pairing: return "Pairing…"
        case .connected: return "Connected"
        case .unreachable: return "Server unreachable"
        case .revoked: return "Access revoked"
        case .disabledOnServer: return "Turned off on the server"
        case .pairingFailed: return "Pairing failed"
        }
    }
}
