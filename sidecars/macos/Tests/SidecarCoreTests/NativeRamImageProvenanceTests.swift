import Foundation
import Testing
@testable import SidecarCore

@Suite("Native RAM image provenance")
struct NativeRamImageProvenanceTests {
    @Test("only one matching whole device with the exact RAM allocation source is accepted")
    func ramSource() throws {
        let device = "/dev/disk9", bytes: Int64 = 512 * 100_000
        let good: [String: Any] = ["image-path": "ram://100000", "system-entities": [["dev-entry": device]]]
        func snapshot(_ images: [[String: Any]]) throws -> Data {
            try PropertyListSerialization.data(fromPropertyList: ["images": images], format: .xml, options: 0)
        }
        #expect(NativeRamImageProvenance.isRAM(try snapshot([good]), device: device, allocatedBytes: bytes))
        for image in [
            ["image-path": "/private/personal.dmg", "system-entities": [["dev-entry": device]]],
            ["image-path": "ram://99999", "system-entities": [["dev-entry": device]]],
            ["image-path": "ram://100000", "system-entities": [["dev-entry": "/dev/disk8"]]],
        ] as [[String: Any]] {
            #expect(!NativeRamImageProvenance.isRAM(try snapshot([image]), device: device, allocatedBytes: bytes))
        }
        #expect(!NativeRamImageProvenance.isRAM(try snapshot([good, good]), device: device, allocatedBytes: bytes))
        #expect(!NativeRamImageProvenance.isRAM(Data("invalid".utf8), device: device, allocatedBytes: bytes))
        #expect(!NativeRamImageProvenance.isRAM(try snapshot([good]), device: device, allocatedBytes: bytes + 1))
    }
}
