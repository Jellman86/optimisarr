import SidecarCore
import SwiftUI

/// The whole interface: what state we are in, and the one action that state allows.
///
/// Deliberately small. This app has exactly two jobs — pair, and report honestly — and a menu that
/// offers more than that would imply capabilities it does not have.
struct SidecarMenu: View {
    @ObservedObject var session: SidecarSession

    @State private var serverAddress = ""
    @State private var pin = ""
    @State private var isPairing = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header

            Divider()

            switch session.status {
            case .unpaired, .pairingFailed:
                pairingForm
            default:
                pairedDetail
            }

            Divider()

            Button("Quit Optimisarr Sidecar") {
                NSApplication.shared.terminate(nil)
            }
            .keyboardShortcut("q")
        }
        .padding(14)
        .frame(width: 320)
        .onAppear {
            session.restore()
            if serverAddress.isEmpty { serverAddress = session.serverAddress }
        }
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("Optimisarr Sidecar").font(.headline)
            Text(session.status.summary)
                .font(.subheadline)
                .foregroundStyle(session.status.isHealthy ? .secondary : .primary)

            // The reason matters more than the state name — "Pairing failed" is not actionable,
            // but "that code has expired" is.
            if let detail = session.status.detail {
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

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
                    // Only cleared on success. Leaving a rejected code in place lets someone fix a
                    // typo instead of retyping the whole thing.
                    if case .connected = session.status { pin = "" }
                }
            }
            .buttonStyle(.borderedProminent)
            .disabled(isPairing || serverAddress.isEmpty || pin.isEmpty)
        }
    }

    private var pairedDetail: some View {
        VStack(alignment: .leading, spacing: 8) {
            LabeledContent("Server", value: session.serverAddress)
                .font(.caption)

            if case let .connected(workerId, lastCheckIn) = session.status {
                LabeledContent("Worker", value: "#\(workerId)").font(.caption)
                LabeledContent("Last check-in", value: lastCheckIn.formatted(date: .omitted, time: .standard))
                    .font(.caption)
            }

            // Said plainly rather than buried. Someone who pairs a machine reasonably expects it to
            // start doing something, and it will not yet.
            Text("This sidecar does not transcode yet. It pairs and reports in so the connection can be set up and tested while the rest is built.")
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Button("Forget this pairing") {
                session.unpair()
                pin = ""
            }
        }
    }
}

private extension SidecarStatus {
    var isHealthy: Bool {
        if case .connected = self { return true }
        return false
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
