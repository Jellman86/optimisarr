import Darwin
import Foundation
import Security
import Testing
@testable import SidecarCore

@Suite("Serialized legacy Keychain interaction")
struct LegacyKeychainInteractionTests {
    @Test("failure to read or suppress interaction never executes the Keychain operation")
    func refusedSuppression() {
        for failsRead in [true, false] {
            let calls = LegacyInteractionFixture()
            let interaction = LegacyKeychainInteraction(readInteraction: { value in
                if failsRead { return errSecAuthFailed }
                return calls.read(&value)
            }, setInteraction: { _ in errSecAuthFailed })
            #expect(throws: KeychainError.self) { try interaction.perform { calls.enterBody() } }
            #expect(calls.bodies == 0)
        }
    }

    @Test("the prior interaction setting is restored after success and thrown errors")
    func restoration() throws {
        for allowed in [false, true] {
            let calls = LegacyInteractionFixture(allowed: allowed)
            let interaction = LegacyKeychainInteraction(readInteraction: calls.read, setInteraction: calls.set)
            try interaction.perform { #expect(!calls.allowed); calls.enterBody() }
            #expect(calls.allowed == allowed)
            #expect(throws: NativeWorkJournalError.self) {
                try interaction.perform { #expect(!calls.allowed); throw NativeWorkJournalError.unavailable }
            }
            #expect(calls.allowed == allowed)
        }
    }

    @Test("unconfirmed restoration cannot report a successful operation")
    func restorationFailure() {
        let calls = LegacyInteractionFixture()
        let interaction = LegacyKeychainInteraction(readInteraction: calls.read, setInteraction: { allowed in
            if allowed { return errSecAuthFailed }
            return calls.set(allowed)
        })
        #expect(throws: KeychainError.self) { try interaction.perform { calls.enterBody() } }
        #expect(calls.bodies == 1)
    }

    @Test("overlapping operations cannot restore prompts while another operation is running")
    func serialization() {
        let calls = LegacyInteractionFixture(), entered = DispatchSemaphore(value: 0), release = DispatchSemaphore(value: 0)
        let interaction = LegacyKeychainInteraction(readInteraction: calls.read, setInteraction: calls.set)
        let finished = DispatchGroup()
        finished.enter()
        Thread.detachNewThread {
            defer { finished.leave() }
            do { try interaction.perform { calls.enterBody(); entered.signal(); release.wait() } }
            catch { calls.fail() }
        }
        defer { release.signal() }
        #expect(entered.wait(timeout: .now() + 2) == .success)
        finished.enter()
        Thread.detachNewThread {
            defer { finished.leave() }
            do { try interaction.perform { calls.enterBody(); if calls.allowed { calls.fail() } } }
            catch { calls.fail() }
        }
        usleep(30_000)
        #expect(calls.bodies == 1)
        #expect(!calls.allowed)
        release.signal()
        #expect(finished.wait(timeout: .now() + 2) == .success)
        #expect(calls.bodies == 2 && !calls.failed && calls.allowed)
    }
}

private final class LegacyInteractionFixture: @unchecked Sendable {
    private let gate = NSLock()
    private var interaction: Bool
    private var count = 0
    private var error = false
    init(allowed: Bool = true) { interaction = allowed }
    var allowed: Bool { gate.withLock { interaction } }
    var bodies: Int { gate.withLock { count } }
    var failed: Bool { gate.withLock { error } }
    func enterBody() { gate.withLock { count += 1 } }
    func fail() { gate.withLock { error = true } }
    func read(_ value: inout DarwinBoolean) -> OSStatus { gate.withLock { value = DarwinBoolean(interaction); return errSecSuccess } }
    func set(_ value: Bool) -> OSStatus { gate.withLock { interaction = value; return errSecSuccess } }
}
