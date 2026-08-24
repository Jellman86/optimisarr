import Foundation
import Security

/// Where a paired credential is kept between launches.
///
/// Behind a protocol so the session can be tested without touching the real Keychain, which needs
/// a signed bundle and would otherwise make the whole state machine untestable.
public protocol CredentialStore: Sendable {
    func load() throws -> StoredPairing?
    func save(_ pairing: StoredPairing) throws
    func clear() throws
}

/// What must survive a restart: which server, and the secret that proves who we are.
public struct StoredPairing: Codable, Sendable, Equatable {
    public let serverAddress: String
    public let credential: String
    public let workerId: Int

    public init(serverAddress: String, credential: String, workerId: Int) {
        self.serverAddress = serverAddress
        self.credential = credential
        self.workerId = workerId
    }
}

/// Keychain-backed storage.
///
/// The credential is a long-lived secret that authorises a remote machine against someone's media
/// server, so it belongs in the Keychain and nowhere else — never `UserDefaults`, never a plist,
/// never a log line. `kSecAttrAccessibleAfterFirstUnlock` lets the app resume checking in after a
/// reboot without the user being present, while still keeping the secret encrypted at rest.
public struct KeychainCredentialStore: CredentialStore {
    private let service: String
    private let account: String

    public init(service: String = "uk.optimisarr.sidecar", account: String = "worker-credential") {
        self.service = service
        self.account = account
    }

    private var baseQuery: [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    public func load() throws -> StoredPairing? {
        var query = baseQuery
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne

        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)

        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = item as? Data else {
            throw KeychainError.unexpectedStatus(status)
        }
        return try JSONDecoder().decode(StoredPairing.self, from: data)
    }

    public func save(_ pairing: StoredPairing) throws {
        let data = try JSONEncoder().encode(pairing)

        // Replace rather than update-or-insert: pairing again should not leave a stale secret
        // behind if the previous item was written by an older build with different attributes.
        SecItemDelete(baseQuery as CFDictionary)

        var query = baseQuery
        query[kSecValueData as String] = data
        query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
    }

    public func clear() throws {
        let status = SecItemDelete(baseQuery as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.unexpectedStatus(status)
        }
    }
}

public enum KeychainError: Error, Equatable {
    case unexpectedStatus(OSStatus)
}

/// For tests. Deliberately not used by the app.
public final class InMemoryCredentialStore: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: StoredPairing?

    public init(stored: StoredPairing? = nil) {
        self.stored = stored
    }

    public func load() throws -> StoredPairing? {
        lock.withLock { stored }
    }

    public func save(_ pairing: StoredPairing) throws {
        lock.withLock { stored = pairing }
    }

    public func clear() throws {
        lock.withLock { stored = nil }
    }
}
