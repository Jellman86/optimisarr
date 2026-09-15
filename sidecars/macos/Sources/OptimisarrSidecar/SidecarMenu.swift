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
    /// Opening a window is the app delegate's job, not this view's; nil in previews and renders.
    var onShowOptions: (() -> Void)?

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

            // A menu-bar panel does not always shrink back when its content does. After a job
            // finishes, the window keeps the height the film strip and the job card needed, and
            // the content settles at the bottom of it — so the panel appears to hang away from
            // the menu bar, with a gap above it exactly the size of the frames that are no longer
            // there. This takes up whatever height the window has left, at the bottom, where it
            // cannot be mistaken for part of the panel.
            Spacer(minLength: 0)
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
            }
            // Shown whether or not a job is running. An idle Mac still has a figure worth seeing —
            // it is what the check-in reports, and it answers "why has this taken nothing on?" —
            // and keeping it here stops the panel collapsing to a single line the moment a job
            // ends, which is half of why the gap above was so obvious.
            //
            // Only once there is a reading, though: the card is a pair of meters with a footnote
            // under them, and without the meters the footnote is a sentence about an encode that
            // is not running, sitting in a box on its own.
            if session.cpu != nil || session.gpu != nil {
                loadCard
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

    /// One running job: what it is, what is being done to it, and how it is getting on.
    ///
    /// The name comes first because that is the question someone opening this menu is asking —
    /// "what is my Mac chewing on?" — and a job number answers it for nobody.
    private func jobCard(jobId: Int, progress: JobProgress) -> some View {
        Card {
            VStack(alignment: .leading, spacing: 7) {
                if let strip = session.filmStrips[jobId], !strip.isEmpty {
                    FilmStripView(strip: strip)
                }

                VStack(alignment: .leading, spacing: 2) {
                    Text(session.jobTitles[jobId].flatMap { $0.isEmpty ? nil : $0 } ?? "Job #\(jobId)")
                        .font(.system(size: 11, weight: .medium))
                        .lineLimit(2)
                        .truncationMode(.middle)
                        .fixedSize(horizontal: false, vertical: true)

                    HStack(spacing: 5) {
                        Image(systemName: progress.symbol)
                            .font(.caption2)
                            .foregroundStyle(progress.tint)
                        Text(progress.label)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                        Spacer(minLength: 4)
                        // Only where bytes are actually moving; an encode has no speed to show.
                        if let rate = session.transferRates[jobId], rate > 0 {
                            Text(Self.rate(rate))
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(.secondary)
                        }
                    }
                }

                Meter(value: progress.fraction, tint: progress.tint)
            }
        }
    }

    /// "42.1 MB/s". Per second rather than per bit, matching the byte counts beside it.
    private static func rate(_ bytesPerSecond: Double) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowedUnits = [.useMB, .useGB, .useKB]
        return formatter.string(fromByteCount: Int64(bytesPerSecond)) + "/s"
    }

    /// What this Mac is doing while it works.
    ///
    /// CPU and GPU together, because either alone misleads: a software encode is all CPU and reads
    /// as an idle GPU, while a VideoToolbox encode runs on the media engine and reads as *both*
    /// being quiet. Neither number is wrong; shown apart, each invites the wrong conclusion.
    private var loadCard: some View {
        Card {
            VStack(alignment: .leading, spacing: 6) {
                if let cpu = session.cpu {
                    meterRow("CPU", symbol: "cpu", value: cpu, tint: .teal)
                }
                if let gpu = session.gpu {
                    meterRow("GPU", symbol: "display", value: gpu.device, tint: .purple)
                }
                Text("A VideoToolbox encode runs on the media engine, which macOS reports as neither — so both can read low while this Mac is busy.")
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func meterRow(_ label: String, symbol: String, value: Double, tint: Color) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 6) {
                Image(systemName: symbol)
                    .font(.caption)
                    .foregroundStyle(tint)
                    .frame(width: 13)
                Text(label).font(.system(size: 11, weight: .medium))
                Spacer(minLength: 4)
                Text("\(Int((value * 100).rounded()))%")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Meter(value: value, tint: tint)
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
                Button("Options…") { onShowOptions?() }
                    .controlSize(.small)
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
