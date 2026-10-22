#!/usr/bin/env swift
import Foundation
import Security

// Reads the certificate export password from stdin, never from argv or a file.
// The disposable keychain password exists only for this process and its ACL helper.
func run() throws -> Int32 {
    let args = Array(CommandLine.arguments.dropFirst())
    guard args.count >= 2, let password = readLine(), !password.isEmpty else {
        throw NSError(domain: "SigningKeychain", code: 1,
            userInfo: [NSLocalizedDescriptionKey: "Usage: with-signing-keychain.swift certificate.p12 command [arguments…]; supply export password on stdin"])
    }
    let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        .appendingPathComponent("build/signing-" + UUID().uuidString)
    try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    defer { try? FileManager.default.removeItem(at: root) }
    let path = root.appendingPathComponent("signing.keychain-db")
    let keychainPassword = UUID().uuidString + UUID().uuidString
    var keychain: SecKeychain?
    let status = keychainPassword.withCString { bytes in
        SecKeychainCreate(path.path, UInt32(keychainPassword.utf8.count), bytes, false, nil, &keychain)
    }
    guard status == errSecSuccess, let keychain else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(status)) }
    defer { SecKeychainDelete(keychain) }
    let unlockStatus = keychainPassword.withCString { bytes in
        SecKeychainUnlock(keychain, UInt32(keychainPassword.utf8.count), bytes, true)
    }
    guard unlockStatus == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(unlockStatus)) }
    var trusted: SecTrustedApplication?
    guard SecTrustedApplicationCreateFromPath("/usr/bin/codesign", &trusted) == errSecSuccess, let trusted else {
        throw NSError(domain: "SigningKeychain", code: 4)
    }
    var access: SecAccess?
    guard SecAccessCreate("Optimisarr disposable signing key" as CFString, [trusted] as CFArray, &access) == errSecSuccess,
          let access else { throw NSError(domain: "SigningKeychain", code: 5) }
    let data = try Data(contentsOf: URL(fileURLWithPath: args[0]))
    var imported: CFArray?
    let importedStatus = SecPKCS12Import(data as CFData, [kSecImportExportPassphrase as String: password,
        kSecImportExportKeychain as String: keychain, kSecImportExportAccess as String: access] as CFDictionary, &imported)
    guard importedStatus == errSecSuccess else { throw NSError(domain: NSOSStatusErrorDomain, code: Int(importedStatus)) }
    // Grant Apple's signing tools access to this temporary key only. No login-keychain
    // password, global search list or default-keychain preference is involved.
    let acl = Process()
    acl.executableURL = URL(fileURLWithPath: "/usr/bin/security")
    acl.arguments = ["set-key-partition-list", "-S", "apple-tool:,apple:", "-s", "-k", keychainPassword, path.path]
    acl.standardOutput = FileHandle.nullDevice
    acl.standardError = FileHandle.nullDevice
    try acl.run(); acl.waitUntilExit()
    guard acl.terminationStatus == 0 else { throw NSError(domain: "SigningKeychain", code: 2) }
    let unlock = Process()
    unlock.executableURL = URL(fileURLWithPath: "/usr/bin/security")
    unlock.arguments = ["unlock-keychain", "-p", keychainPassword, path.path]
    unlock.standardOutput = FileHandle.nullDevice
    unlock.standardError = FileHandle.nullDevice
    try unlock.run(); unlock.waitUntilExit()
    guard unlock.terminationStatus == 0 else { throw NSError(domain: "SigningKeychain", code: 6) }
    let command = Process()
    command.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    command.arguments = Array(args.dropFirst())
    command.environment = ProcessInfo.processInfo.environment.merging(["SIGNING_KEYCHAIN": path.path]) { _, new in new }
    command.standardInput = FileHandle.nullDevice
    try command.run(); command.waitUntilExit()
    return command.terminationStatus
}
do { exit(try run()) }
catch { fputs("Temporary signing keychain failed: \(error.localizedDescription)\n", stderr); exit(1) }
