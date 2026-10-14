import Foundation
import Testing
@testable import SidecarCore

@Suite("Native assignment routing")
struct NativeQualityAssignmentTests {
    static func wire(kind: Int = 3) -> Data {
        Data("""
        {
          "leaseId":"8b1e2c3d-0000-4000-8000-000000000001", "jobId":12,
          "sourceBytes":4096, "title":"Fixture", "kind":\(kind),
          "imageEncoder":"mjpeg", "audioEncoder":"aac", "renewWithinSeconds":30,
          "arguments":["-i","{{input}}","{{output}}.jpg"], "outputExtension":"jpg",
          "quality":{"measure":false,"model":"vmaf_v0.6.1","frameSubsample":1,
            "clipVmaf":false,"minimumHarmonicMean":93,"minimumMinimum":80},
          "perceptualSearch":{"version":1,"futureField":{"escaped":"line\\nvalue"}},
          "fullVerification":{"version":4,"futureImageEvidence":true}
        }
        """.utf8)
    }

    @Test("image and audio searches use an opaque shared-engine assignment, never legacy verification")
    func nativeRoute() throws {
        for kind in [2, 3] {
            let assignment = try #require(Assignment.decode(Self.wire(kind: kind)))
            #expect(assignment.jobId == 12)
            #expect(assignment.isAudio == (kind == 2))
            #expect(assignment.isImage == (kind == 3))
            #expect(assignment.encoder == (kind == 3 ? "mjpeg" : "aac"))
            #expect(assignment.nativeQuality != nil)
            #expect(assignment.fullVerification == nil)
            #expect(assignment.search == nil)
            #expect(Assignment(json: try JSONSerialization.jsonObject(with: Self.wire(kind: kind)) as! [String: Any]) == nil)
        }
    }

    @Test("helper requests retain unknown fields, integer precision, escapes and duplicate fields for authoritative rejection")
    func originalWireSurvives() throws {
        let raw = String(decoding: Self.wire(), as: UTF8.self)
            .replacingOccurrences(of: "\"sourceBytes\":4096", with: "\"sourceBytes\":9007199254740993")
            .replacingOccurrences(of: "\"futureImageEvidence\":true", with: "\"futureImageEvidence\":true,\"futureImageEvidence\":false")
        let assignment = try #require(Assignment.decode(Data(raw.utf8)))
        let native = try #require(assignment.nativeQuality)
        let request = try native.request(mode: "requirements", tools: tools, scratchRoot: URL(fileURLWithPath: "/tmp/fixture"))
        let text = String(decoding: request, as: UTF8.self)
        #expect(text.contains("9007199254740993"))
        #expect(text.contains("\"futureImageEvidence\":true,\"futureImageEvidence\":false"))
        #expect(text.contains("line\\nvalue"))
        #expect(!request.contains(10) && !request.contains(13))
        #expect(!text.contains("credential"))
        #expect(!text.contains("grantedMemoryBytes"))
    }

    @Test("native envelope identities are typed and bounded before routing")
    func invalidEnvelope() {
        let raw = String(decoding: Self.wire(), as: UTF8.self)
        for invalid in [
            raw.replacingOccurrences(of: "\"jobId\":12", with: "\"jobId\":12,\"JobId\":99"),
            raw.replacingOccurrences(of: "\"jobId\":12", with: "\"jobId\":true"),
            raw.replacingOccurrences(of: "\"jobId\":12", with: "\"jobId\":1.5"),
            raw.replacingOccurrences(of: "\"sourceBytes\":4096", with: "\"sourceBytes\":-1"),
            raw.replacingOccurrences(of: "8b1e2c3d-0000-4000-8000-000000000001", with: "../../settings"),
            raw.replacingOccurrences(of: "\"kind\":3", with: "\"kind\":1"),
            raw.replacingOccurrences(of: "\"kind\":3", with: "\"kind\":true"),
            raw.replacingOccurrences(of: "\"perceptualSearch\":{", with: "\"perceptualSearch\":false,\"ignored\":{")
        ] { #expect(Assignment.decode(Data(invalid.utf8)) == nil) }
        #expect(Assignment.decode(Data(repeating: 32, count: 256 * 1024 + 1)) == nil)
        let deep = raw.replacingOccurrences(of: "true}", with: String(repeating: "[", count: 33) + "true" + String(repeating: "]", count: 33) + "}")
        #expect(Assignment.decode(Data(deep.utf8)) == nil)
    }

    @Test("run requests require private pairing and a positive reserved grant; requirements cannot carry either")
    func requestAuthority() throws {
        let native = try #require(Assignment.decode(Self.wire())?.nativeQuality)
        let pairing = StoredPairing(serverAddress: "http://localhost:8787", credential: "private-fixture-credential-0123456789", workerId: 4)
        #expect(throws: NativeQualityHostError.self) { try native.request(mode: "run", tools: tools, scratchRoot: scratch) }
        #expect(throws: NativeQualityHostError.self) { try native.request(mode: "requirements", tools: tools, scratchRoot: scratch, pairing: pairing) }
        #expect(throws: NativeQualityHostError.self) { try native.request(mode: "run", tools: tools, scratchRoot: scratch, pairing: pairing, grantedMemoryBytes: 0) }
        let data = try native.request(mode: "run", tools: tools, scratchRoot: scratch, pairing: pairing, grantedMemoryBytes: 512 * 1024 * 1024)
        let json = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        #expect((json["pairing"] as? [String: Any])?["credential"] as? String == pairing.credential)
        let grant = try #require((json["grantedMemoryBytes"] as? NSNumber)?.int64Value)
        #expect(grant == 536870912)
    }

    private var scratch: URL { URL(fileURLWithPath: "/tmp/fixture") }
    private var tools: NativeQualityTools {
        NativeQualityTools(host: URL(fileURLWithPath: "/missing/host"), ffmpeg: URL(fileURLWithPath: "/missing/ffmpeg"),
            imageMetric: URL(fileURLWithPath: "/missing/image"), audioMetric: URL(fileURLWithPath: "/missing/audio"), metadataTool: URL(fileURLWithPath: "/missing/exiftool"))
    }
}
