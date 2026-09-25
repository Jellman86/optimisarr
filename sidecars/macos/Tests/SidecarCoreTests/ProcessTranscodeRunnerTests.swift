import Foundation
import Testing
@testable import SidecarCore

/// What a job is allowed to depend on when it runs a process.
///
/// These run `/bin/sh`, not FFmpeg: the behaviour under test is the runner's, and a shell can be
/// made to do the one thing FFmpeg did by accident.
@Suite("Running a process")
struct ProcessTranscodeRunnerTests {
    private static let shell = URL(fileURLWithPath: "/bin/sh")

    @Test("a candidate beyond its frozen size budget stops its encoder")
    func sizeBudgetStopsProcess() async throws {
        let output = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: output) }
        let started = Date()

        do {
            _ = try await ProcessTranscodeRunner().run(Self.shell,
                ["-c", "printf '1234567890123' > \"$1\"; exec sleep 120", "-", output.path],
                sizeBudget: OutputSizeBudget(output: output, maxBytes: 12), progress: { _ in })
            Issue.record("expected the runner to stop an oversized candidate")
        } catch let exceeded as OutputSizeExceeded {
            #expect(exceeded.observedBytes == 13)
            #expect(exceeded.maxBytes == 12)
        }
        #expect(Date().timeIntervalSince(started) < 10)
    }

    /// Collects what the progress callback was told, from whichever queue tells it.
    private final class Reported: @unchecked Sendable {
        private let gate = NSLock()
        private var values: [Double] = []
        func note(_ value: Double) { gate.lock(); values.append(value); gate.unlock() }
        var all: [Double] { gate.lock(); defer { gate.unlock() }; return values }
    }

    @Test("a process that leaves a child holding its stdout still ends when it exits")
    func outlivedByAGrandchild() async throws {
        // This is the bug, reduced. The shell exits at once, but the `sleep` it started inherited
        // the stdout pipe and holds the write end open behind it, so end-of-file does not arrive
        // for another thirty seconds. Waiting for that end-of-file is what left a Mac sat in
        // "Measuring" for thirty-six minutes with no FFmpeg running at all.
        let started = Date()
        let run = try await ProcessTranscodeRunner().run(
            Self.shell, ["-c", "sleep 30 & exit 0"], progress: { _ in })

        #expect(run.exitCode == 0)
        // The grace, not the sleep. Generous against a loaded machine, and still nowhere near 30.
        #expect(Date().timeIntervalSince(started) < 10)
    }

    @Test("one job's pipes do not leave with another job's process")
    func pipesAreNotInherited() async throws {
        // The short process finishes first, but the long one started while its pipes were open.
        // Without the seal the long one carries a copy of them away and the short one's reader
        // waits on it — which is a Mac running a load probe or a preview frame beside an encode,
        // every few seconds, all day.
        let runner = ProcessTranscodeRunner()
        let long = Task {
            _ = try? await runner.run(Self.shell, ["-c", "sleep 8"], progress: { _ in })
        }

        let started = Date()
        let short = try await runner.run(Self.shell, ["-c", "sleep 1"], progress: { _ in })
        let waited = Date().timeIntervalSince(started)

        #expect(short.exitCode == 0)
        // Its own second, not the other process's eight.
        #expect(waited < 5)
        await long.value
    }

    @Test("the exit code and the end of stderr come back")
    func failureIsExplained() async throws {
        let run = try await ProcessTranscodeRunner().run(
            Self.shell, ["-c", "echo 'no such filter' >&2; exit 3"], progress: { _ in })

        #expect(run.exitCode == 3)
        #expect(run.stderr.contains("no such filter"))
    }

    @Test("progress is read as it arrives, across whatever boundaries the pipe falls on")
    func progressIsReported() async throws {
        let reported = Reported()
        // Written in three pieces with the last line left unterminated, because a read boundary
        // lands wherever the pipe happens to fill and a half-line must not be read as a whole one.
        let run = try await ProcessTranscodeRunner().run(
            Self.shell,
            ["-c", """
                printf 'frame=1\\nout_time_us=1000000\\n'
                printf 'progress=continue\\nout_time_'
                printf 'us=2500000\\nout_time_us=99'
                """],
            progress: { reported.note($0) })

        #expect(run.exitCode == 0)
        #expect(reported.all == [1, 2.5])
    }

    @Test("a command that will not launch throws rather than waiting on pipes nobody will write")
    func launchFailureThrows() async throws {
        await #expect(throws: (any Error).self) {
            try await ProcessTranscodeRunner().run(
                URL(fileURLWithPath: "/nonexistent/ffmpeg"), ["-version"], progress: { _ in })
        }
    }

    @Test("cancelling the job stops the process rather than leaving it running for nobody")
    func cancellationTerminates() async throws {
        let started = Date()
        let task = Task {
            try await ProcessTranscodeRunner().run(
                Self.shell, ["-c", "sleep 120"], progress: { _ in })
        }
        try await Task.sleep(for: .milliseconds(300))
        task.cancel()

        let run = try? await task.value
        // Terminated by a signal, so not a clean zero — what matters is that it returned at all.
        #expect(run?.exitCode != 0)
        #expect(Date().timeIntervalSince(started) < 15)
    }
}
