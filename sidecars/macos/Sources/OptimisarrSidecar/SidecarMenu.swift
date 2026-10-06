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
                        }.buttonStyle(.borderless)
                        Text("Preferences").font(.title3.weight(.semibold))
                        OptionsView(settings: previewSettings ?? AppState.shared.settings, embedded: true)
                        loginControl
                    } else if page == "diagnostics" {
                        Button { page = "activity" } label: {
                            Label("Back to activity", systemImage: "chevron.left")
                        }.buttonStyle(.borderless)
                        Text("Connection & diagnostics").font(.title3.weight(.semibold))
                        readout
                        Text("Version \(SidecarBuild.version)").font(.caption).foregroundStyle(Instrument.ink3)
                        Text("Verification follows the server’s policy. This Mac returns a candidate and evidence; only the server decides whether to replace media.")
                            .font(.caption).foregroundStyle(Instrument.ink3)
                        if session.isPaired {
                            Button("Unpair this Mac…") { showUnpairConfirmation = true }
                                .panelButton().tint(Instrument.bad)
                        }
                    } else {
                        if let update = session.availableUpdate { updateNotice(update) }
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
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Optimisarr").font(.system(size: 17, weight: .semibold))
                    Text(machineName)
                        .font(.system(size: 12)).foregroundStyle(Instrument.ink3).lineLimit(1)
                }
                Spacer()
                StatusChip(text: chipText, tone: chipTone)
                Button { page = page == "preferences" ? "activity" : "preferences" } label: {
                    Image(systemName: "gearshape").font(.system(size: 14)).frame(width: 18, height: 18)
                }.buttonStyle(.borderless).help("Preferences").accessibilityLabel("Preferences")
            }
            if let detail = session.status.detail {
                Text(detail).font(.system(size: 12)).foregroundStyle(session.status.tone.ink)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }.padding(.horizontal, 20).padding(.vertical, 16)
        .overlay(alignment: .bottom) { Rectangle().fill(Instrument.separator).frame(height: 1) }
    }

    /// The state in a word or two, from the table both sidecars share: pause and an armed
    /// shutdown outrank the connection, because they say what the machine will do next.
    private var chipText: String {
        session.shutdown.armed ? "Shutdown armed" : session.isPaused ? "Paused" : session.status.readout
    }

    private var chipTone: Tone {
        session.shutdown.armed ? .warn : session.isPaused ? .info : session.status.tone
    }

    // MARK: - Pairing

    private var pairingForm: some View {
        VStack(alignment: .leading, spacing: 9) {
            Text("Enter the address of your Optimisarr server and the pairing code it shows under Settings → Workers.")
                .font(.system(size: 12))
                .foregroundStyle(Instrument.ink3)
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
                    .frame(maxWidth: .infinity)
            }
            .panelButton(prominent: true)
            .disabled(!canPair)
        }
    }

    private var canPair: Bool {
        !isPairing && !serverAddress.isEmpty && !pin.isEmpty
    }

    private func field(_ prompt: String, text: Binding<String>) -> some View {
        TextField(prompt, text: text)
            .textFieldStyle(.roundedBorder)
            .controlSize(.large)
            .disableAutocorrection(true)
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
                            Text("Job #\(jobId) · \(progress.stage) · \(progress.label)")
                                .font(.caption.weight(.semibold)).foregroundStyle(Instrument.ink2)
                            if session.audioJobs.contains(jobId) {
                                Text("Source spectrogram shown in the job card above.").font(.caption).foregroundStyle(Instrument.ink3)
                            } else if let strip = session.filmStrips[jobId], !strip.isEmpty { FilmStripView(strip: strip) }
                        }
                    }
                    Text("Receive → encode → verify when requested → return")
                        .font(.caption).foregroundStyle(Instrument.ink3)
                    readout
                    Text("The server decides whether to replace media. No originals are changed from here.")
                        .font(.caption).foregroundStyle(Instrument.ink3)
                }.padding(.top, 12)
            }.font(.system(size: 13, weight: .semibold))
            if session.shutdown.armed {
                Label("New assignments stopped", systemImage: "checkmark.shield")
                    .font(.system(size: 12, weight: .semibold)).foregroundStyle(Instrument.warn)
            } else {
                HStack {
                    Text("Jobs at once").font(.system(size: 12, weight: .medium)).foregroundStyle(Instrument.ink2)
                    Spacer()
                    Picker("Jobs at once", selection: Binding(
                        get: { session.jobConcurrency }, set: { session.setJobConcurrency($0) })) {
                        ForEach(Array(SidecarSession.concurrencyRange), id: \.self) { count in
                            Text("\(count)").tag(count)
                                .accessibilityLabel("\(count) job\(count == 1 ? "" : "s") at once")
                        }
                    }.pickerStyle(.segmented).labelsHidden().fixedSize()
                }
                Button { session.setPaused(!session.isPaused) } label: {
                    Label(session.isPaused ? "Resume accepting jobs" : session.activeJobs.isEmpty ? "Pause new jobs" : "Pause after current jobs",
                          systemImage: session.isPaused ? "play.fill" : "pause.fill")
                        .frame(maxWidth: .infinity)
                }.panelButton().disabled(!session.isPaired)
            }
            VStack(alignment: .leading, spacing: 8) {
                Text("After current work")
                    .font(.system(size: 13, weight: .semibold)).foregroundStyle(Instrument.ink)
                Text(session.shutdown.armed ? shutdownDetail :
                     "Stops new jobs, waits for held work to return, then starts a 60-second countdown.")
                    .font(.system(size: 12)).foregroundStyle(Instrument.ink3)
                    .fixedSize(horizontal: false, vertical: true)
                Button {
                    if session.shutdown.armed { session.cancelShutdown() }
                    else { session.armShutdown() }
                } label: {
                    Label(session.shutdown.armed ? "Cancel shutdown" : "Shut down when work is complete",
                          systemImage: session.shutdown.armed ? "xmark.circle" : "power")
                        .frame(maxWidth: .infinity)
                }.panelButton().disabled(!session.isPaired || (session.shutdown.armed && !session.shutdown.canCancel))
                    .padding(.top, 2)
            }.frame(maxWidth: .infinity, alignment: .leading).monitorCard()
            Text(session.shutdown.armed ? "Closing this panel does not cancel shutdown." : session.isPaused ? "Current work will finish. New jobs are paused until resumed or the app restarts." : "Closing this panel keeps your jobs running.")
                .font(.system(size: 12)).foregroundStyle(Instrument.ink3).fixedSize(horizontal: false, vertical: true)
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
                .font(.system(size: 12))
                .foregroundStyle(Instrument.ink3)
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
                if !session.audioJobs.contains(jobId) {
                ZStack {
                    RoundedRectangle(cornerRadius: 8).fill(Instrument.well)
                    if let data = session.filmStrips[jobId]?.frames.last, let image = NSImage(data: data) {
                        Image(nsImage: image).resizable().scaledToFill()
                    } else {
                        Image(systemName: "film").foregroundStyle(Instrument.ink4)
                    }
                }.frame(width: 96, height: 54).clipped().clipShape(RoundedRectangle(cornerRadius: 8))
                    .accessibilityLabel("Media preview")
                }
                VStack(alignment: .leading, spacing: 5) {
                Text(session.jobTitles[jobId].flatMap { $0.isEmpty ? nil : $0 } ?? "Job #\(jobId)")
                    .font(.system(size: 15, weight: .semibold))
                    .foregroundStyle(Instrument.ink)
                    .lineLimit(2)
                    .truncationMode(.middle)
                    .fixedSize(horizontal: false, vertical: true)

                Text(Self.subline(jobId: jobId, progress: progress, session: session))
                    .font(.system(size: 12))
                    .monospacedDigit()
                    .foregroundStyle(Instrument.ink3)
                    .lineLimit(1)
                    .truncationMode(.middle)
            }

            }
            if session.audioJobs.contains(jobId) {
                AudioSpectrumView(frame: session.filmStrips[jobId]?.frames.last)
            }
            if let storage = session.jobStorage[jobId] {
                VStack(alignment: .leading, spacing: 3) {
                    Text(storage.summary).font(.system(size: 12, weight: .medium))
                    if let reason = storage.fallbackReason {
                        Text(reason).font(.system(size: 12)).fixedSize(horizontal: false, vertical: true)
                    }
                }.foregroundStyle(Instrument.ink3).help(storage.path)
            }
            Group {
                if let fraction = progress.fraction { ProgressView(value: fraction) } else { ProgressView() }
            }
            .progressViewStyle(.linear).controlSize(.small).padding(.top, 4)
            .accessibilityValue(progress.fraction.map { "\(Int(($0 * 100).rounded())) percent" } ?? "in progress")

            HStack(spacing: 8) {
                Text(Self.progressLine(jobId: jobId, progress: progress, session: session))
                    .font(.system(size: 11, weight: .medium)).monospacedDigit().foregroundStyle(Instrument.ink3)
                Spacer(minLength: 8)
                if let fraction = progress.fraction {
                    Text("\(Int((fraction * 100).rounded()))%")
                        .font(.system(size: 11, weight: .semibold))
                        .monospacedDigit()
                        .foregroundStyle(Instrument.ink2)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// The stage and what it is working with, on one line under the title: "Encoding ·
    /// hevc_videotoolbox", the same line the Windows tray shows.
    private static func subline(
        jobId: Int, progress: JobProgress, session: SidecarSession
    ) -> String {
        var parts = [progress.stage]
        if let encoder = session.jobEncoders[jobId], !encoder.isEmpty { parts.append(encoder) }
        return parts.joined(separator: " · ")
    }

    /// How far in, under the bar: "182 MB of 493 MB · 42.1 MB/s" while bytes move.
    private static func progressLine(jobId: Int, progress: JobProgress, session: SidecarSession) -> String {
        guard let rate = session.transferRates[jobId], rate > 0 else { return progress.label }
        return progress.label + " · " + rate2(rate)
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
        .overlay(alignment: .top) { Rectangle().fill(Instrument.separator).frame(height: 1) }
    }

    /// The three figures worth a glance rather than a read.
    ///
    /// CPU and GPU together, because either alone misleads: a software encode is all CPU and reads
    /// as an idle GPU, while a VideoToolbox encode runs on the media engine and reads as *both*
    /// being quiet. Neither number is wrong; shown apart, each invites the wrong conclusion, which
    /// is why the note under them stays even when the panel is otherwise terse.
    private var cells: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 0) {
                ReadoutCell(figure: session.cpu.map(Self.percent) ?? "—", unit: "CPU load")
                Rectangle().fill(Instrument.separator).frame(width: 1, height: 34).padding(.horizontal, 12)
                ReadoutCell(figure: session.gpu.map { Self.percent($0.device) } ?? "—", unit: "GPU load")
                Rectangle().fill(Instrument.separator).frame(width: 1, height: 34).padding(.horizontal, 12)
                ReadoutCell(figure: session.freeScratchBytes.map(Self.gigabytes) ?? "—", unit: "Free space")
            }.monitorCard()
            Text("macOS does not report VideoToolbox media-engine usage.")
                .font(.system(size: 12))
                .foregroundStyle(Instrument.ink3)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private static func percent(_ value: Double) -> String {
        "\(Int((value * 100).rounded()))%"
    }

    /// "428 GB": whole gigabytes with one decimal below ten, as the Windows tray writes it.
    private static func gigabytes(_ bytes: Int64) -> String {
        let value = Double(bytes) / 1_073_741_824
        return value < 10 ? String(format: "%.1f GB", value) : "\(Int(value.rounded())) GB"
    }

    /// "42.1 MB/s". Per second rather than per bit, matching the byte counts beside it.
    private static func rate2(_ bytesPerSecond: Double) -> String {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowedUnits = [.useMB, .useGB, .useKB]
        return formatter.string(fromByteCount: Int64(bytesPerSecond)) + "/s"
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

    // MARK: - Update

    /// Said plainly and first: an outdated sidecar can fail good encodes while every light is green.
    private func updateNotice(_ update: SidecarUpdate) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Label("Update available", systemImage: "arrow.down.circle.fill")
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(Instrument.accent)
            Text("The server is on \(update.version) and this sidecar is older. Older sidecars can fail good encodes.")
                .font(.system(size: 12)).foregroundStyle(Instrument.ink3)
                .fixedSize(horizontal: false, vertical: true)
            Button("Open release page") { NSWorkspace.shared.open(update.releasePage) }
                .panelButton().padding(.top, 4)
        }
        .monitorCard()
        .accessibilityElement(children: .contain)
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
            })).toggleStyle(.switch).controlSize(.small)
            if let loginItemError { Text(loginItemError).font(.caption).foregroundStyle(Instrument.bad) }
        }
    }

    private var footer: some View {
        HStack {
            Button { page = page == "diagnostics" ? "activity" : "diagnostics" } label: {
                Label("Diagnostics", systemImage: "waveform.path.ecg")
            }.buttonStyle(.borderless).help("Connection, version and pairing")
            Spacer()
            Menu {
                if let url = serverURL { Button("Open Optimisarr") { NSWorkspace.shared.open(url) } }
                Button("Quit sidecar") { NSApplication.shared.terminate(nil) }
            } label: { Image(systemName: "ellipsis").frame(width: 28, height: 24) }
                .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 32).help("More actions")
        }.font(.system(size: 12, weight: .medium)).foregroundStyle(Instrument.ink3).padding(.horizontal, 12).padding(.vertical, 10)
            .overlay(alignment: .top) { Rectangle().fill(Instrument.separator).frame(height: 1) }
    }

    private var serverURL: URL? {
        let address = session.serverAddress
        guard !address.isEmpty, let url = URL(string: address.contains("://") ? address : "http://" + address),
              ["http", "https"].contains(url.scheme?.lowercased() ?? "") else { return nil }
        return url
    }
}

private extension JobProgress {
    /// The stage in the words both sidecars use.
    var stage: String {
        switch self {
        case .fetchingSource: return "Receiving source"
        case .encoding: return "Encoding"
        case .measuring: return "Quality & verification"
        case .delivering: return "Returning candidate"
        }
    }

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
    /// What the state means, as one of the web interface's tones, used by the chip and by fault
    /// text. Blue means the machine is doing what it should, gold that something may pass on its
    /// own, raspberry that somebody has to act, and graphite a fact rather than a verdict. The
    /// table is shared with the Windows tray (`docs/design/windows-sidecar/README.md`).
    var tone: Tone {
        switch self {
        case .working, .connected: return .ok
        case .unreachable, .disabledOnServer: return .warn
        case .pairingFailed, .revoked: return .bad
        case .unpaired, .pairing: return .info
        }
    }

    /// The state in the words the chip has room for.
    var readout: String {
        switch self {
        case .working: return "Working"
        case .connected: return "Ready"
        case .unreachable: return "No server"
        case .disabledOnServer: return "Stood down"
        case .pairingFailed: return "Pairing failed"
        case .revoked: return "Revoked"
        case .unpaired: return "Not paired"
        case .pairing: return "Pairing"
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
