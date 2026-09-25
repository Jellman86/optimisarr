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
    static let iconFlag = "--render-menu-icon-motion"

    static func renderIcons(into directory: URL) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let working = SidecarStatus.working(jobId: 1, progress: .encoding(encodedSeconds: 1))
        for light in [false, true] {
            NSApplication.shared.appearance = NSAppearance(named: light ? .aqua : .darkAqua)
            let idle = MenuBarIcon.image(for: .connected(workerId: 1, lastCheckIn: .now))
            let theme = light ? "light" : "dark"
            for index in [0, 12, 32, 49, 72, 87] {
                let icon = index == 0 ? idle : MenuBarIcon.image(for: working, spin: Double(index) / 88,
                                                                  reduceMotion: false)
                writeIcon(icon, to: directory.appendingPathComponent("\(theme)-\(String(format: "%02d", index)).png"))
            }
            let from = MenuBarIcon.image(for: working, spin: 32.0 / 88, reduceMotion: false)
            for index in 0..<6 {
                let icon = index == 5 ? idle : MenuBarIcon.blend(
                    from: from, to: idle, progress: CGFloat(index + 1) / 6)
                writeIcon(icon, to: directory.appendingPathComponent("\(theme)-settle-\(String(format: "%02d", index)).png"))
            }
        }
    }

    private static func writeIcon(_ icon: NSImage, to url: URL) {
        guard let tiff = icon.tiffRepresentation,
              let bitmap = NSBitmapImageRep(data: tiff),
              let png = bitmap.representation(using: .png, properties: [:]) else {
            preconditionFailure("Could not render menu-bar icon")
        }
        try! png.write(to: url)
    }

    /// The states the layout has to hold up in. A change that only looks right while idle is not
    /// a change that has been reviewed.
    static func render(into directory: URL) {
        let strip = FilmStrip(frames: sampleFrames())
        let suite = "uk.optimisarr.documentation.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let settings = SidecarSettings(defaults: defaults, physicalMemoryBytes: 16 * 1024 * 1024 * 1024)
        var shutdown = ShutdownCountdown()
        shutdown.arm()
        _ = shutdown.evaluate(at: Date(), ready: true, activeJobs: 0)
        let poses: [(String, SidecarSession)] = [
            ("unpaired", .posed(status: .unpaired, serverAddress: "")),
            ("connected-idle", .posed(
                status: .connected(workerId: 1, lastCheckIn: Date()),
                lastOutcome: .delivered(jobId: 5845, bytes: 394_256_442))),
            ("shutdown-countdown", .posed(
                status: .connected(workerId: 1, lastCheckIn: Date()), shutdown: shutdown)),
            ("receiving", .posed(
                status: .working(jobId: 5846, progress: .fetchingSource(received: 182_000_000, total: 493_040_520)),
                activeJobs: [5846: .fetchingSource(received: 182_000_000, total: 493_040_520)],
                jobTitles: [5846: "Prism Field · Demo clip"],
                transferRates: [5846: 42_100_000],
                gpu: GpuUsage(device: 0.04, memoryInUse: 700_000_000))),
            ("encoding", .posed(
                status: .working(jobId: 5846, progress: .encoding(encodedSeconds: 751)),
                activeJobs: [5846: .encoding(encodedSeconds: 751)],
                jobTitles: [5846: "Prism Field · Demo clip"],
                filmStrips: [5846: strip],
                gpu: GpuUsage(device: 0.31, memoryInUse: 1_253_064_704))),
            ("sending", .posed(
                status: .working(jobId: 5846, progress: .delivering(sent: 300_000_000, total: 394_256_442)),
                activeJobs: [5846: .delivering(sent: 300_000_000, total: 394_256_442)],
                jobTitles: [5846: "Prism Field · Demo clip"],
                transferRates: [5846: 68_400_000],
                filmStrips: [5846: strip],
                gpu: GpuUsage(device: 0.06, memoryInUse: 900_000_000))),
            ("two-jobs", .posed(
                status: .working(jobId: 5846, progress: .encoding(encodedSeconds: 751)),
                activeJobs: [
                    5846: .encoding(encodedSeconds: 751),
                    5847: .measuring,
                ],
                jobTitles: [
                    5846: "Prism Field · Demo clip",
                    5847: "Orbit Study · Demo clip",
                ],
                filmStrips: [5846: strip],
                gpu: GpuUsage(device: 0.62, memoryInUse: 2_100_000_000))),
            ("details", .posed(
                status: .working(jobId: 5846, progress: .encoding(encodedSeconds: 751)),
                activeJobs: [5846: .encoding(encodedSeconds: 751)],
                jobTitles: [5846: "Prism Field · Demo clip"], filmStrips: [5846: strip])),
            ("preferences", .posed(status: .connected(workerId: 1, lastCheckIn: Date()))),
            ("revoked", .posed(status: .revoked)),
            ("unreachable", .posed(
                status: .unreachable(reason: "optimisarr.example.com could not be reached."))),
        ]

        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for (name, session) in poses {
            for light in [false, true] {
            NSApplication.shared.appearance = NSAppearance(named: light ? .aqua : .darkAqua)
            let hosting = NSHostingView(rootView: SidecarMenu(session: session, machineName: "Studio Mac",
                initialPage: name == "preferences" ? "preferences" : "activity",
                detailsExpanded: name == "details", previewSettings: settings).frame(width: 390))
            let window = NSWindow(contentRect: NSRect(x: -10000, y: -10000, width: 390, height: 700),
                                  styleMask: [.borderless], backing: .buffered, defer: false)
            window.contentView = hosting
            let size = hosting.fittingSize
            hosting.setFrameSize(size)
            window.setContentSize(size)
            hosting.layoutSubtreeIfNeeded()
            // Focus the documentation details capture on the disclosure; its scroll viewport
            // otherwise leaves a sliver of the next controls visible above the fixed footer.
            let capture = name == "details"
                ? NSRect(x: 0, y: 0, width: size.width, height: min(size.height, 620))
                : hosting.bounds
            guard let bitmap = hosting.bitmapImageRepForCachingDisplay(in: capture) else { continue }
            hosting.cacheDisplay(in: capture, to: bitmap)
            guard let png = bitmap.representation(using: .png, properties: [:]) else { continue }
            let file = directory.appendingPathComponent("\(light ? "light-" : "")\(name).png")
            try? png.write(to: file)
            print(file.path)
            }
        }
    }

    /// Stand-in frames: a colour ramp, so the strip and its playback are visibly a sequence rather
    /// than one picture repeated.
    private static func sampleFrames() -> [Data] {
        if let path = ProcessInfo.processInfo.environment["OPTIMISARR_RENDER_FRAME"],
           let data = try? Data(contentsOf: URL(fileURLWithPath: path)) { return [data] }
        return (0..<8).compactMap { index in
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
