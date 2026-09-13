import SidecarCore
import SwiftUI

/// The whole interface: what state we are in, what this Mac is doing, and the actions that state
/// allows.
///
/// Small on purpose. This app pairs, works, and reports honestly; a menu offering more than that
/// would imply capabilities it does not have. What it does show, it shows properly — a running job
/// gets the frames going through the encoder and a real progress bar, because "Encoding" on its
/// own cannot tell a working Mac from a stuck one.
struct SidecarMenu: View {
    @ObservedObject var session: SidecarSession

    @State private var serverAddress = ""
    @State private var pin = ""
    @State private var startAtLogin = LoginItem.isEnabled
    @State private var loginItemError: String?
    @State private var isPairing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            header

            switch session.status {
            case .unpaired, .pairingFailed:
                pairingForm
            default:
                pairedDetail
            }

            Divider()
            footer
        }
        .padding(14)
        .frame(width: 340)
        .onAppear {
            session.restore()
            if serverAddress.isEmpty { serverAddress = session.serverAddress }
            // Frame grabs and GPU readings cost something, so they run only while this is on
            // screen. A menu-bar window is closed far more of the time than it is open.
            session.setPreviewsWanted(true)
        }
        .onDisappear { session.setPreviewsWanted(false) }
    }

    // MARK: - Header

    private var header: some View {
        HStack(alignment: .firstTextBaseline, spacing: 7) {
            Circle()
                .fill(session.status.tint)
                .frame(width: 7, height: 7)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 1) {
                Text("Optimisarr Sidecar")
                    .font(.system(size: 13, weight: .semibold))
                Text(session.status.summary)
                    .font(.caption)
                    .foregroundStyle(.secondary)

                // The reason matters more than the state name — "Pairing failed" is not
                // actionable, but "that code has expired" is.
                if let detail = session.status.detail {
                    Text(detail)
                        .font(.caption)
                        .foregroundStyle(session.status.tint == .red ? .red : .secondary)
                        .fixedSize(horizontal: false, vertical: true)
                        .padding(.top, 2)
                }
            }
            Spacer(minLength: 0)
        }
    }

    // MARK: - Pairing

    private var pairingForm: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Enter the address of your Optimisarr server and the pairing code it shows under Settings → Workers.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            TextField("optimisarr.local:8787", text: $serverAddress)
                .textFieldStyle(.roundedBorder)
                .disableAutocorrection(true)

            TextField("Pairing code", text: $pin)
                .textFieldStyle(.roundedBorder)
                .disableAutocorrection(true)

            Button(isPairing ? "Pairing…" : "Pair") {
                Task {
                    isPairing = true
                    await session.pair(serverAddress: serverAddress, pin: pin)
                    isPairing = false
                    // Only cleared on success. Leaving a rejected code in place lets someone fix
                    // a typo instead of retyping the whole thing.
                    if case .connected = session.status { pin = "" }
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(isPairing || serverAddress.isEmpty || pin.isEmpty)
        }
    }

    // MARK: - Paired

    private var pairedDetail: some View {
        VStack(alignment: .leading, spacing: 10) {
            if session.activeJobs.isEmpty {
                idleCard
            } else {
                ForEach(session.activeJobs.keys.sorted(), id: \.self) { jobId in
                    if let progress = session.activeJobs[jobId] {
                        jobCard(jobId: jobId, progress: progress)
                    }
                }
                if let gpu = session.gpu {
                    gpuCard(gpu)
                }
            }

            connectionCard
            concurrencyPicker

            if let outcome = session.lastOutcome {
                Text(outcome.label)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            // Said plainly: a delivered candidate is a proposal. Optimisarr verifies it against
            // the original before anything is replaced, and this machine never sees that decision.
            Label(
                "Encodes go to Optimisarr for verification. Nothing is replaced from here.",
                systemImage: "checkmark.shield")
                .font(.caption2)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var idleCard: some View {
        Card {
            HStack(spacing: 8) {
                Image(systemName: "moon.zzz")
                    .foregroundStyle(.secondary)
                Text("Waiting for work")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Spacer(minLength: 0)
            }
        }
    }

    /// One running job: the frames going through it, what stage it is at, and how far along.
    private func jobCard(jobId: Int, progress: JobProgress) -> some View {
        Card {
            VStack(alignment: .leading, spacing: 7) {
                if let strip = session.filmStrips[jobId], !strip.isEmpty {
                    FilmStripView(strip: strip)
                }

                HStack(spacing: 6) {
                    Image(systemName: progress.symbol)
                        .font(.caption)
                        .foregroundStyle(progress.tint)
                        .frame(width: 13)
                    Text("Job #\(jobId)")
                        .font(.system(size: 11, weight: .medium))
                    Spacer(minLength: 4)
                    Text(progress.label)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                // A bar only where there is a real fraction to draw. An encode reports seconds of
                // output, not a fraction, because only the server knows the source's duration.
                Meter(value: progress.fraction, tint: progress.tint)
            }
        }
    }

    /// The GPU figure, with the caveat stated rather than left for someone to discover.
    private func gpuCard(_ gpu: GpuUsage) -> some View {
        Card {
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 6) {
                    Image(systemName: "cpu")
                        .font(.caption)
                        .foregroundStyle(.purple)
                        .frame(width: 13)
                    Text("GPU").font(.system(size: 11, weight: .medium))
                    Spacer(minLength: 4)
                    Text("\(Int((gpu.device * 100).rounded()))%")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                Meter(value: gpu.device, tint: .purple)
                Text("VideoToolbox encodes on the media engine, which macOS does not report. This covers the GPU only.")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var connectionCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 3) {
                detailRow("Server", session.serverAddress)
                if case let .connected(workerId, lastCheckIn) = session.status {
                    detailRow("Worker", "#\(workerId)")
                    detailRow("Last check-in", lastCheckIn.formatted(date: .omitted, time: .standard))
                }
            }
        }
    }

    private func detailRow(_ name: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(name)
                .font(.caption)
                .foregroundStyle(.secondary)
            Spacer(minLength: 8)
            Text(value)
                .font(.caption.monospacedDigit())
                .lineLimit(1)
                .truncationMode(.middle)
        }
    }

    private var concurrencyPicker: some View {
        // Takes effect on the next check-in; a running job is never stopped to fit.
        HStack(spacing: 8) {
            Text("Jobs at once")
                .font(.caption)
                .foregroundStyle(.secondary)
            Picker("", selection: Binding(
                get: { session.jobConcurrency },
                set: { session.setJobConcurrency($0) })) {
                ForEach(Array(SidecarSession.concurrencyRange), id: \.self) { count in
                    Text("\(count)").tag(count)
                }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
        }
    }

    // MARK: - Footer

    private var footer: some View {
        VStack(alignment: .leading, spacing: 6) {
            Toggle("Start at login", isOn: Binding(
                get: { startAtLogin },
                set: { wanted in
                    do {
                        try LoginItem.setEnabled(wanted)
                        startAtLogin = LoginItem.isEnabled
                        loginItemError = nil
                    } catch {
                        startAtLogin = LoginItem.isEnabled
                        loginItemError = "Could not change the login item: \(error.localizedDescription)"
                    }
                }))
                .font(.caption)
                .toggleStyle(.checkbox)

            if let loginItemError {
                Text(loginItemError)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 8) {
                if case .unpaired = session.status {} else {
                    Button("Forget this pairing") {
                        session.unpair()
                        pin = ""
                    }
                    .controlSize(.small)
                }
                Spacer(minLength: 0)
                Button("Quit") { NSApplication.shared.terminate(nil) }
                    .controlSize(.small)
                    .keyboardShortcut("q")
            }
        }
    }
}

/// A grouped panel. Menu-bar windows have no chrome of their own, so without something to sit in,
/// every row reads at the same weight and the eye has nowhere to land.
private struct Card<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: 7)
                    .fill(Color.primary.opacity(0.05)))
    }
}

