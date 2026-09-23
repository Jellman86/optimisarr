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
    @Environment(\.colorScheme) private var colorScheme
    @ObservedObject var session: SidecarSession

    private let machineName: String
    private let previewSettings: SidecarSettings?
    @State private var page = "activity"
    @State private var detailsExpanded = false

    init(session: SidecarSession, machineName: String = Host.current().localizedName ?? "This Mac",
         initialPage: String = "activity", detailsExpanded: Bool = false,
         previewSettings: SidecarSettings? = nil) {
        self.session = session
        self.machineName = machineName
        self.previewSettings = previewSettings
        _page = State(initialValue: initialPage)
        _detailsExpanded = State(initialValue: detailsExpanded)
        _startAtLogin = State(initialValue: previewSettings == nil ? LoginItem.isEnabled : false)
    }
    @State private var showUnpairConfirmation = false
    @State private var serverAddress = ""
    @State private var pin = ""
    @State private var startAtLogin = LoginItem.isEnabled
    @State private var loginItemError: String?
    @State private var isPairing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            statusBar
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if page == "preferences" {
                        Button { page = "activity" } label: {
                            Label("Back to activity", systemImage: "chevron.left")
                        }.buttonStyle(.plain).foregroundStyle(Instrument.phosphor)
                        Text("Preferences").font(.title3.weight(.semibold))
                        OptionsView(settings: previewSettings ?? AppState.shared.settings, embedded: true)
                        loginControl
                    } else if page == "diagnostics" {
                        Button { page = "activity" } label: {
                            Label("Back to activity", systemImage: "chevron.left")
                        }.buttonStyle(.plain).foregroundStyle(Instrument.phosphor)
                        Text("Connection & diagnostics").font(.title3.weight(.semibold))
                        readout
                        Text("Version \(SidecarBuild.version)").font(.caption).foregroundStyle(Instrument.dim)
                        Text("Verification follows the server’s policy. This Mac returns a candidate and evidence; only the server decides whether to replace media.")
                            .font(.caption).foregroundStyle(Instrument.dim)
                        if session.isPaired {
                            Button("Unpair this Mac…") { showUnpairConfirmation = true }
                                .foregroundStyle(Instrument.alarm)
                        }
                    } else {
                        switch session.status {
                        case .unpaired, .pairingFailed, .revoked: pairingForm
                        default: pairedDetail
                        }
                    }
                }.padding(20)
            }.frame(maxHeight: 560)
            footer
        }
        .frame(width: 390)
        .foregroundStyle(Instrument.ink)
        .background(Instrument.ground)
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .fixedSize(horizontal: false, vertical: true)
        .confirmationDialog("Unpair this Mac? Any current jobs will be handed back.", isPresented: $showUnpairConfirmation) {
            Button("Unpair", role: .destructive) { session.unpair(); pin = ""; page = "activity" }
        }
        .onAppear {
            session.restore()
            if serverAddress.isEmpty { serverAddress = session.serverAddress }
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
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Image(nsImage: colorScheme == .dark ? MenuBarIcon.artwork : MenuBarIcon.lightArtwork)
                    .resizable().scaledToFit().frame(width: 30, height: 30)
                    .foregroundStyle(Instrument.phosphor).accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text("Optimisarr").font(.system(size: 17, weight: .semibold))
                    Text(machineName)
                        .font(.caption).foregroundStyle(Instrument.dim).lineLimit(1)
                }
                Spacer()
                Text(session.shutdown.armed ? "SHUTDOWN ARMED" : session.isPaused ? "PAUSED" : session.status.readout)
                    .font(.system(size: 10, weight: .semibold)).foregroundStyle(session.status.lamp)
                Button { page = page == "preferences" ? "activity" : "preferences" } label: {
                    Image(systemName: "gearshape").frame(width: 28, height: 28)
                }.buttonStyle(.plain).help("Preferences").accessibilityLabel("Preferences")
            }
            if let detail = session.status.detail {
                Text(detail).font(.caption).foregroundStyle(session.status.lamp)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }.padding(20)
        .overlay(alignment: .bottom) { Rectangle().fill(Instrument.rule).frame(height: 1) }
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
        VStack(alignment: .leading, spacing: 16) {
            if session.activeJobs.isEmpty {
                idleHead.monitorCard()
            } else {
                ForEach(session.activeJobs.keys.sorted(), id: \.self) { jobId in
                    if let progress = session.activeJobs[jobId] {
                        jobHead(jobId: jobId, progress: progress).monitorCard()
                    }
                }
            }
            cells
            DisclosureGroup("Processing details", isExpanded: $detailsExpanded) {
                VStack(alignment: .leading, spacing: 10) {
                    ForEach(session.activeJobs.keys.sorted(), id: \.self) { jobId in
                        if let progress = session.activeJobs[jobId] {
                            Text("Job #\(jobId) · \(progress.label)")
                                .font(.caption).foregroundStyle(Instrument.phosphor)
                            if let strip = session.filmStrips[jobId], !strip.isEmpty { FilmStripView(strip: strip) }
                        }
                    }
                    Text("Receive → encode → verify when requested → return")
                        .font(.caption).foregroundStyle(Instrument.dim)
                    readout
                    Text("The server decides whether to replace media. No originals are changed from here.")
                        .font(.caption).foregroundStyle(Instrument.dim)
                }.padding(.top, 12)
            }.font(.system(size: 12, weight: .medium)).tint(Instrument.phosphor)
            if session.shutdown.armed {
                Label("New assignments stopped", systemImage: "checkmark.shield")
                    .font(.caption.weight(.semibold)).foregroundStyle(Instrument.phosphor)
            } else {
                HStack {
                    Text("Jobs at once").font(.caption).foregroundStyle(Instrument.dim)
                    Spacer()
                    ForEach(Array(SidecarSession.concurrencyRange), id: \.self) { concurrencyKey($0) }
                }
                Button { session.setPaused(!session.isPaused) } label: {
                    Label(session.isPaused ? "Resume accepting jobs" : session.activeJobs.isEmpty ? "Pause new jobs" : "Pause after current jobs",
                          systemImage: session.isPaused ? "play.fill" : "pause.fill")
                        .font(.system(size: 12, weight: .semibold)).frame(maxWidth: .infinity).padding(.vertical, 10)
                }.buttonStyle(MonitorButtonStyle()).disabled(!session.isPaired)
            }
            VStack(alignment: .leading, spacing: 8) {
                Text("AFTER CURRENT WORK")
                    .font(.system(size: 10, weight: .semibold)).foregroundStyle(Instrument.phosphor)
                Text(session.shutdown.armed ? shutdownDetail :
                     "Stops new jobs, waits for held work to return, then starts a 60-second countdown.")
                    .font(.caption).foregroundStyle(Instrument.dim)
                Button {
                    if session.shutdown.armed { session.cancelShutdown() }
                    else { session.armShutdown() }
                } label: {
                    Label(session.shutdown.armed ? "Cancel shutdown" : "Shut down when work is complete",
                          systemImage: session.shutdown.armed ? "xmark.circle" : "power")
                        .font(.system(size: 12, weight: .semibold))
                        .frame(maxWidth: .infinity).padding(.vertical, 10)
                }.buttonStyle(MonitorButtonStyle()).disabled(!session.isPaired || (session.shutdown.armed && !session.shutdown.canCancel))
            }.padding(12).monitorCard()
            Text(session.shutdown.armed ? "Closing this panel does not cancel shutdown." : session.isPaused ? "Current work will finish. New jobs are paused until resumed or the app restarts." : "Closing this panel keeps your jobs running.")
                .font(.caption).foregroundStyle(Instrument.dim).fixedSize(horizontal: false, vertical: true)
        }
    }

    /// What the machine is not doing, and the last thing it did.
    ///
    /// An idle instrument still reports. "No job held" is the state; the line under it is the
    /// evidence that the machine was working recently and is not quietly broken.
    private var idleTitle: String {
        if session.shutdown.armed { return "Finishing before shutdown" }
        if session.isPaused { return "New jobs paused" }
        switch session.status {
        case .unreachable: return "Waiting for the server"
        case .disabledOnServer: return "Worker disabled on server"
        case .connected: return "Ready for work"
        default: return "No active jobs"
        }
    }

    private var shutdownDetail: String {
        if case .unreachable = session.status { return session.shutdown.detail }
        if session.activeJobs.values.contains(where: {
            if case .delivering = $0 { return true }; return false
        }) {
            return "Finishing candidate upload and waiting for the server acknowledgement. No new jobs are accepted."
        }
        if session.activeJobs.values.contains(where: {
            if case .measuring = $0 { return true }; return false
        }) {
            return "Waiting for sidecar quality checks and verification to finish. No new jobs are accepted."
        }
        return session.shutdown.detail
    }

    private var idleHead: some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(idleTitle)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Instrument.ink)
            Text(session.shutdown.armed ? "No new jobs will be accepted." :
                 session.lastOutcome.map(Self.lastLine) ?? "New jobs will appear here automatically.")
                .font(.system(size: 11, weight: .medium, design: .monospaced))
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
            HStack(alignment: .top, spacing: 12) {
                ZStack {
                    RoundedRectangle(cornerRadius: 7).fill(Instrument.cell)
                    if let data = session.filmStrips[jobId]?.frames.last, let image = NSImage(data: data) {
                        Image(nsImage: image).resizable().scaledToFill()
                    } else {
                        Image(systemName: "film").foregroundStyle(Instrument.dim)
                    }
                }.frame(width: 54, height: 66).clipped().clipShape(RoundedRectangle(cornerRadius: 7))
                    .accessibilityLabel("Media preview")
                VStack(alignment: .leading, spacing: 5) {
                Text(session.jobTitles[jobId].flatMap { $0.isEmpty ? nil : $0 } ?? "Job #\(jobId)")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(Instrument.ink)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .fixedSize(horizontal: false, vertical: true)

                Text(Self.subline(jobId: jobId, progress: progress, session: session))
                    .font(.system(size: 11, weight: .medium, design: .monospaced))
                    .tracking(0.9)
                    .foregroundStyle(Instrument.dim)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            }
            Meter(value: progress.fraction, tint: Instrument.phosphor)

            HStack(spacing: 8) {
                Instrument.label(progress.label)
                Spacer(minLength: 8)
                if let fraction = progress.fraction {
                    Text("\(Int((fraction * 100).rounded()))%")
                        .font(.system(size: 11, weight: .medium, design: .monospaced))
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
            Text("macOS does not report VideoToolbox media-engine usage.")
                .font(.system(size: 11))
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
        case let .failed(jobId, _):
            return "#\(jobId) · failed"
        case let .released(jobId, _), let .leaseLost(jobId, _), let .unconfirmed(jobId, _):
            return "#\(jobId) · handed back"
        }
    }

    // MARK: - Footer

    /// The face's controls, along the bottom where they cannot be mistaken for readings.
    private var loginControl: some View {
        VStack(alignment: .leading, spacing: 8) {
            Toggle("Start at login", isOn: Binding(get: { startAtLogin }, set: { wanted in
                do {
                    try LoginItem.setEnabled(wanted)
                    startAtLogin = LoginItem.isEnabled
                    loginItemError = nil
                } catch {
                    startAtLogin = LoginItem.isEnabled
                    loginItemError = "Could not change the login item: \(error.localizedDescription)"
                }
            })).toggleStyle(.checkbox).font(.caption)
            if let loginItemError { Text(loginItemError).font(.caption).foregroundStyle(Instrument.alarm) }
        }
    }

    private var footer: some View {
        HStack {
            Button { page = page == "diagnostics" ? "activity" : "diagnostics" } label: {
                Label("Diagnostics", systemImage: "waveform.path.ecg")
            }.buttonStyle(.plain).help("Connection, version and pairing")
            Spacer()
            Menu {
                if let url = serverURL { Button("Open Optimisarr") { NSWorkspace.shared.open(url) } }
                Button("Quit sidecar") { NSApplication.shared.terminate(nil) }
            } label: { Image(systemName: "ellipsis").frame(width: 28, height: 24) }
                .menuStyle(.borderlessButton).frame(width: 32).help("More actions")
        }.font(.caption).foregroundStyle(Instrument.dim).padding(.horizontal, 20).padding(.vertical, 13)
            .overlay(alignment: .top) { Rectangle().fill(Instrument.rule).frame(height: 1) }
    }

    private var serverURL: URL? {
        let address = session.serverAddress
        guard !address.isEmpty, let url = URL(string: address.contains("://") ? address : "http://" + address),
              ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
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
                .frame(width: 28, height: 26)
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
            return "Last job #\(jobId): returned \(size) to the server."
        case let .failed(jobId, reason):
            return "Last job #\(jobId): \(reason)"
        case let .released(jobId, reason):
            return "Last job #\(jobId): handed back — \(reason)"
        case let .unconfirmed(jobId, reason):
            return "Last job #\(jobId): hand-back unconfirmed — \(reason)"
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
