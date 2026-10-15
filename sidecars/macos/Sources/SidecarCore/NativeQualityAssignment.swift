import Foundation

public struct NativeQualityTools: Sendable {
    public let host: URL
    public let ffmpeg: URL
    public let imageMetric: URL?
    public let audioMetric: URL?
    public let metadataTool: URL?

    public init(host: URL, ffmpeg: URL, imageMetric: URL? = nil, audioMetric: URL? = nil, metadataTool: URL? = nil) {
        self.host = host; self.ffmpeg = ffmpeg; self.imageMetric = imageMetric
        self.audioMetric = audioMetric; self.metadataTool = metadataTool
    }

    public static func bundled() -> Self? {
        guard let root = Bundle.main.resourceURL else { return nil }
        return bundled(in: root)
    }

    static func bundled(in root: URL) -> Self? {
        func executable(_ path: String) -> URL? {
            let url = root.appendingPathComponent(path)
            return FileManager.default.isExecutableFile(atPath: url.path) ? url : nil
        }
        guard let host = executable("native-quality/Optimisarr.Sidecar.Native"), let ffmpeg = executable("ffmpeg") else { return nil }
        return Self(host: host, ffmpeg: ffmpeg, imageMetric: executable("optimisarr-image-quality"),
                    audioMetric: executable("optimisarr-audio-quality"), metadataTool: executable("metadata/exiftool"))
    }
}

/// UI envelope only. The complete frozen contract remains opaque until the shared engine validates it.
public struct NativeQualityAssignment: Sendable, Equatable {
    let wire: Data
    let leaseId: String
    let jobId: Int
    let sourceBytes: Int64
    let title: String
    let kind: String
    let encoder: String
    let renewWithinSeconds: Int
    let arguments: [String]
    let outputExtension: String
    let quality: QualityRequirement

    private struct Envelope: Decodable {
        let leaseId: UUID
        let jobId: Int
        let sourceBytes: Int64
        let title: String?
        let kind: Kind
        let imageEncoder: String?
        let audioEncoder: String?
        let renewWithinSeconds: Int
        let arguments: [String]
        let outputExtension: String
    }
    private enum Kind: String, Decodable {
        case audio = "Audio", image = "Image"
        init(from decoder: Decoder) throws {
            let value = try decoder.singleValueContainer()
            if let number = try? value.decode(Int.self), [2, 3].contains(number) {
                self = number == 2 ? .audio : .image
            } else if let text = try? value.decode(String.self), let kind = Self(rawValue: text) { self = kind }
            else { throw NativeQualityHostError() }
        }
    }

    init?(data: Data) {
        guard let wire = try? NativeWireJSON.compact(data),
              let envelope = try? JSONDecoder().decode(Envelope.self, from: wire),
              envelope.leaseId.uuidString != "00000000-0000-0000-0000-000000000000",
              envelope.jobId > 0, envelope.sourceBytes > 0, envelope.renewWithinSeconds > 0,
              let object = try? JSONSerialization.jsonObject(with: wire) as? [String: Any],
              object["perceptualSearch"] is [String: Any],
              let rawQuality = object["quality"] as? [String: Any], let quality = QualityRequirement(json: rawQuality),
              let encoder = envelope.kind == .image ? envelope.imageEncoder : envelope.audioEncoder,
              !encoder.isEmpty else { return nil }
        self.wire = wire; leaseId = envelope.leaseId.uuidString; jobId = envelope.jobId
        sourceBytes = envelope.sourceBytes; title = envelope.title ?? ""; kind = envelope.kind.rawValue
        self.encoder = encoder; renewWithinSeconds = envelope.renewWithinSeconds
        arguments = envelope.arguments; outputExtension = envelope.outputExtension; self.quality = quality
    }

    public func request(mode: String, tools: NativeQualityTools, scratchRoot: URL,
                        pairing: StoredPairing? = nil, grantedMemoryBytes: Int64? = nil,
                        version: Int = 1, owner: NativeAccountedOwner? = nil, accountingScope: NativeScratchScope? = nil) throws -> Data {
        guard ["requirements", "run"].contains(mode),
              [1, 2].contains(version),
              version == 1 ? owner == nil && accountingScope == nil
                : mode == "requirements" ? owner == nil && accountingScope == nil
                    : owner?.isValid == true && owner?.jobId == jobId && owner?.leaseId.uuidString == leaseId && accountingScope?.isValid == true,
              mode == "requirements" ? pairing == nil && grantedMemoryBytes == nil : pairing != nil && grantedMemoryBytes ?? 0 > 0,
              [tools.host, tools.ffmpeg, tools.imageMetric, tools.audioMetric, tools.metadataTool, scratchRoot]
                .compactMap({ $0 }).allSatisfy({ $0.isFileURL && $0.path.hasPrefix("/") && $0.path.utf8.count <= 4096 })
        else { throw NativeQualityHostError() }
        var body: [String: Any] = ["version": version, "mode": mode, "ffmpegPath": tools.ffmpeg.path, "scratchRoot": scratchRoot.path]
        if let owner {
            body["accountedOwner"] = ["owner": owner.owner.uuidString, "jobId": owner.jobId, "leaseId": owner.leaseId.uuidString]
        }
        if let accountingScope {
            body["accountingScope"] = ["root": accountingScope.root, "control": accountingScope.control, "gate": accountingScope.gate]
        }
        if let path = tools.imageMetric { body["imageMetric"] = path.path }
        if let path = tools.audioMetric { body["audioMetric"] = path.path }
        if let path = tools.metadataTool { body["metadataTool"] = path.path }
        if let pairing {
            body["pairing"] = ["serverAddress": pairing.serverAddress, "credential": pairing.credential, "workerId": pairing.workerId]
        }
        if let grantedMemoryBytes { body["grantedMemoryBytes"] = grantedMemoryBytes }
        var data = try JSONSerialization.data(withJSONObject: body)
        data.removeLast()
        data.append(Data(",\"assignment\":".utf8)); data.append(wire); data.append(125)
        guard data.count <= NativeWireJSON.maximumBytes else { throw NativeQualityHostError() }
        return data
    }
}

