import SwiftUI

/// About panel (Help ▸ About): `Autarx v<version>`, the product site and
/// the issue tracker. Autarx is proprietary — the license line states
/// that rather than promising an opensource license link.
struct AboutPanelView: View {
    @Environment(\.dismiss) private var dismiss

    private var version: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
            ?? RivetGeneratedConfig.version
    }

    var body: some View {
        VStack(spacing: 12) {
            Image(systemName: "car")
                .font(.system(size: 44))
                .foregroundStyle(Color.accentColor)
            Text("Autarx v\(version)")
                .font(.system(size: 15, weight: .semibold))
            Text("AUTOSAR Integration Workbench")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
            Text("© 2026 TuringLambdaAI")
                .font(.system(size: 12))
                .foregroundStyle(.secondary)
            Text("Proprietary — all rights reserved")
                .font(.system(size: 11))
                .foregroundStyle(.secondary)
            HStack(spacing: 20) {
                Link("Website",
                     destination: URL(string: "https://autarx.jrtx.site")!)
                Link("Report an Issue",
                     destination: URL(string: "https://github.com/turinglambdaai/autarx/issues")!)
            }
            .font(.system(size: 12))
            Button("OK") { dismiss() }
                .keyboardShortcut(.defaultAction)
                .buttonStyle(.borderedProminent)
        }
        .padding(28)
        .frame(width: 320)
        .background(.regularMaterial)
    }
}
