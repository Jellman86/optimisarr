import Foundation
import Testing
@testable import SidecarCore

@Suite("Native quality host bridge")
struct NativeQualityHostTests {
    @Test("fragmented progress and final events retain their identity")
    func frames() throws {
        var frames = NativeQualityFrames(expectedJobId: 12)
        #expect(try frames.append(Data("{\"version\":1,\"type\":\"pro".utf8)).isEmpty)
        let events = try frames.append(Data("gress\",\"jobId\":12,\"stage\":\"Measuring\",\"encodedSeconds\":2.5}\n".utf8))
        #expect(events.count == 1)
        #expect(events.first?.encodedSeconds == 2.5)
        #expect(frames.terminal == nil)
        _ = try frames.append(Data(Self.completed.utf8))
        #expect(frames.terminal?.delivered == true)
        #expect(try frames.finish(exitCode: 0).jobId == 12)
    }

    @Test("an event alone cannot confirm delivery after a failed process")
    func processExitRequired() throws {
        var frames = NativeQualityFrames(expectedJobId: 12)
        _ = try frames.append(Data(Self.completed.utf8))
        #expect(throws: NativeQualityHostError.self) { try frames.finish(exitCode: 1) }
        var refused = NativeQualityFrames(expectedJobId: 12)
        _ = try refused.append(Data(Self.completed.replacingOccurrences(of: "\"delivered\":true", with: "\"delivered\":false").utf8))
        #expect(throws: NativeQualityHostError.self) { try refused.finish(exitCode: 2) }
    }

    @Test("missing, truncated, oversized, wrong-job or trailing events fail closed")
    func invalidFrames() {
        for value in [
            "", "{}", "{}\n", "{\"version\":2,\"type\":\"refused\"}\n",
            "{\"version\":1,\"type\":\"progress\",\"jobId\":99}\n",
            "{\"version\":true,\"type\":\"refused\"}\n",
            String(repeating: "x", count: 16 * 1024 + 1),
            Self.completed + Self.completed,
            Self.completed + "trailing",
            Self.completed.replacingOccurrences(of: "\"capacityBlocked\":false", with: "\"capacityBlocked\":true")
        ] {
            #expect(throws: NativeQualityHostError.self) {
                var frames = NativeQualityFrames(expectedJobId: 12)
                _ = try frames.append(Data(value.utf8))
                _ = try frames.finish(exitCode: 0)
            }
        }
    }

    @Test("capability replies cannot masquerade as job completion")
    func capabilityIdentity() {
        #expect(throws: NativeQualityHostError.self) {
            var frames = NativeQualityFrames(expectedJobId: 12)
            _ = try frames.append(Data("{\"version\":1,\"type\":\"capabilities\",\"capabilities\":{}}\n".utf8))
        }
        #expect(throws: NativeQualityHostError.self) {
            var frames = NativeQualityFrames(expectedJobId: nil)
            _ = try frames.append(Data("{\"version\":1,\"type\":\"capabilities\",\"jobId\":12,\"capabilities\":{}}\n".utf8))
        }
    }

    @Test("a real child gets one private request and must exit after its final event")
    func privatePipe() async throws {
        let tool = try Fixture("""
        import json,sys
        request=json.loads(sys.stdin.readline())
        assert request['secret']=='private-pipe-secret'
        assert len(sys.argv)==1
        print('\(Self.completed.trimmingCharacters(in: .newlines))',flush=True)
        """)
        defer { tool.remove() }
        let result = try await NativeQualityHost(executable: tool.url).exchange(
            request: Data("{\"secret\":\"private-pipe-secret\"}".utf8), expectedJobId: 12)
        #expect(result.delivered)
    }

    @Test("task cancellation stops a child while its private request remains open")
    func cancellation() async throws {
        let tool = try Fixture("import sys,time\nsys.stdin.readline()\ntime.sleep(30)")
        defer { tool.remove() }
        let started = Date()
        let task = Task { try await NativeQualityHost(executable: tool.url).exchange(request: Data("{}".utf8), expectedJobId: 12) }
        try await Task.sleep(for: .milliseconds(300))
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(Date().timeIntervalSince(started) < 5)
    }

    @Test("oversized requests never launch the child")
    func requestBound() async throws {
        await #expect(throws: NativeQualityHostError.self) {
            try await NativeQualityHost(executable: URL(fileURLWithPath: "/does-not-exist")).exchange(
                request: Data(repeating: 120, count: 256 * 1024 + 1), expectedJobId: 12)
        }
    }

    @Test("closing the child's input cannot raise SIGPIPE or authorise an unsent request")
    func closedInput() async throws {
        let tool = try Fixture("import os\nos.close(0)\nprint('\(Self.completed.trimmingCharacters(in: .newlines))',flush=True)")
        defer { tool.remove() }
        let request = Data(("{\"padding\":\"" + String(repeating: "x", count: 200_000) + "\"}").utf8)
        await #expect(throws: NativeQualityHostError.self) {
            try await NativeQualityHost(executable: tool.url).exchange(request: request, expectedJobId: 12)
        }
    }

    @Test("cancellation also interrupts a full request pipe when the child never reads")
    func blockedInput() async throws {
        let tool = try Fixture("import time\ntime.sleep(30)")
        defer { tool.remove() }
        let request = Data(("{\"padding\":\"" + String(repeating: "x", count: 200_000) + "\"}").utf8)
        let task = Task { try await NativeQualityHost(executable: tool.url).exchange(request: request, expectedJobId: 12) }
        try await Task.sleep(for: .milliseconds(300))
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
    }

    @Test("a terminal event retains ownership until its process actually exits")
    func finalEventDoesNotReleaseProcess() async throws {
        let tool = try Fixture("import sys,time\nsys.stdin.readline()\nprint('\(Self.completed.trimmingCharacters(in: .newlines))',flush=True)\ntime.sleep(.5)")
        defer { tool.remove() }
        let started = Date()
        _ = try await NativeQualityHost(executable: tool.url).exchange(request: Data("{}".utf8), expectedJobId: 12)
        #expect(Date().timeIntervalSince(started) >= 0.5)
    }

    private static let completed = "{\"version\":1,\"type\":\"completed\",\"jobId\":12,\"outcome\":{\"jobId\":12,\"delivered\":true,\"detail\":\"Delivered\",\"capacityBlocked\":false}}\n"

    private struct Fixture {
        let root: URL
        let url: URL
        init(_ source: String) throws {
            root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            url = root.appendingPathComponent("native-host")
            try Data(("#!/usr/bin/python3\n" + source + "\n").utf8).write(to: url)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
        }
        func remove() { try? FileManager.default.removeItem(at: root) }
    }
}
