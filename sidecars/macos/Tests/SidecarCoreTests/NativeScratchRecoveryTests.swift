import Foundation
import Testing
@testable import SidecarCore

@Suite("Native scratch recovery service")
struct NativeScratchRecoveryTests {
    private let root = URL(fileURLWithPath: "/fixture/scratch")
    private let tools = NativeQualityCapabilityTests.tools
    private let cursor = "owner-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.json"

    @Test("a full owner page can carry one final root-level orphan hold without becoming clean")
    func fullPageWithOrphan() async throws {
        let page = NativeScratchRecovery(removed: 64, deferred: 1)
        let helper = ScratchRecoveryHelper(pages: [page])
        let summary = try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root)
        #expect(summary == page && helper.requests.count == 1)
        for invalid in [NativeScratchRecovery(removed: 64, busy: 1), NativeScratchRecovery(removed: 64, deferred: 2),
                        NativeScratchRecovery(removed: 64, deferred: 1, nextOwner: cursor)] {
            #expect(!invalid.isValidPage)
        }
    }

    @Test("scope-bound recovery carries the same physical scope on every page and requires version two")
    func boundScope() async throws {
        let scope = NativeScratchScope(root: "darwin:1:2", control: "darwin:1:3", gate: "darwin:1:4")
        let helper = ScratchRecoveryHelper(pages: [NativeScratchRecovery(nextOwner: cursor), NativeScratchRecovery()])
        _ = try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root, scope: scope)
        for frame in helper.requests {
            let json = try #require(JSONSerialization.jsonObject(with: frame) as? [String: Any])
            #expect(json["version"] as? Int == 2)
            #expect(try JSONDecoder().decode(NativeScratchScope.self, from: JSONSerialization.data(withJSONObject: json["accountingScope"]!)) == scope)
        }
    }

    @Test("bounded pages carry only a working root and an opaque owner cursor")
    func pages() async throws {
        let helper = ScratchRecoveryHelper(pages: [NativeScratchRecovery(removed: 2, busy: 1, nextOwner: cursor), NativeScratchRecovery(deferred: 1)])
        let summary = try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root)
        #expect(summary == NativeScratchRecovery(removed: 2, busy: 1, deferred: 1))
        #expect(helper.requests.count == 2)
        let first = try #require(helper.requests.first)
        let body = try #require(JSONSerialization.jsonObject(with: first) as? [String: Any])
        #expect(Set(body.keys) == Set(["version", "mode", "ffmpegPath", "scratchRoot"]))
        #expect(body["mode"] as? String == "recover" && body["scratchRoot"] as? String == root.path)
        let second = try #require(JSONSerialization.jsonObject(with: helper.requests[1]) as? [String: Any])
        #expect(second["recoveryAfterOwner"] as? String == cursor)
    }

    @Test("default, capability and chosen folders are recovered without duplicate lexical roots")
    func roots() async {
        let helper = ScratchRecoveryHelper(pages: [NativeScratchRecovery()])
        let service = NativeScratchRecoveryService(tools: tools, helper: helper)
        let settings = SettingsSnapshot(workLocation: .folder(URL(fileURLWithPath: "/chosen")))
        await service.recoverWorkingRoots(settings: settings, defaultRoot: root, capabilityRoot: URL(fileURLWithPath: "/capability"))
        #expect(helper.roots == [root.path, "/capability", "/chosen"])
        settings.update(workLocation: .folder(root), memoryBudgetBytes: 0)
        await service.recoverWorkingRoots(settings: settings, defaultRoot: root, capabilityRoot: root)
        #expect(helper.roots == [root.path, "/capability", "/chosen", root.path])
    }

    @Test("timeout waits for cancellation and does not attempt an unsafe filesystem fallback")
    func timeout() async {
        let helper = ScratchRecoveryHelper(hang: true)
        await #expect(throws: NativeQualityHostError.self) {
            try await NativeScratchRecoveryService(tools: tools, helper: helper, timeout: .milliseconds(20)).recover(root: root)
        }
        #expect(helper.stopped && helper.requests.count == 1)
    }

    @Test("caller cancellation ends recovery before another page")
    func cancellation() async throws {
        let helper = ScratchRecoveryHelper(hang: true)
        let service = NativeScratchRecoveryService(tools: tools, helper: helper)
        let task = Task { try await service.recover(root: root) }
        try await waitFor("recovery helper") { !helper.requests.isEmpty }
        task.cancel()
        await #expect(throws: CancellationError.self) { try await task.value }
        #expect(helper.stopped && helper.requests.count == 1)
    }

    @Test("recovery attention and four-page limits remain explicit")
    func pageLimit() async throws {
        let pages = (1...5).map { index in NativeScratchRecovery(removed: 64,
            nextOwner: "owner-" + String(repeating: String(index), count: 32) + ".json", scanLimited: true) }
        let helper = ScratchRecoveryHelper(pages: pages)
        let summary = try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root)
        #expect(summary.removed == 256 && summary.scanLimited && summary.nextOwner == pages[3].nextOwner)
        #expect(helper.requests.count == 4)
    }

    @Test("repeated cursors and malformed summaries stop paging")
    func invalid() async {
        for pages in [[NativeScratchRecovery(removed: -1)], [NativeScratchRecovery(removed: 65)],
                      [NativeScratchRecovery(nextOwner: cursor), NativeScratchRecovery(nextOwner: cursor)]] {
            let helper = ScratchRecoveryHelper(pages: pages)
            await #expect(throws: NativeQualityHostError.self) {
                try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: root)
            }
            #expect(helper.requests.count == pages.count)
        }
    }

    @Test("non-file roots never start the helper")
    func rootValidation() async throws {
        let helper = ScratchRecoveryHelper(pages: [NativeScratchRecovery()])
        await #expect(throws: NativeQualityHostError.self) {
            try await NativeScratchRecoveryService(tools: tools, helper: helper).recover(root: URL(string: "https://example.com/scratch")!)
        }
        #expect(helper.requests.isEmpty)
    }
}

final class ScratchRecoveryHelper: NativeQualityExchanging, @unchecked Sendable {
    private let gate = NSLock()
    private var pages: [NativeScratchRecovery]
    private var frames: [Data] = []
    private var ended = false
    private let hang: Bool
    init(pages: [NativeScratchRecovery] = [], hang: Bool = false) { self.pages = pages; self.hang = hang }
    var requests: [Data] { gate.withLock { frames } }
    var stopped: Bool { gate.withLock { ended } }
    var roots: [String] { requests.compactMap { (try? JSONSerialization.jsonObject(with: $0) as? [String: Any])?["scratchRoot"] as? String } }
    func exchange(request: Data, expectedJobId: Int?, report: @escaping @Sendable (NativeQualityEvent) -> Void) async throws -> NativeQualityReply {
        #expect(expectedJobId == nil)
        gate.withLock { frames.append(request) }
        defer { gate.withLock { ended = true } }
        if hang { try await Task.sleep(for: .seconds(60)); throw NativeQualityHostError() }
        let page = gate.withLock { pages.count > 1 ? pages.removeFirst() : pages.first ?? NativeScratchRecovery() }
        let json = try JSONSerialization.jsonObject(with: request) as! [String: Any]
        return NativeQualityReply(type: "recovered", jobId: nil, delivered: false, capacityBlocked: false, capabilities: nil, recovery: page,
            version: json["version"] as! Int)
    }
}
