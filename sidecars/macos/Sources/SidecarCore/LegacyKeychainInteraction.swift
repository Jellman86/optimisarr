import Foundation
import Security

/// Legacy interaction is process-wide. Credential and accounting callers share this gate so
/// one operation cannot restore prompts while another still depends on suppression.
final class LegacyKeychainInteraction: @unchecked Sendable {
    static let shared = LegacyKeychainInteraction()
    private let gate = NSLock()
    private let readInteraction: @Sendable (inout DarwinBoolean) -> OSStatus
    private let setInteraction: @Sendable (Bool) -> OSStatus

    init(readInteraction: @escaping @Sendable (inout DarwinBoolean) -> OSStatus = { SecKeychainGetUserInteractionAllowed(&$0) },
         setInteraction: @escaping @Sendable (Bool) -> OSStatus = { SecKeychainSetUserInteractionAllowed($0) }) {
        self.readInteraction = readInteraction; self.setInteraction = setInteraction
    }

    func perform<Value>(_ body: () throws -> Value) throws -> Value {
        try gate.withLock {
            var previous = DarwinBoolean(true)
            let read = readInteraction(&previous)
            guard read == errSecSuccess else { throw KeychainError.unexpectedStatus(read) }
            let disabled = setInteraction(false)
            guard disabled == errSecSuccess else {
                _ = setInteraction(previous.boolValue)
                throw KeychainError.unexpectedStatus(disabled)
            }
            let result = Result { try body() }
            let restored = setInteraction(previous.boolValue)
            guard restored == errSecSuccess else { throw KeychainError.unexpectedStatus(restored) }
            return try result.get()
        }
    }
}
