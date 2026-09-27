import Foundation
import Testing
@testable import SidecarCore

/// Creates and destroys a real RAM disk, so the hdiutil/newfs_hfs/diskutil sequence is proved
/// rather than assumed. Skipped unless asked for, because a test that mounts volumes on someone's
/// machine — or a CI runner — should be a deliberate act:
///
///     OPTIMISARR_LIVE_RAMDISK=1 swift test --filter LiveRamDisk
@Suite("Live RAM disk", .serialized, .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_LIVE_RAMDISK"] != nil))
struct LiveRamDiskTests {
    @Test("the production job runner ejects its RAM volume after delivery, encoder failure or cancellation",
          .enabled(if: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"] != nil),
          arguments: [LiveEncodeOutcome.success, .failure, .cancelled])
    func encodesInMemory(ending: LiveEncodeOutcome) async throws {
        let ffmpeg = URL(fileURLWithPath: ProcessInfo.processInfo.environment["OPTIMISARR_FFMPEG"]!)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let fixture = root.appendingPathComponent("fixture.mp4")
        let generated = try await ProcessTranscodeRunner().run(ffmpeg, [
            "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", fixture.path,
        ]) { _ in }
        #expect(generated.exitCode == 0, "\(generated.stderr)")
        let source = try Data(contentsOf: fixture)
        let server = FakeWorkerServer(sourceBytes: source)
        let recorder = ArgumentRecorder()
        let budget = MemoryWorkBudget()
        let runner = JobRunner(
            client: SidecarClient(transport: server), ffmpeg: ffmpeg,
            runner: RecordedLiveTranscoder(recorder: recorder, ending: ending),
            scratchRoot: root.appendingPathComponent("disk-fallback"),
            settings: SettingsSnapshot(workLocation: .memory, memoryBudgetBytes: 128 * 1024 * 1024), memoryBudget: budget)
        let assignment = Assignment(
            leaseId: UUID().uuidString, jobId: 12, sourceBytes: Int64(source.count),
            videoEncoder: "hevc_videotoolbox", renewWithinSeconds: 30,
            arguments: ["-y", "-progress", "pipe:1", "-nostats", "-hwaccel", "videotoolbox",
                        "-i", "{{input}}", "-c:v", "hevc_videotoolbox", "-q:v", "60", "{{output}}.mp4"],
            outputExtension: "mp4",
            quality: QualityRequirement(measure: false, model: "vmaf_v0.6.1", frameSubsample: 1,
                clipVmaf: false, minimumHarmonicMean: 93, minimumMinimum: 80, commands: [], sampling: "None"), maxCandidateBytes: 10 * 1024 * 1024)
        let outcome = await runner.execute(assignment,
            pairing: StoredPairing(serverAddress: "localhost:8787", credential: "test", workerId: 3)) { _ in }
        let arguments = try #require(recorder.all.first)
        let input = arguments[try #require(arguments.firstIndex(of: "-i")) + 1]
        let output = try #require(arguments.last)
        #expect(input.hasPrefix("/Volumes/\(RamDisk.volumePrefix)"))
        #expect(output.hasPrefix("/Volumes/\(RamDisk.volumePrefix)"))
        let volume = URL(fileURLWithPath: output).deletingLastPathComponent().deletingLastPathComponent()
        #expect(!FileManager.default.fileExists(atPath: volume.path))
        #expect(budget.reservedBytes == 0)
        if ending != .success {
            guard case .released = outcome else {
                Issue.record("expected a released job, got \(outcome)")
                return
            }
            #expect(server.released)
            #expect(server.deliveredFile == nil)
            return
        }
        guard case .delivered = outcome else {
            Issue.record("expected RAM-backed delivery, got \(outcome)")
            return
        }
        let candidate = root.appendingPathComponent("delivered.mp4")
        try #require(server.deliveredFile).write(to: candidate)
        let decoded = try await ProcessTranscodeRunner().run(ffmpeg,
            ["-v", "error", "-xerror", "-i", candidate.path, "-f", "null", "-"]) { _ in }
        #expect(decoded.exitCode == 0, "\(decoded.stderr)")
    }

    @Test("a real volume is created, holds a file, and leaves nothing behind")
    func roundTrip() throws {
        let disk = try #require(RamDisk.create(bytes: 64 * 1024 * 1024), "the Mac would not create a RAM disk")
        defer {
            if FileManager.default.fileExists(atPath: disk.mountPoint.path) { disk.destroy() }
        }
        #expect(disk.mountPoint.lastPathComponent.hasPrefix(RamDisk.volumePrefix))
        #expect(FileManager.default.fileExists(atPath: disk.mountPoint.path))
        let available = try #require(JobRunner.availableScratchBytes(at: disk.mountPoint),
                                     "the job runner must be able to check RAM volume capacity")
        #expect(available >= 64 * 1024 * 1024)

        // The volume has to be big enough for what it was asked to hold, which is the part the
        // filesystem overhead allowance exists for.
        let file = disk.mountPoint.appendingPathComponent("probe.bin")
        try Data(repeating: 3, count: 32 * 1024 * 1024).write(to: file)
        #expect(FileManager.default.fileExists(atPath: file.path))

        disk.destroy()
        // Leaving one behind holds real memory until the Mac reboots, with nothing on screen to
        // say so, which is the failure this whole type is written to avoid.
        #expect(!FileManager.default.fileExists(atPath: disk.mountPoint.path))
    }

    @Test("sweeping removes one left behind by a crash")
    func sweepsStrays() throws {
        let disk = try #require(RamDisk.create(bytes: 32 * 1024 * 1024))
        let path = disk.mountPoint.path
        defer {
            if FileManager.default.fileExists(atPath: path) { disk.destroy() }
        }
        // Deliberately not destroyed: this is what a crash leaves.
        RamDisk.sweepStrays()

        #expect(!FileManager.default.fileExists(atPath: path))
    }
}

enum LiveEncodeOutcome: Sendable { case success, failure, cancelled }

private struct RecordedLiveTranscoder: TranscodeRunner {
    let recorder: ArgumentRecorder
    let ending: LiveEncodeOutcome

    func run(_ executable: URL, _ arguments: [String],
             progress: @escaping @Sendable (Double) -> Void) async throws -> (exitCode: Int32, stderr: String) {
        recorder.record(arguments)
        if ending == .failure { return (1, "Injected encoder failure") }
        if ending == .cancelled { throw CancellationError() }
        return try await ProcessTranscodeRunner().run(executable, arguments, progress: progress)
    }
}
