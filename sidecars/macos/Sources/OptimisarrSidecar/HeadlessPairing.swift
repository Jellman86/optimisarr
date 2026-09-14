import Foundation
import SidecarCore

/// Pairing without a screen.
///
///     OptimisarrSidecar --pair optimisarr.example.com
///
/// The PIN is read from standard input rather than taken as an argument, so it never appears in
/// `ps` output or a shell history where any other account on the machine could read it.
///
/// The menu's pairing sheet assumes somebody is sitting in front of the Mac. That is a poor
/// assumption for a machine whose whole purpose is to sit in a cupboard and encode: an app that can
/// only be paired by hand cannot be set up over SSH, cannot be scripted onto several machines, and
/// cannot be recovered remotely when a pairing is lost — which is exactly the position this was
/// written to get out of. The Windows sidecar will need the same thing more urgently still, since
/// it runs as a service with no logged-in user at all.
///
/// Pairing writes the credential through the same store the app uses, so a pairing made this way is
/// the app's pairing: the item is written by this signed bundle and read back by it on next launch.
enum HeadlessPairing {
    static let flag = "--pair"

    /// Returns the process exit code. Prints one line saying what happened, on stdout for success
    /// and stderr for failure, so a script can act on either without parsing prose.
    static func run(serverAddress: String) async -> Int32 {
        guard let pin = readLine(strippingNewline: true)?.trimmingCharacters(in: .whitespaces),
              !pin.isEmpty
        else {
            FileHandle.standardError.write(Data("No pairing code on stdin.\n".utf8))
            return 2
        }

        let session = await MainActor.run { SidecarSession() }
        await session.pair(serverAddress: serverAddress, pin: pin)

        let status = await MainActor.run { session.status }
        // Anything but an outright refusal counts: a machine that paired and then could not reach
        // the server on its first check-in is paired, and will recover on its own. Only a rejected
        // PIN means the operator has to do something.
        switch status {
        case .pairingFailed(let reason):
            // The reason, not `status.summary`: that is the menu bar's three-word label, and
            // "Pairing failed: Pairing failed" is what it produces here.
            var message = "Pairing failed: \(reason)\n"
            // A bare host is reached over http, which is right for a server on a home network and
            // wrong for one behind a TLS proxy — where it fails with nothing to suggest the scheme
            // is the problem. Say so rather than let the next person lose the same ten minutes.
            if !serverAddress.lowercased().hasPrefix("http") {
                message += "Tried http://\(serverAddress) — give the address as "
                    + "https://\(serverAddress) if the server is behind TLS.\n"
            }
            FileHandle.standardError.write(Data(message.utf8))
            return 1

        case .unpaired:
            FileHandle.standardError.write(Data("Pairing failed: the code was not accepted.\n".utf8))
            return 1
        case .connected(let workerId, _):
            print("Paired with \(serverAddress) as worker \(workerId).")
            return 0
        default:
            // Paired, but not yet checked in — an unreachable server or one with the feature off.
            // The credential is stored and the app will recover on its own, so this is a success
            // with something worth saying.
            print("Paired with \(serverAddress). \(status.summary)")
            return 0
        }
    }
}
