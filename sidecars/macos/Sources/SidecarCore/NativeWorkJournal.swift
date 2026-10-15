import Darwin
import Foundation

public enum NativeWorkJournalError: Error, Sendable {
    case unavailable, contended, changed, closed
}

/// A stable, non-inherited parent lock spans loading, transitions and durable replacement.
/// This directory lives outside working media; the app must retain its store ID independently.
public final class NativeWorkJournal: @unchecked Sendable {
    enum Checkpoint: Sendable { case temporarySynced, replaced, filesystemSynced, reopening }
    public static let fileName = "native-work.json"
    public static let coordinatorName = "native-work.lock"
    public private(set) var storeId = NativeWorkLedger.zero
    private struct Identity: Equatable { let device: dev_t; let inode: ino_t }
    private struct Snapshot { let identity: Identity; let bytes: Data }
    private let gate = NSLock()
    private let directory: URL
    private var directoryFD: Int32 = -1
    private var coordinatorFD: Int32 = -1
    private var directoryIdentity: Identity?
    private var coordinatorIdentity: Identity?
    private var expected: Snapshot?
    private let checkpoint: @Sendable (Checkpoint) throws -> Void
    private let checkpointStore: (any NativeWorkCheckpointStore)?
    private var checkpointState: NativeWorkCheckpoint?

    public convenience init(directory: URL, expectedStoreId: UUID? = nil) throws {
        try self.init(directory: directory, expectedStoreId: expectedStoreId, checkpoint: { _ in })
    }

    public convenience init(directory: URL, checkpointStore: any NativeWorkCheckpointStore) throws {
        try self.init(directory: directory, expectedStoreId: nil, checkpointStore: checkpointStore, checkpoint: { _ in })
    }

