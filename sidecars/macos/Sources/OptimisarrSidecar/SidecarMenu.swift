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
        VStack(alignment: .leading, spacing: 0) {
            statusBar

            VStack(alignment: .leading, spacing: 11) {
                switch session.status {
                case .unpaired, .pairingFailed:
                    pairingForm
                default:
                    pairedDetail
                }
            }
            .padding(13)

            footer
        }
        .frame(width: 340)
        .background(Instrument.ground)
        // A menu-bar panel does not always shrink back when its content does. After a job
        // finishes, the window keeps the height the film strip needed — and SwiftUI centres a
        // shorter view inside it, so the panel hangs away from the menu bar with a gap above it
        // exactly the size of the frames that are no longer there. A spacer inside the stack
        // cannot fix that: the stack is sized to its content and never sees the spare height.
        // This does, by filling whatever height the window has and keeping the panel at the top
        // of it.
        .frame(maxHeight: .infinity, alignment: .top)
        .onAppear {
            session.restore()
            if serverAddress.isEmpty { serverAddress = session.serverAddress }
            // Frame grabs and GPU readings cost something, so they run only while this is on
            // screen. A menu-bar window is closed far more of the time than it is open.
            session.setPreviewsWanted(true)
        }
        .onDisappear { session.setPreviewsWanted(false) }
    }

    // MARK: - Status bar

    /// Which machine this is and what it is doing, in the two places you look first.
    ///
    /// The identity matters because a fleet has several of these and they all look alike; the
    /// state sits opposite it so the pair can be read in one movement rather than hunted for.
    private var statusBar: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                Instrument.label(Self.machineName)
                Spacer(minLength: 8)
                Circle()
                    .fill(session.status.lamp)
                    .frame(width: 5, height: 5)
                    .accessibilityHidden(true)
                Text(session.status.readout)
                    .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                    .tracking(0.9)
                    .foregroundStyle(session.status.lamp)
            }
            .padding(.horizontal, 13)
            .padding(.vertical, 9)

            // The reason matters more than the state name — "Pairing failed" is not actionable,
            // but "that code has expired" is. It sits inside the bar so a fault reads as part of
            // the machine's state rather than as a notice pinned over the top of it.
            if let detail = session.status.detail {
                Text(detail)
                    .font(.system(size: 10.5))
                    .foregroundStyle(session.status.lamp)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, 13)
                    .padding(.bottom, 9)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .overlay(alignment: .bottom) { Rectangle().fill(Instrument.rule).frame(height: 1) }
    }

    /// This Mac, as the fleet knows it.
    private static var machineName: String {
        let name = Host.current().localizedName ?? "This Mac"
        return "SIDECAR · \(name)"
    }

    // MARK: - Pairing

    private var pairingForm: some View {
        VStack(alignment: .leading, spacing: 9) {
            Text("Enter the address of your Optimisarr server and the pairing code it shows under Settings → Workers.")
                .font(.system(size: 10.5))
                .foregroundStyle(Instrument.dim)
                .fixedSize(horizontal: false, vertical: true)

            field("optimisarr.local:8787", text: $serverAddress)
            field("Pairing code", text: $pin)

            Button {
                Task {
                    isPairing = true
                    await session.pair(serverAddress: serverAddress, pin: pin)
                    isPairing = false
                    // Only cleared on success. Leaving a rejected code in place lets someone fix
                    // a typo instead of retyping the whole thing.
                    if case .connected = session.status { pin = "" }
                }
            } label: {
                Text(isPairing ? "Pairing…" : "Pair")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(canPair ? Instrument.ground : Instrument.dim)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 6)
                    .background(
                        RoundedRectangle(cornerRadius: 5)
                            .fill(canPair ? Instrument.phosphor : Instrument.cell))
            }
            .buttonStyle(.plain)
            .disabled(!canPair)
        }
    }

    private var canPair: Bool {
        !isPairing && !serverAddress.isEmpty && !pin.isEmpty
    }

    /// A field on the instrument's face rather than the system's: a rounded-border text field
    /// draws itself for a light window and disappears into this ground.
    private func field(_ prompt: String, text: Binding<String>) -> some View {
        TextField("", text: text, prompt:
            Text(prompt).foregroundStyle(Instrument.dim))
            .textFieldStyle(.plain)
            .font(.system(size: 11, design: .monospaced))
            .foregroundStyle(Instrument.ink)
            .disableAutocorrection(true)
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
            .background(RoundedRectangle(cornerRadius: 5).fill(Instrument.cell))
            .overlay(RoundedRectangle(cornerRadius: 5).stroke(Instrument.rule, lineWidth: 1))
    }

    // MARK: - Paired

    private var pairedDetail: some View {
        VStack(alignment: .leading, spacing: 11) {
            if session.activeJobs.isEmpty {
                idleHead
            } else {
                ForEach(session.activeJobs.keys.sorted(), id: \.self) { jobId in
                    if let progress = session.activeJobs[jobId] {
                        jobHead(jobId: jobId, progress: progress)
                    }
                }
            }

            readout
            cells

            // Said plainly: a delivered candidate is a proposal. Optimisarr verifies it against
            // the original before anything is replaced, and this machine never sees that decision.
            Text("Encodes go to Optimisarr for verification. Nothing is replaced from here.")
                .font(.system(size: 9.5))
                .foregroundStyle(Instrument.dim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// What the machine is not doing, and the last thing it did.
    ///
    /// An idle instrument still reports. "No job held" is the state; the line under it is the
    /// evidence that the machine was working recently and is not quietly broken.
    private var idleHead: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text("No job held")
                .font(.system(size: 12.5, weight: .semibold))
                .foregroundStyle(Instrument.ink)
            Text(session.lastOutcome.map(Self.lastLine) ?? "NOTHING RUN ON THIS MACHINE YET")
                .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                .tracking(0.9)
                .foregroundStyle(Instrument.dim)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// One running job: what it is, what is being done to it, and how far in it is.
    ///
    /// The name comes first because that is the question someone opening this panel is asking —
    /// "what is my Mac chewing on?" — and a job number answers it for nobody.
    private func jobHead(jobId: Int, progress: JobProgress) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            // The frames going through the encoder, flush to the panel's edges rather than inset
            // in a card. On an instrument a picture is a monitor, not an illustration.
            if let strip = session.filmStrips[jobId], !strip.isEmpty {
                FilmStripView(strip: strip)
                    .overlay(RoundedRectangle(cornerRadius: 3).stroke(Instrument.rule, lineWidth: 1))
            }

            VStack(alignment: .leading, spacing: 3) {
                Text(session.jobTitles[jobId].flatMap { $0.isEmpty ? nil : $0 } ?? "Job #\(jobId)")
                    .font(.system(size: 12.5, weight: .semibold))
                    .foregroundStyle(Instrument.ink)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .fixedSize(horizontal: false, vertical: true)

                Text(Self.subline(jobId: jobId, progress: progress, session: session))
                    .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                    .tracking(0.9)
                    .foregroundStyle(Instrument.dim)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            SegmentMeter(fraction: progress.fraction)

            HStack(spacing: 8) {
                Instrument.label(progress.label)
                Spacer(minLength: 8)
                if let fraction = progress.fraction {
                    Text("\(Int((fraction * 100).rounded()))%")
                        .font(.system(size: 9.5, weight: .medium, design: .monospaced))
                        .monospacedDigit()
                        .foregroundStyle(Instrument.phosphor)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The stage and what it is working with, on one line under the title.
    private static func subline(
        jobId: Int, progress: JobProgress, session: SidecarSession
    ) -> String {
        var parts = ["JOB \(jobId)"]
        if let rate = session.transferRates[jobId], rate > 0 {
            parts.append(rate2(rate))
        }
        return parts.joined(separator: " · ")
    }

    /// Everything the machine knows about itself, in one column that can be read down.
    private var readout: some View {
        VStack(spacing: 0) {
            ReadoutRow(name: "Server", value: session.serverAddress)
            if case let .connected(workerId, lastCheckIn) = session.status {
                ReadoutRow(name: "Worker", value: "#\(workerId)")
                ReadoutRow(
                    name: "Check-in",
                    value: lastCheckIn.formatted(date: .omitted, time: .standard))
            }
            ReadoutRow(name: "Concurrency", value: "\(session.jobConcurrency) of \(SidecarSession.concurrencyRange.upperBound)")
            if !session.activeJobs.isEmpty, let outcome = session.lastOutcome {
                ReadoutRow(name: "Last", value: Self.lastValue(outcome))
            }
        }
        .overlay(alignment: .top) { Rectangle().fill(Instrument.rule).frame(height: 1) }
    }

    /// The three figures worth a glance rather than a read.
    ///
    /// CPU and GPU together, because either alone misleads: a software encode is all CPU and reads
    /// as an idle GPU, while a VideoToolbox encode runs on the media engine and reads as *both*
    /// being quiet. Neither number is wrong; shown apart, each invites the wrong conclusion, which
    /// is why the note under them stays even when the panel is otherwise terse.
    private var cells: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 7) {
                ReadoutCell(figure: session.cpu.map(Self.percent) ?? "—", unit: "CPU %")
                ReadoutCell(figure: session.gpu.map { Self.percent($0.device) } ?? "—", unit: "GPU %")
                ReadoutCell(figure: "\(session.activeJobs.count)", unit: "HELD")
            }
            Text("A VideoToolbox encode runs on the media engine, which macOS reports as neither — so both can read low while this Mac is busy.")
                .font(.system(size: 9.5))
                .foregroundStyle(Instrument.dim)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private static func percent(_ value: Double) -> String {
        "\(Int((value * 100).rounded()))"
    }

    /// "42.1 MB/s". Per second rather than per bit, matching the byte counts beside it.
    private static func rate2(_ bytesPerSecond: Double) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowedUnits = [.useMB, .useGB, .useKB]
        return (formatter.string(fromByteCount: Int64(bytesPerSecond)) + "/s").uppercased()
    }

    private static func lastLine(_ outcome: JobOutcome) -> String {
        outcome.label.uppercased()
    }

    private static func lastValue(_ outcome: JobOutcome) -> String {
        switch outcome {
        case let .delivered(jobId, bytes):
            return "#\(jobId) · " + ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
        case let .released(jobId, _), let .leaseLost(jobId, _):
            return "#\(jobId) · handed back"
        }
    }

    // MARK: - Footer

    /// The face's controls, along the bottom where they cannot be mistaken for readings.
    private var footer: some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 10) {
                Instrument.label("Jobs at once")
                Spacer(minLength: 8)
                // Takes effect on the next check-in; a running job is never stopped to fit.
                HStack(spacing: 3) {
                    ForEach(Array(SidecarSession.concurrencyRange), id: \.self) { count in
                        concurrencyKey(count)
                    }
                }
            }

            Toggle(isOn: Binding(
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
                })) {
                    Text("Start at login")
                        .font(.system(size: 10.5))
                        .foregroundStyle(Instrument.value)
                }
                .toggleStyle(.checkbox)

            if let loginItemError {
                Text(loginItemError)
                    .font(.system(size: 10.5))
                    .foregroundStyle(Instrument.alarm)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack(spacing: 6) {
                InstrumentButton(title: "Options") { onShowOptions?() }
                if case .unpaired = session.status {} else {
                    InstrumentButton(title: "Unpair") {
                        session.unpair()
                        pin = ""
                    }
                }
                InstrumentButton(title: "Quit", emphasis: Instrument.dim) {
                    NSApplication.shared.terminate(nil)
                }
                .keyboardShortcut("q")
            }
        }
        .padding(13)
        .overlay(alignment: .top) { Rectangle().fill(Instrument.rule).frame(height: 1) }
    }

    /// One position of the concurrency control. A key rather than a segmented picker: the panel
    /// has four choices and a system picker would bring its own appearance onto this face.
    private func concurrencyKey(_ count: Int) -> some View {
        let chosen = session.jobConcurrency == count
        return Button {
            session.setJobConcurrency(count)
        } label: {
            Text("\(count)")
                .font(.system(size: 10.5, weight: .medium, design: .monospaced))
                .foregroundStyle(chosen ? Instrument.ground : Instrument.value)
                .frame(width: 22, height: 18)
                .background(
                    RoundedRectangle(cornerRadius: 4)
                        .fill(chosen ? Instrument.phosphor : Instrument.cell))
                .overlay(RoundedRectangle(cornerRadius: 4).stroke(Instrument.rule, lineWidth: 1))
        }
        .buttonStyle(.plain)
        .accessibilityLabel("\(count) job\(count == 1 ? "" : "s") at once")
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
    /// One colour for the whole panel's sense of health, used by the lamp and by fault text.
    ///
    /// Three colours and no more: lit means the machine is doing what it should, amber means it is
    /// held up by something that may pass, and alarm means somebody has to do something. A fourth
    /// would have to mean something, and there is nothing else for it to mean.
    var lamp: Color {
        switch self {
        case .working, .connected: return Instrument.phosphor
        case .unreachable, .disabledOnServer: return Instrument.amber
        case .pairingFailed, .revoked: return Instrument.alarm
        default: return Instrument.dim
        }
    }

    /// The state in the one word the bar has room for.
    var readout: String {
        switch self {
        case .working: return "WORKING"
        case .connected: return "IDLE"
        case .unreachable: return "NO LINK"
        case .disabledOnServer: return "STOOD DOWN"
        case .pairingFailed: return "PAIRING FAILED"
        case .revoked: return "REVOKED"
        case .unpaired: return "UNPAIRED"
        case .pairing: return "PAIRING"
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
