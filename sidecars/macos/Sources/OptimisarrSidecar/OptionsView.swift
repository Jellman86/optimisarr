import AppKit
import SidecarCore
import SwiftUI

/// Settings that do not belong in the menu.
///
/// The menu is for watching a job; this is for deciding how the Mac does the work. Keeping them
/// apart stops the menu growing into a control panel that has to be scrolled past to see whether
/// anything is happening.
struct OptionsView: View {
    @ObservedObject var settings: SidecarSettings

    var embedded = false

    private func bytes(_ value: Int64) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowedUnits = [.useGB, .useMB]
        return formatter.string(fromByteCount: value)
    }

    private var budgetRange: ClosedRange<Double> {
        let physical = Double(settings.physicalMemoryBytes)
        return (physical * WorkLocationPolicy.minimumBudgetFraction)...(physical * WorkLocationPolicy.maximumBudgetFraction)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Where work happens").font(.headline)
                Text("A job downloads its source here and writes its candidate here before sending it back. Both together are roughly one and a half times the size of the original.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Picker("", selection: Binding(
                get: { mode },
                set: { apply($0) })) {
                Text("The app's own folder").tag(Mode.applicationSupport)
                Text("A folder I choose").tag(Mode.folder)
                Text("Memory").tag(Mode.memory)
            }
            .pickerStyle(.radioGroup)
            .labelsHidden()

            switch settings.workLocation {
            case .applicationSupport:
                note("Inside Application Support, on the startup disk.")

            case let .folder(url):
                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: 8) {
                        Text(url.path)
                            .font(.caption.monospaced())
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Spacer(minLength: 8)
                        Button("Change…") { chooseFolder() }
                            .controlSize(.small)
                    }
                    note("Useful for keeping multi-gigabyte transfers off the startup disk, or for using a larger external one. If the drive is not mounted when the app starts, it falls back to the app's own folder.")
                }

            case .memory:
                VStack(alignment: .leading, spacing: 8) {
                    HStack {
                        Text("Memory budget").font(.caption)
                        Spacer(minLength: 8)
                        Text(bytes(settings.memoryBudgetBytes))
                            .font(.caption.monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                    Slider(
                        value: Binding(
                            get: { Double(settings.memoryBudgetBytes) },
                            set: { settings.setMemoryBudget(Int64($0)) }),
                        in: budgetRange)
                    HStack {
                        Text(bytes(Int64(budgetRange.lowerBound))).font(.caption2).foregroundStyle(.tertiary)
                        Spacer()
                        Text("of \(bytes(settings.physicalMemoryBytes)) installed")
                            .font(.caption2).foregroundStyle(.tertiary)
                        Spacer()
                        Text(bytes(Int64(budgetRange.upperBound))).font(.caption2).foregroundStyle(.tertiary)
                    }
                    // Said plainly, because the obvious expectation is that memory is faster.
                    note("A job needing more working space than the budget runs on disk instead — it is never refused over this setting. A RAM disk holds real memory for as long as the job runs, and most films will not fit in any sensible budget. It is also rarely faster: a download is limited by the network and an encode by the encoder, not by an Apple SSD.")
                }
            }

            Spacer(minLength: 0)
        }
        .padding(embedded ? 0 : 18)
        .frame(width: embedded ? nil : 420, alignment: .topLeading)
    }

    private func note(_ text: String) -> some View {
        Text(text)
            .font(.caption)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    private enum Mode: Hashable { case applicationSupport, folder, memory }

    private var mode: Mode {
        switch settings.workLocation {
        case .applicationSupport: return .applicationSupport
        case .folder: return .folder
        case .memory: return .memory
        }
    }

    private func apply(_ mode: Mode) {
        switch mode {
        case .applicationSupport:
            settings.setWorkLocation(.applicationSupport)
        case .memory:
            settings.setWorkLocation(.memory)
        case .folder:
            // Nothing is stored until a folder is actually picked, so cancelling the panel leaves
            // the previous choice in place rather than a "folder" mode with no folder.
            chooseFolder()
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        panel.prompt = "Use this folder"
        panel.message = "Choose where jobs download and encode."
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        settings.setWorkLocation(.folder(url))
    }
}
