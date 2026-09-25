import Foundation

/// This app's own build, as an operator would read it off the About box and quote in a bug report.
///
/// Reported to the server on pairing and on every check-in. The server records the protocol
/// version two ends agreed to speak, which says nothing about which build is installed — so an
/// upgrade that never happened, or one that landed on only some machines, looked exactly like a
/// fleet running the current code. This is the field that tells them apart.
public enum SidecarBuild {
    /// `0.1.6 (202609141130)` from a bundled app: the short version people quote, and the build
    /// number that actually distinguishes two builds carrying the same version — which is the
    /// distinction that matters when a locally built app and a release both call themselves 0.1.6.
    ///
    /// `"unknown"` when there is no bundle to read: the test suite, and the bare executable Swift
    /// Package Manager produces. Saying so is more useful than inventing a number, because the
    /// server shows this to a person who is deciding whether to trust what a machine is running.
    public static let version: String = describe(Bundle.main.infoDictionary)

    /// Split out so the formatting is testable without a bundle to stand in for one.
    static func describe(_ info: [String: Any]?) -> String {
        let short = (info?["CFBundleShortVersionString"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let build = (info?["CFBundleVersion"] as? String)?
            .trimmingCharacters(in: .whitespacesAndNewlines)

        switch (short.flatMap(nonEmpty), build.flatMap(nonEmpty)) {
        case let (short?, build?) where short != build:
            return "\(short) (\(build))"
        case let (short?, _):
            return short
        case (nil, let build?):
            return build
        case (nil, nil):
            return "unknown"
        }
    }

    private static func nonEmpty(_ value: String) -> String? {
        value.isEmpty ? nil : value
    }
}
