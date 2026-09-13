import AppKit
import SidecarCore
import SwiftUI

/// Renders the menu in each state worth looking at, to PNGs, and exits.
///
/// The menu is the whole product, and most of its states — mid-transfer, encoding with a strip of
/// frames, revoked — need a paired server and a running job to reach. Judging a layout change by
/// waiting for one is slow enough that it does not happen, so the states are posed and rendered
/// instead:
///
///     OptimisarrSidecar --render-menu /tmp/menu
@MainActor
enum MenuRenderer {
    static let flag = "--render-menu"

    /// The states the layout has to hold up in. A change that only looks right while idle is not
    /// a change that has been reviewed.
    static func render(into directory: URL) {
        let strip = FilmStrip(frames: sampleFrames())
        let poses: [(String, SidecarSession)] = [
            ("unpaired", .posed(status: .unpaired, serverAddress: "")),
            ("connected-idle", .posed(
                status: .connected(workerId: 1, lastCheckIn: Date()),
                lastOutcome: .delivered(jobId: 5845, bytes: 394_256_442))),
            ("receiving", .posed(
                status: .working(jobId: 5846, progress: .fetchingSource(received: 182_000_000, total: 493_040_520)),
                activeJobs: [5846: .fetchingSource(received: 182_000_000, total: 493_040_520)],
                gpu: GpuUsage(device: 0.04, memoryInUse: 700_000_000))),
            ("encoding", .posed(
                status: .working(jobId: 5846, progress: .encoding(encodedSeconds: 751)),
                activeJobs: [5846: .encoding(encodedSeconds: 751)],
                filmStrips: [5846: strip],
                gpu: GpuUsage(device: 0.31, memoryInUse: 1_253_064_704))),
            ("sending", .posed(
                status: .working(jobId: 5846, progress: .delivering(sent: 300_000_000, total: 394_256_442)),
                activeJobs: [5846: .delivering(sent: 300_000_000, total: 394_256_442)],
                filmStrips: [5846: strip],
                gpu: GpuUsage(device: 0.06, memoryInUse: 900_000_000))),
            ("two-jobs", .posed(
                status: .working(jobId: 5846, progress: .encoding(encodedSeconds: 751)),
                activeJobs: [
                    5846: .encoding(encodedSeconds: 751),
                    5847: .measuring,
                ],
                filmStrips: [5846: strip],
                gpu: GpuUsage(device: 0.62, memoryInUse: 2_100_000_000))),
            ("revoked", .posed(status: .revoked)),
            ("unreachable", .posed(
                status: .unreachable(reason: "optimisarr.pownet.uk could not be reached."))),
        ]

        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for (name, session) in poses {
            let renderer = ImageRenderer(content: SidecarMenu(session: session).frame(width: 340))
            // Retina, so the PNG is worth looking at closely rather than a blurry approximation.
            renderer.scale = 2
            guard let image = renderer.nsImage,
                  let tiff = image.tiffRepresentation,
                  let bitmap = NSBitmapImageRep(data: tiff),
                  let png = bitmap.representation(using: .png, properties: [:])
            else {
                FileHandle.standardError.write(Data("could not render \(name)\n".utf8))
                continue
            }
            let file = directory.appendingPathComponent("\(name).png")
            try? png.write(to: file)
            print(file.path)
        }
    }

    /// Stand-in frames: a colour ramp, so the strip and its playback are visibly a sequence rather
    /// than one picture repeated.
    private static func sampleFrames() -> [Data] {
        (0..<8).compactMap { index in
            let size = NSSize(width: 320, height: 180)
            let image = NSImage(size: size)
            image.lockFocus()
            NSColor(hue: CGFloat(index) / 8, saturation: 0.55, brightness: 0.7, alpha: 1).setFill()
            NSRect(origin: .zero, size: size).fill()
            image.unlockFocus()
            guard let tiff = image.tiffRepresentation,
                  let bitmap = NSBitmapImageRep(data: tiff)
            else { return nil }
            return bitmap.representation(using: .jpeg, properties: [:])
        }
    }
}