/// Remove framing whitespace without rounding numbers or discarding duplicate/unknown properties.
/// The shared parser, not a reserialized dictionary, must see the original contract.
enum NativeWireJSON {
    static let maximumBytes = 256 * 1024
    static func hasUniqueProperties(_ wire: Data) -> Bool {
        struct Container { let object: Bool; var expectingKey = true; var keys: Set<String> = [] }
        var containers: [Container] = []
        var quoted = false, escaped = false
        var keyStart: Data.Index?
        for index in wire.indices {
            let byte = wire[index]
            if quoted {
                if escaped { escaped = false }
                else if byte == 92 { escaped = true }
                else if byte == 34 {
                    quoted = false
                    if let start = keyStart {
                        guard let key = try? JSONDecoder().decode(String.self, from: wire[start...index]),
                              containers[containers.count - 1].keys.insert(key.lowercased()).inserted else { return false }
                        keyStart = nil
                    }
                }
            } else if byte == 34 {
                quoted = true
                if containers.last?.object == true && containers.last?.expectingKey == true {
                    keyStart = index; containers[containers.count - 1].expectingKey = false
                }
            } else if byte == 123 || byte == 91 { containers.append(Container(object: byte == 123)) }
            else if byte == 125 || byte == 93 {
                guard !containers.isEmpty else { return false }
                containers.removeLast()
            } else if byte == 44, !containers.isEmpty { containers[containers.count - 1].expectingKey = true }
        }
        return !quoted && containers.isEmpty
    }
    static func rootKeys(_ wire: Data) -> [String] {
        var keys: [String] = []
        var depth = 0, quoted = false, escaped = false, expectingKey = false
        var start: Data.Index?
        for index in wire.indices {
            let byte = wire[index]
            if quoted {
                if escaped { escaped = false }
                else if byte == 92 { escaped = true }
                else if byte == 34 {
                    quoted = false
                    if let start, let key = try? JSONDecoder().decode(String.self, from: wire[start...index]) { keys.append(key) }
                    start = nil
                }
                continue
            }
            if byte == 34 {
                quoted = true
                if depth == 1 && expectingKey { start = index; expectingKey = false }
            } else if byte == 123 || byte == 91 {
                depth += 1
                if depth == 1 { expectingKey = byte == 123 }
            } else if byte == 125 || byte == 93 { depth -= 1 }
            else if byte == 44 && depth == 1 { expectingKey = true }
        }
        return keys
    }

    static func uniqueLeaseId(_ data: Data) -> UUID? {
        struct Identity: Decodable { let leaseId: UUID }
        guard let wire = try? compact(data), rootKeys(wire).filter({ $0.lowercased() == "leaseid" }).count == 1,
              let identity = try? JSONDecoder().decode(Identity.self, from: wire),
              identity.leaseId.uuidString != "00000000-0000-0000-0000-000000000000" else { return nil }
        return identity.leaseId
    }

    static func compact(_ input: Data) throws -> Data {
        guard !input.isEmpty, input.count <= maximumBytes else { throw NativeQualityHostError() }
        var output = Data(); output.reserveCapacity(input.count)
        var quoted = false, escaped = false, depth = 0
        for byte in input {
            if quoted {
                output.append(byte)
                if escaped { escaped = false }
                else if byte == 92 { escaped = true }
                else if byte == 34 { quoted = false }
            } else {
                if [9, 10, 13, 32].contains(byte) { continue }
                if byte == 34 { quoted = true }
                if byte == 123 || byte == 91 { depth += 1 }
                if byte == 125 || byte == 93 { depth -= 1 }
                guard depth >= 0, depth <= 28 else { throw NativeQualityHostError() }
                output.append(byte)
            }
        }
        guard !quoted, depth == 0, let value = try? JSONSerialization.jsonObject(with: output),
              value is [String: Any] else { throw NativeQualityHostError() }
        return output
    }
}
