import SwiftUI

/// Update panel (shared/spec/UPDATE.md), ported from taskly/brainfuel's
/// UpdateSheet: one phase machine driving check → offer → download →
/// install. A failed install leaves the running version untouched; the
/// download phase cannot be dismissed (no cancel support). The host
/// presents it as a sheet over the main window.
struct UpdatePanelView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        VStack(spacing: 16) {
            Image(systemName: "arrow.down.circle")
                .font(.system(size: 44))
                .foregroundStyle(Color.accentColor)

            switch model.updatePhase {
            case .checking:
                Text("Checking for updates…")
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                ProgressView()

            case .available:
                Text("Autarx \(model.updateAvailableVersion) is available")
                    .font(.system(size: 15, weight: .semibold))
                Text("Restart to download and install the new version.")
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                HStack(spacing: 12) {
                    Button("Cancel") { model.dismissUpdatePanel() }
                    Button("Restart and Update") { model.installUpdate() }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                }

            case .downloading:
                Text("Downloading update…")
                    .font(.system(size: 13))
                ProgressView(value: Double(model.updateProgressPercent), total: 100)
                    .frame(width: 240)
                Text("\(model.updateProgressPercent)%")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)

            case .upToDate:
                Text("Autarx is up to date")
                    .font(.system(size: 14, weight: .medium))
                Button("OK") { model.dismissUpdatePanel() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)

            case .failed:
                Text(model.updateErrorMessage)
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    // Failure text (URLs, manifest errors) must leave the
                    // screen with the user — selectable for a bug report.
                    .textSelection(.enabled)
                    .frame(maxWidth: 320)
                    .fixedSize(horizontal: false, vertical: true)
                HStack(spacing: 12) {
                    Button("Cancel") { model.dismissUpdatePanel() }
                    Button("Check Again") { model.checkForUpdates() }
                }

            case .idle:
                EmptyView()
            }
        }
        .padding(28)
        .frame(width: 360)
        .background(.regularMaterial)
    }
}