    init(directory: URL, expectedStoreId: UUID? = nil,
         checkpointStore: (any NativeWorkCheckpointStore)? = nil,
         checkpoint: @escaping @Sendable (Checkpoint) throws -> Void) throws {
        self.checkpoint = checkpoint
        self.checkpointStore = checkpointStore
        self.directory = directory.standardizedFileURL
        guard directory.isFileURL, NativeWorkLedger.validPath(directory.path), expectedStoreId != NativeWorkLedger.zero else {
            throw NativeWorkJournalError.unavailable
        }
        do {
            checkpointState = try checkpointStore?.read()
            if let state = checkpointState {
                guard state.isValid, state.directory == self.directory.path,
                      expectedStoreId == nil || expectedStoreId == state.storeId else { throw NativeWorkJournalError.changed }
            }
            let pinnedStoreId = checkpointState?.storeId ?? expectedStoreId
            if pinnedStoreId == nil {
                try FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true,
                    attributes: [.posixPermissions: 0o700])
            }
            directoryFD = Darwin.open(self.directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
            guard directoryFD >= 0 else { throw NativeWorkJournalError.unavailable }
            directoryIdentity = try identity(directoryFD, isDirectory: true)
            var filesystem = statfs()
            guard fstatfs(directoryFD, &filesystem) == 0, filesystem.f_flags & UInt32(MNT_LOCAL) != 0 else {
                throw NativeWorkJournalError.unavailable
            }
            var created = false
            if pinnedStoreId == nil {
                coordinatorFD = openat(directoryFD, Self.coordinatorName, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
                created = coordinatorFD >= 0
                if coordinatorFD < 0 && errno != EEXIST { throw NativeWorkJournalError.unavailable }
            }
            if coordinatorFD < 0 { coordinatorFD = openat(directoryFD, Self.coordinatorName, O_RDWR | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK) }
            guard coordinatorFD >= 0 else { throw NativeWorkJournalError.unavailable }
            coordinatorIdentity = try identity(coordinatorFD)
            guard flock(coordinatorFD, LOCK_EX | LOCK_NB) == 0 else { throw NativeWorkJournalError.contended }
            if created {
                guard try readOptional(Self.fileName) == nil else { throw NativeWorkJournalError.changed }
                storeId = UUID()
                try writeAll(coordinatorFD, Data((storeId.uuidString + "\n").utf8))
                try synchronize(coordinatorFD)
                try commit(NativeWorkLedger.encode([]))
            } else {
                let header = try readBytes(coordinatorFD, maximum: 128)
                guard let text = String(data: header, encoding: .utf8), text.hasSuffix("\n"),
                      let id = UUID(uuidString: String(text.dropLast())), id != NativeWorkLedger.zero,
                      text == id.uuidString + "\n", pinnedStoreId == nil || pinnedStoreId == id,
                      let snapshot = try readOptional(Self.fileName) else { throw NativeWorkJournalError.unavailable }
                storeId = id
                _ = try NativeWorkLedger.decode(snapshot.bytes)
                try validateCheckpoint(snapshot.bytes)
                expected = snapshot
                try validateBinding()
                try validateExpected()
                // A prior parent's save may have renamed successfully but failed its final
                // flush. Reading those visible bytes alone cannot authorize receipt removal.
                try checkpoint(.reopening)
                guard fsync(directoryFD) == 0 else { throw NativeWorkJournalError.unavailable }
                try synchronize(coordinatorFD)
                try confirmCheckpoint(snapshot.bytes)
            }
        } catch { close(); throw error }
    }

    deinit { close() }

    public func close() {
        gate.withLock { closeDescriptors() }
    }

    private func closeDescriptors() {
        if coordinatorFD >= 0 { _ = Darwin.close(coordinatorFD); coordinatorFD = -1 }
        if directoryFD >= 0 { _ = Darwin.close(directoryFD); directoryFD = -1 }
    }

    public func load() throws -> [NativeWorkObligation] {
        try gate.withLock {
            try validateBinding()
            guard let expected, let current = try readOptional(Self.fileName),
                  current.identity == expected.identity, current.bytes == expected.bytes else { throw NativeWorkJournalError.changed }
            return try NativeWorkLedger.decode(current.bytes)
        }
    }

    public func save(_ obligations: [NativeWorkObligation]) throws {
        let data = try NativeWorkLedger.encode(obligations)
        try gate.withLock { try commit(data) }
    }

    public func compareAndSave(previous: [NativeWorkObligation], replacement: [NativeWorkObligation]) throws {
        let data = try NativeWorkLedger.encode(replacement)
        try gate.withLock {
            guard let expected, try NativeWorkLedger.decode(expected.bytes) == previous else {
                throw NativeWorkJournalError.changed
            }
            try commit(data)
        }
    }

    private func commit(_ data: Data) throws {
        try validateBinding()
        try validateExpected()
        let temporary = ".native-work-" + UUID().uuidString + ".tmp"
        let descriptor = openat(directoryFD, temporary, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
        guard descriptor >= 0 else { throw NativeWorkJournalError.unavailable }
        let writtenIdentity = try identity(descriptor)
        var renamed = false
        var committed = false
        var checkpointAttempted = false
        defer {
            _ = Darwin.close(descriptor)
            if !renamed, (try? readOptional(temporary)?.identity) == writtenIdentity {
                _ = unlinkat(directoryFD, temporary, 0)
            }
            // No receipt consumption or resource release may follow an unconfirmed transition.
            if (renamed || checkpointAttempted) && !committed { closeDescriptors() }
        }
        try writeAll(descriptor, data)
        try synchronize(descriptor)
        try checkpoint(.temporarySynced)
        try validateBinding()
        try validateExpected()
        guard let temporaryState = try readOptional(temporary), temporaryState.identity == writtenIdentity,
              temporaryState.bytes == data else { throw NativeWorkJournalError.changed }
        if let state = checkpointState, let checkpointStore {
            guard state.pending == nil, let expected,
                  state.committed == NativeWorkCheckpoint.digest(expected.bytes) else { throw NativeWorkJournalError.changed }
            let pending = NativeWorkCheckpoint(storeId: storeId, directory: directory.path,
                committed: state.committed, pending: NativeWorkCheckpoint.digest(data))
            checkpointAttempted = true
            try checkpointStore.write(pending, replacing: state)
            guard try checkpointStore.read() == pending else { throw NativeWorkJournalError.changed }
            checkpointState = pending
        } else if checkpointStore != nil {
            guard expected == nil, try NativeWorkLedger.decode(data).isEmpty else { throw NativeWorkJournalError.changed }
        }
        guard renameat(directoryFD, temporary, directoryFD, Self.fileName) == 0 else { throw NativeWorkJournalError.unavailable }
        renamed = true
        try checkpoint(.replaced)
        guard fsync(directoryFD) == 0 else { throw NativeWorkJournalError.unavailable }
        try synchronize(coordinatorFD)
        try checkpoint(.filesystemSynced)
        checkpointAttempted = checkpointStore != nil
        try confirmCheckpoint(data)
        expected = Snapshot(identity: writtenIdentity, bytes: data)
        committed = true
    }

    private func validateExpected() throws {
        let current = try readOptional(Self.fileName)
        if let expected {
            guard current?.identity == expected.identity, current?.bytes == expected.bytes else { throw NativeWorkJournalError.changed }
        } else if current != nil { throw NativeWorkJournalError.changed }
    }

    private func validateCheckpoint(_ data: Data) throws {
        guard checkpointStore != nil else { return }
        if let state = checkpointState {
            let digest = NativeWorkCheckpoint.digest(data)
            guard state.committed == digest || state.pending == digest else { throw NativeWorkJournalError.changed }
        } else {
            // A first-start crash can leave an empty journal before its anchor is inserted.
            // Outstanding work can never be adopted without independent checkpoint authority.
            guard try NativeWorkLedger.decode(data).isEmpty else { throw NativeWorkJournalError.changed }
        }
    }

    private func confirmCheckpoint(_ data: Data) throws {
        guard let checkpointStore else { return }
        try validateCheckpoint(data)
        let confirmed = NativeWorkCheckpoint(storeId: storeId, directory: directory.path,
            committed: NativeWorkCheckpoint.digest(data), pending: nil)
        if checkpointState != confirmed { try checkpointStore.write(confirmed, replacing: checkpointState) }
        guard try checkpointStore.read() == confirmed else { throw NativeWorkJournalError.changed }
        checkpointState = confirmed
    }

    private func validateBinding() throws {
        guard directoryFD >= 0, coordinatorFD >= 0 else { throw NativeWorkJournalError.closed }
        let fresh = Darwin.open(directory.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard fresh >= 0 else { throw NativeWorkJournalError.changed }
        defer { _ = Darwin.close(fresh) }
        guard try identity(fresh, isDirectory: true) == directoryIdentity,
              let lock = try readOptional(Self.coordinatorName, maximum: 128), lock.identity == coordinatorIdentity,
              lock.bytes == Data((storeId.uuidString + "\n").utf8) else { throw NativeWorkJournalError.changed }
        if let checkpointStore {
            guard try checkpointStore.read() == checkpointState else { throw NativeWorkJournalError.changed }
        }
    }

    private func readOptional(_ name: String, maximum: Int = NativeWorkLedger.maximumBytes) throws -> Snapshot? {
        let descriptor = openat(directoryFD, name, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK)
        if descriptor < 0 && errno == ENOENT { return nil }
        guard descriptor >= 0 else { throw NativeWorkJournalError.unavailable }
        defer { _ = Darwin.close(descriptor) }
        return Snapshot(identity: try identity(descriptor), bytes: try readBytes(descriptor, maximum: maximum))
    }

    private func identity(_ descriptor: Int32, isDirectory: Bool = false) throws -> Identity {
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_uid == getuid(), info.st_mode & 0o022 == 0,
              info.st_mode & mode_t(S_IFMT) == mode_t(isDirectory ? S_IFDIR : S_IFREG),
              isDirectory || info.st_nlink == 1 else { throw NativeWorkJournalError.unavailable }
        return Identity(device: info.st_dev, inode: info.st_ino)
    }

    private func readBytes(_ descriptor: Int32, maximum: Int) throws -> Data {
        var info = stat()
        guard fstat(descriptor, &info) == 0, info.st_size >= 0, info.st_size <= maximum,
              lseek(descriptor, 0, SEEK_SET) == 0 else { throw NativeWorkJournalError.unavailable }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = Darwin.read(descriptor, &buffer, buffer.count)
            if count == 0 { return data }
            if count < 0 {
                if errno == EINTR { continue }
                throw NativeWorkJournalError.unavailable
            }
            guard data.count + count <= maximum else { throw NativeWorkJournalError.unavailable }
            data.append(contentsOf: buffer.prefix(count))
        }
    }

    private func writeAll(_ descriptor: Int32, _ data: Data) throws {
        try data.withUnsafeBytes { bytes in
            var written = 0
            while written < bytes.count {
                let count = Darwin.write(descriptor, bytes.baseAddress!.advanced(by: written), bytes.count - written)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { throw NativeWorkJournalError.unavailable }
                written += count
            }
        }
    }

    private func synchronize(_ descriptor: Int32) throws {
        guard fsync(descriptor) == 0, fcntl(descriptor, F_FULLFSYNC) == 0 else { throw NativeWorkJournalError.unavailable }
    }
}
