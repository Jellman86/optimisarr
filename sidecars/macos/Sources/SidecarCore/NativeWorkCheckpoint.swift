import CryptoKit
import Foundation
import LocalAuthentication
import Security

/// Kept independently of the journal directory. A pending transition admits exactly its old
/// and new snapshots until reopening confirms which one became durable.
public struct NativeWorkCheckpoint: Codable, Equatable, Sendable {
    let schemaVersion: Int
    public let storeId: UUID
    public let directory: String
    public let committed: String
    public let pending: String?

    public init(storeId: UUID, directory: String, committed: String, pending: String?) {
        schemaVersion = 1; self.storeId = storeId; self.directory = directory
        self.committed = committed; self.pending = pending
    }

    var isValid: Bool {
        schemaVersion == 1 && storeId != NativeWorkLedger.zero && NativeWorkLedger.validPath(directory)
            && directory == URL(fileURLWithPath: directory).standardizedFileURL.path
            && Self.validDigest(committed) && (pending == nil || Self.validDigest(pending!))
    }

    public func encode() throws -> Data {
        guard isValid else { throw NativeWorkJournalError.unavailable }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(self)
        guard bytes.count <= 8192 else { throw NativeWorkJournalError.unavailable }
        return bytes
    }

    public static func decode(_ bytes: Data) throws -> Self {
        guard !bytes.isEmpty, bytes.count <= 8192, NativeWireJSON.hasUniqueProperties(bytes),
              let object = try? JSONSerialization.jsonObject(with: bytes) as? [String: Any],
              Set(object.keys).isSubset(of: Set(["schemaVersion", "storeId", "directory", "committed", "pending"])),
              let value = try? JSONDecoder().decode(Self.self, from: bytes), value.isValid else {
            throw NativeWorkJournalError.unavailable
        }
        return value
    }

    static func digest(_ bytes: Data) -> String { SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined() }
    private static func validDigest(_ value: String) -> Bool {
        value.utf8.count == 64 && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }
}

/// The journal coordinator must remain held throughout every read/write/readback transaction.
/// Each anchor is bound to one fixed journal path; all parents use that same coordinator.
public protocol NativeWorkCheckpointStore: Sendable {
    func read() throws -> NativeWorkCheckpoint?
    func write(_ value: NativeWorkCheckpoint, replacing expected: NativeWorkCheckpoint?) throws
}

/// A nonsynchronizing item separate from pairing. The backend is selected explicitly once; Only ItemNotFound is absence;
/// locked, unreadable, unentitled or malformed state blocks admission without deletion/fallback.
public struct KeychainNativeWorkCheckpointStore: NativeWorkCheckpointStore {
    public enum Backend: Sendable { case fileBased, dataProtection }
    private let backend: Backend
    private final class Gate: @unchecked Sendable { let lock = NSLock() }
    private static let gate = Gate()
    private let service: String
    private let account: String
    private let accessGroup: String?

    public init(service: String = "uk.optimisarr.sidecar.native-work", account: String = "journal-checkpoint",
                accessGroup: String? = nil, backend: Backend = .fileBased) {
        self.backend = backend
        self.service = service; self.account = account; self.accessGroup = accessGroup
    }

    private var query: [String: Any] {
        var value: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account,
            kSecUseDataProtectionKeychain as String: backend == .dataProtection, kSecAttrSynchronizable as String: false]
        if backend == .dataProtection {
            let context = LAContext(); context.interactionNotAllowed = true
            value[kSecUseAuthenticationContext as String] = context
        }
        if let accessGroup { value[kSecAttrAccessGroup as String] = accessGroup }
        return value
    }

    public func read() throws -> NativeWorkCheckpoint? { try Self.gate.lock.withLock { try readUnlocked() } }

    private func readUnlocked() throws -> NativeWorkCheckpoint? {
        var search = query
        search[kSecReturnData as String] = true
        search[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = try perform { SecItemCopyMatching(search as CFDictionary, &item) }
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
        guard let data = item as? Data else { throw NativeWorkJournalError.unavailable }
        return try NativeWorkCheckpoint.decode(data)
    }

    private func perform<Value>(_ body: () -> Value) throws -> Value {
        if backend == .dataProtection { return body() }
        return try LegacyKeychainInteraction.shared.perform(body)
    }

    public func write(_ value: NativeWorkCheckpoint, replacing expected: NativeWorkCheckpoint?) throws {
        let data = try value.encode()
        try Self.gate.lock.withLock {
            guard try readUnlocked() == expected else { throw NativeWorkJournalError.changed }
            let status: OSStatus
            if expected == nil {
                var insert = query
                insert[kSecValueData as String] = data
                if backend == .dataProtection { insert[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly }
                status = try perform { SecItemAdd(insert as CFDictionary, nil) }
            } else {
                status = try perform { SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary) }
            }
            guard status == errSecSuccess else { throw KeychainError.unexpectedStatus(status) }
            guard try readUnlocked() == value else { throw NativeWorkJournalError.changed }
        }
    }
}
