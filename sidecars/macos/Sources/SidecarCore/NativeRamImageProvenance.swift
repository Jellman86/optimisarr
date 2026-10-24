import Foundation

enum NativeRamImageProvenance {
    static func isRAM(_ data: Data, device: String, allocatedBytes: Int64) -> Bool {
        guard !data.isEmpty, data.count <= 1024 * 1024, allocatedBytes > 0, allocatedBytes % 512 == 0,
              let snapshot = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let images = snapshot["images"] as? [[String: Any]], images.count <= 4096 else { return false }
        var matchingSources: [String] = []
        for image in images {
            guard let entities = image["system-entities"] as? [[String: Any]], entities.count <= 64 else { return false }
            if entities.contains(where: { $0["dev-entry"] as? String == device }) {
                guard let path = image["image-path"] as? String else { return false }
                matchingSources.append(path)
            }
        }
        return matchingSources == ["ram://\(allocatedBytes / 512)"]
    }
}
