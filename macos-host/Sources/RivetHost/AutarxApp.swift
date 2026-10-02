import AppKit
import SwiftUI

/// Autarx macOS workbench: one SwiftUI window over the embedded Racket
/// backend. The tool sits above the vendor generators — inspection,
/// tracing and diffing only.
@main
struct AutarxApp: App {
    @State private var model = AppModel()

    var body: some Scene {
        WindowGroup("Autarx") {
            MainWindowView()
                .environment(model)
                .frame(minWidth: 860, idealWidth: 1180,
                       minHeight: 560, idealHeight: 780)
                .task { model.start() }
        }
        .defaultSize(width: 1180, height: 820)
        .commands {
            AutarxCommands(model: model)
            CommandGroup(replacing: .toolbar) {}
        }
    }
}

struct AutarxCommands: Commands {
    let model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("Open Delivery…") { openDelivery() }
                .keyboardShortcut("o", modifiers: [.command])
                .disabled(model.api == nil)
            Button("Close Workspace") { Task { await model.closeWorkspace() } }
                .disabled(!model.isConnected)
        }
    }

    private func openDelivery() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = true
        panel.allowedContentTypes = []
        panel.message = "Choose an OEM delivery directory or a single ARXML file"
        if panel.runModal() == .OK, let url = panel.url {
            Task { await model.openWorkspace(at: url) }
        }
    }
}
