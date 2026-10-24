import Darwin
import Foundation
import Testing
@testable import SidecarCore

@Suite("Native RAM mountpoint binding")
struct NativeRamMountDirectoryTests {
    private func root() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        return root
    }

    @Test("a shared parent cannot acquire a native RAM mountpoint")
    func sharedParent() throws {
        let root = try root(); defer { try? FileManager.default.removeItem(at: root) }
        let owner = UUID()
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: root.path)
        #expect(throws: MacNativeRamFactoryAllocation.Unconfirmed.self) { try NativeRamMountDirectory(parent: root, owner: owner) }
        #expect(!FileManager.default.fileExists(atPath: root.appendingPathComponent(owner.uuidString).path))
    }

    @Test("a redirected leaf cannot acquire authority or remove a neighbouring directory")
    func redirectedLeaf() throws {
        let root = try root(); defer { try? FileManager.default.removeItem(at: root) }
        let neighbour = root.appendingPathComponent("keep")
        try FileManager.default.createDirectory(at: neighbour, withIntermediateDirectories: false)
        let sentinel = neighbour.appendingPathComponent("owned-copy")
        try Data("preserve neighbouring copy".utf8).write(to: sentinel)
        var directory: NativeRamMountDirectory? = try NativeRamMountDirectory(parent: root, owner: UUID())
        let mount = try #require(directory?.mountPoint)
        try #require(rmdir(mount.path) == 0)
        try FileManager.default.createSymbolicLink(at: mount, withDestinationURL: neighbour)
        #expect(directory?.isBound == false)
        directory = nil
        #expect(try Data(contentsOf: sentinel) == Data("preserve neighbouring copy".utf8))
        #expect(try FileManager.default.destinationOfSymbolicLink(atPath: mount.path) == neighbour.path)
    }

    @Test("renaming the private parent cannot redirect cleanup into its replacement")
    func replacedParent() throws {
        let root = try root(); defer { try? FileManager.default.removeItem(at: root) }
        let parent = root.appendingPathComponent("parent"), moved = root.appendingPathComponent("moved")
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let owner = UUID()
        var directory: NativeRamMountDirectory? = try NativeRamMountDirectory(parent: parent, owner: owner)
        try FileManager.default.moveItem(at: parent, to: moved)
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let replacement = parent.appendingPathComponent(owner.uuidString)
        try FileManager.default.createDirectory(at: replacement, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
        let sentinel = replacement.appendingPathComponent("owned-copy")
        try Data("keep replacement".utf8).write(to: sentinel)
        #expect(directory?.isBound == false)
        directory = nil
        #expect(try Data(contentsOf: sentinel) == Data("keep replacement".utf8))
        #expect(FileManager.default.fileExists(atPath: moved.appendingPathComponent(owner.uuidString).path))
    }
}