private extension JobProgress {
    var label: String {
        switch self {
        case let .fetchingSource(received, total):
            return Self.transferred(received, total)
        case let .encoding(seconds):
            let whole = Int(seconds)
            return String(format: "%d:%02d:%02d encoded", whole / 3600, whole % 3600 / 60, whole % 60)
        case .measuring:
            return "Measuring quality"
        case let .delivering(sent, total):
            return Self.transferred(sent, total)
        }
    }

    var symbol: String {
        switch self {
        case .fetchingSource: return "arrow.down.circle"
        case .encoding: return "wand.and.stars"
        case .measuring: return "waveform.badge.magnifyingglass"
        case .delivering: return "arrow.up.circle"
        }
    }

    var tint: Color {
        switch self {
        case .fetchingSource: return .teal
        case .encoding: return .blue
        case .measuring: return .orange
        case .delivering: return .green
        }
    }

    /// How far through the transfer, or nil for a stage with no honest fraction to show.
    var fraction: Double? {
        switch self {
        case let .fetchingSource(done, total), let .delivering(done, total):
            guard total > 0 else { return nil }
            return min(max(Double(done) / Double(total), 0), 1)
        case .encoding, .measuring:
            return nil
        }
    }

    /// "182 MB of 1.2 GB". The total is dropped while it is still unknown rather than shown as
    /// zero, which would read as a finished transfer of nothing.
    static func transferred(_ done: Int64, _ total: Int64) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        let doneText = formatter.string(fromByteCount: done)
        guard total > 0 else { return doneText }
        return "\(doneText) of \(formatter.string(fromByteCount: total))"
    }
}

private extension JobOutcome {
    var label: String {
        switch self {
        case let .delivered(jobId, bytes):
            let size = ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
            return "Last job #\(jobId): delivered \(size) for verification."
        case let .released(jobId, reason):
            return "Last job #\(jobId): handed back — \(reason)"
        case let .leaseLost(jobId, reason):
            return "Last job #\(jobId): lease lost — \(reason)"
        }
    }
}

private extension SidecarStatus {
    /// One colour for the whole menu's sense of health, used by the dot and by error text.
    var tint: Color {
        switch self {
        case .working: return .blue
        case .connected: return .green
        case .unreachable, .disabledOnServer: return .orange
        case .pairingFailed, .revoked: return .red
        default: return .secondary
        }
    }

    /// The explanation behind the state, where there is one worth reading.
    var detail: String? {
        switch self {
        case let .pairingFailed(reason): return reason
        case let .unreachable(reason): return reason
        case let .disabledOnServer(reason): return reason
        case .revoked: return "This worker was revoked in Optimisarr. Pair again to reconnect."
        default: return nil
        }
    }
}
