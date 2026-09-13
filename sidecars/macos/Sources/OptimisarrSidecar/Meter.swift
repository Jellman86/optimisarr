import SwiftUI

/// A slim progress bar drawn from shapes rather than the stock `ProgressView`.
///
/// Two reasons. It sits better in a menu-bar popover, where the system bar is too tall and too
/// pale against a card; and being pure SwiftUI it renders offscreen, so `--render-menu` produces
/// a picture of the real thing instead of a placeholder where an AppKit control would be.
struct Meter: View {
    /// 0...1, or nil for work whose remaining time is genuinely unknown.
    var value: Double?
    var tint: Color

    private static let height: CGFloat = 5

    @State private var sweep = false

    var body: some View {
        GeometryReader { geometry in
            ZStack(alignment: .leading) {
                Capsule().fill(Color.primary.opacity(0.1))

                if let value {
                    Capsule()
                        .fill(tint)
                        .frame(width: max(Self.height, geometry.size.width * min(max(value, 0), 1)))
                        .animation(.easeOut(duration: 0.25), value: value)
                } else {
                    // Indeterminate: a short bar sweeping across says "running, no estimate",
                    // where a full bar would claim progress nobody has measured.
                    Capsule()
                        .fill(tint.opacity(0.85))
                        .frame(width: geometry.size.width * 0.3)
                        .offset(x: sweep ? geometry.size.width * 0.7 : 0)
                        .animation(
                            .easeInOut(duration: 1.1).repeatForever(autoreverses: true),
                            value: sweep)
                        .onAppear { sweep = true }
                }
            }
        }
        .frame(height: Self.height)
        .accessibilityValue(value.map { "\(Int(($0 * 100).rounded())) percent" } ?? "in progress")
    }
}
