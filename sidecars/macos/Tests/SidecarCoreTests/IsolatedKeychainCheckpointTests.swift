import Foundation
import Security
import Testing
@testable import SidecarCore

@Suite("Explicit checkpoint keychain isolation", .serialized)
struct IsolatedKeychainCheckpointTests {
    @Test("an explicit keychain isolates equal service names and never falls back when locked or missing")
    func isolatedStore() throws {
        func searchPaths() throws -> [String] {
            var list: CFArray?
            #expect(SecKeychainCopySearchList(&list) == errSecSuccess)
            return try (try #require(list) as! [SecKeychain]).map { keychain in
                var size: UInt32 = 4096
                var bytes = [CChar](repeating: 0, count: Int(size))
                #expect(SecKeychainGetPath(keychain, &size, &bytes) == errSecSuccess)
                return String(cString: bytes)
            }
        }
        let originalSearch = try searchPaths()
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let password = "disposable-checkpoint-fixture"
        func create(_ name: String) throws -> (URL, SecKeychain) {
            let path = root.appendingPathComponent(name)
            var keychain: SecKeychain?
            let status = password.withCString { bytes in
                SecKeychainCreate(path.path, UInt32(password.utf8.count), bytes, false, nil, &keychain)
            }
            #expect(status == errSecSuccess)
            return (path, try #require(keychain))
        }
        let (firstPath, first) = try create("first.keychain-db")
        defer { SecKeychainDelete(first) }
        let (secondPath, second) = try create("second.keychain-db")
        defer { SecKeychainDelete(second) }
        #expect(try searchPaths() == originalSearch)
        let service = "uk.optimisarr.acceptance." + UUID().uuidString
        let a = KeychainNativeWorkCheckpointStore(service: service, fileBasedKeychain: firstPath)
        let b = KeychainNativeWorkCheckpointStore(service: service, fileBasedKeychain: secondPath)
        let value = NativeWorkCheckpoint(storeId: UUID(), directory: root.path,
            committed: String(repeating: "a", count: 64), pending: nil)
        #expect(try a.read() == nil)
        try a.write(value, replacing: nil)
        #expect(try a.read() == value)
        #expect(try b.read() == nil)
        #expect(throws: (any Error).self) { try a.write(value, replacing: nil) }
        let next = NativeWorkCheckpoint(storeId: value.storeId, directory: root.path,
            committed: String(repeating: "b", count: 64), pending: nil)
        try a.write(next, replacing: value)
        #expect(try a.read() == next)
        #expect(try b.read() == nil)
        #expect(SecKeychainLock(first) == errSecSuccess)
        #expect(throws: (any Error).self) { try a.read() }
        let unlocked = password.withCString { bytes in
            SecKeychainUnlock(first, UInt32(password.utf8.count), bytes, true)
        }
        #expect(unlocked == errSecSuccess)
        #expect(try a.read() == next)
        let missing = KeychainNativeWorkCheckpointStore(service: service,
            fileBasedKeychain: root.appendingPathComponent("absent.keychain-db"))
        #expect(throws: (any Error).self) { try missing.read() }
        let alias = root.appendingPathComponent("alias.keychain-db")
        try FileManager.default.createSymbolicLink(at: alias, withDestinationURL: firstPath)
        let linked = KeychainNativeWorkCheckpointStore(service: service, fileBasedKeychain: alias)
        #expect(throws: (any Error).self) { try linked.read() }
        let incompatible = KeychainNativeWorkCheckpointStore(service: service, backend: .dataProtection,
            fileBasedKeychain: firstPath)
        #expect(throws: (any Error).self) { try incompatible.read() }
    }
}
