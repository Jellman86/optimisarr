import Foundation
import LocalAuthentication
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
/// never a log line.
///
/// **Two keychains, deliberately.** A properly signed build uses the data protection keychain,
/// where access is decided by the code signature and no dialog is ever raised. An ad-hoc build has
/// no team identifier, so that keychain refuses it outright (`errSecMissingEntitlement`), and it
/// falls back to the legacy file keychain. The legacy one guards items with an access control list
/// naming the exact binary that wrote them, and an ad-hoc signature changes with every rebuild —
/// so a rebuilt development build is a different application to it, and asks the operator for a
/// password it should never be asking for. That was seen on 2026-09-13 as a prompt that would not
/// stop. Reads therefore refuse interaction, and an item that cannot be read without it is removed
/// and reported as "not paired" so the operator pairs once more instead of being nagged forever.
public struct KeychainCredentialStore: CredentialStore {
    private let service: String
    private let account: String
    private let accessGroup: String?

    public init(
        service: String = "uk.optimisarr.sidecar",
        account: String = "worker-credential",
        accessGroup: String? = nil
    ) {
        self.service = service
        self.account = account
        self.accessGroup = accessGroup
    }

    /// The data protection keychain first; the legacy one only where it is not available.
    private var keychains: [Bool] { [true, false] }

    private func baseQuery(dataProtection: Bool) -> [String: Any] {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        if dataProtection {
            query[kSecUseDataProtectionKeychain as String] = true
            if let accessGroup { query[kSecAttrAccessGroup as String] = accessGroup }
        }
        return query
    }

    public func load() throws -> StoredPairing? {
        for dataProtection in keychains {
            var query = baseQuery(dataProtection: dataProtection)
            query[kSecReturnData as String] = true
            query[kSecMatchLimit as String] = kSecMatchLimitOne

            // Never let the Keychain put a dialog on screen. This call is on the launch path, and
            // a modal prompt there hangs an app that has no window and no Dock icon to show for
            // it — it simply looks as though it failed to start.
            if !dataProtection {
                let context = LAContext()
                context.interactionNotAllowed = true
                query[kSecUseAuthenticationContext as String] = context
            }

            var item: CFTypeRef?
            let status = SecItemCopyMatching(query as CFDictionary, &item)

            switch status {
            case errSecSuccess:
                guard let data = item as? Data else { throw KeychainError.unexpectedStatus(status) }
                return try JSONDecoder().decode(StoredPairing.self, from: data)

            case errSecItemNotFound, errSecMissingEntitlement:
                // Nothing here, or this keychain is not open to this build. Try the other.
                continue

            case errSecInteractionNotAllowed, errSecAuthFailed:
                // Present but unreadable: written by a previous build of this app, whose signature
                // this one no longer matches. Take it away rather than leave it to prompt on every
                // future attempt; the secret is unrecoverable either way and pairing again is a
                // few seconds' work.
                SecItemDelete(baseQuery(dataProtection: dataProtection) as CFDictionary)
                return nil

            default:
                throw KeychainError.unexpectedStatus(status)
            }
        }
        return nil
    }

    public func save(_ pairing: StoredPairing) throws {
        let data = try JSONEncoder().encode(pairing)
        var lastStatus: OSStatus = errSecSuccess

        for dataProtection in keychains {
            // Replace rather than update-or-insert: pairing again should not leave a stale secret
            // behind if the previous item was written by an older build with different attributes.
            SecItemDelete(baseQuery(dataProtection: dataProtection) as CFDictionary)

            var query = baseQuery(dataProtection: dataProtection)
            query[kSecValueData as String] = data
            query[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

            let status = SecItemAdd(query as CFDictionary, nil)
            if status == errSecSuccess { return }
            lastStatus = status
            // A build without the entitlement cannot use the data protection keychain at all.
            if status != errSecMissingEntitlement { break }
        }
        throw KeychainError.unexpectedStatus(lastStatus)
    }

    public func clear() throws {
        for dataProtection in keychains {
            let status = SecItemDelete(baseQuery(dataProtection: dataProtection) as CFDictionary)
            guard status == errSecSuccess
                || status == errSecItemNotFound
                || status == errSecMissingEntitlement
            else {
                throw KeychainError.unexpectedStatus(status)
            }
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
