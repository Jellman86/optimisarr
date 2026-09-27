import Foundation
import Testing
@testable import SidecarCore

private final class StreamFixture: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let mode = request.url!.lastPathComponent
        if mode == "slow" {
            client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: 206,
                httpVersion: "HTTP/1.1", headerFields: [:])!, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(repeating: 7, count: 300 * 1024))
            return
        }
        let status = mode == "whole" ? 200 : mode == "denied" ? 403 : 206
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: status,
            httpVersion: "HTTP/1.1", headerFields: ["Content-Length": "4"])!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data("next".utf8))
        if mode == "broken" {
            client?.urlProtocol(self, didFailWithError: URLError(.networkConnectionLost))
        } else { client?.urlProtocolDidFinishLoading(self) }
    }
    override func stopLoading() {}
}

@Suite("Direct source streaming")
struct StreamingDownloadTests {
    @Test("bytes reach working storage before the response finishes and cancellation restores the prefix")
    func streamingCancellation() async throws {
        let file = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try Data("prior".utf8).write(to: file)
        defer { try? FileManager.default.removeItem(at: file) }
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StreamFixture.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let transfer = Task {
            try await URLSessionTransport(session: session).download(
                URLRequest(url: URL(string: "https://fixture.test/slow")!), to: file)
        }
        for _ in 0..<100 {
            if ((try? FileManager.default.attributesOfItem(atPath: file.path)[.size]) as? NSNumber)?.intValue ?? 0 > 5 { break }
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(((try FileManager.default.attributesOfItem(atPath: file.path)[.size] as? NSNumber)?.intValue ?? 0) > 5)
        transfer.cancel()
        do { _ = try await transfer.value; Issue.record("cancelled download succeeded") } catch {}
        #expect(try Data(contentsOf: file) == Data("prior".utf8))
    }

    @Test("whole responses replace, ranges append, and refused or interrupted responses preserve the prefix",
          arguments: ["whole", "range", "denied", "broken"])
    func writesOnlyAcceptedBytes(mode: String) async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("source")
        try Data("prior".utf8).write(to: file)
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StreamFixture.self]
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        let transport = URLSessionTransport(session: session)
        do {
            _ = try await transport.download(URLRequest(url: URL(string: "https://fixture.test/\(mode)")!), to: file)
            #expect(mode != "broken")
        } catch { #expect(mode == "broken") }
        let expected = mode == "whole" ? "next" : mode == "range" ? "priornext" : "prior"
        #expect(try Data(contentsOf: file) == Data(expected.utf8))
        #expect(try FileManager.default.contentsOfDirectory(atPath: root.path) == ["source"])
    }
}
