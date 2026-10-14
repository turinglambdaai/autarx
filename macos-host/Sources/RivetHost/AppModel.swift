import Foundation
import Observation
import RivetEmbedding

/// Autarx workbench model: one embedded Racket backend, one open workspace.
@Observable
final class AppModel: @unchecked Sendable {
    enum Section: String, CaseIterable, Identifiable {
        case workspace = "Workspace"
        case objects = "Objects"
        case ecus = "ECUs"
        case unresolved = "Unresolved"
        case comm = "Communication"
        var id: String { rawValue }
    }

    var statusMessage = "Ready"
    var progressLabel: String?
    var summary: RivetTypes.WorkspaceSummary?
    var objects: [RivetTypes.SemanticObjectDto] = []
    var ecus: [RivetTypes.SemanticObjectDto] = []
    var unresolved: [RivetTypes.ReferenceDto] = []
    var comm: RivetTypes.CommunicationDto?
    var searchText = ""
    var selectedSection: Section = .workspace

    // MARK: - Updates (shared/spec/UPDATE.md)

    enum UpdatePhase: Equatable {
        case idle, checking, available, downloading, upToDate, failed
    }

    var updatePhase: UpdatePhase = .idle
    var updateProgressPercent = 0
    var updateAvailableVersion = ""
    var updateErrorMessage = ""
    var updatePanelVisible = false
    var aboutVisible = false

    private var pendingUpdateManifest: UpdateService.Manifest?

    /// Silent launch checks throttle to one attempt per 4 h (UserDefaults;
    /// unix epoch seconds — a GUI-only concern, no backend round-trip).
    static let updateThrottleInterval: TimeInterval = 4 * 60 * 60
    private static let lastUpdateCheckKey = "last-update-check"

    private(set) var api: RivetAPI?
    private var backend: EmbeddedRacketBackend?
    private var started = false

    var isConnected: Bool { summary != nil }

    /// Manual entry point: menu "Check for Updates…". The panel reports
    /// every outcome (up to date, offer, failure, dev copy).
    @MainActor
    func checkForUpdates() {
        guard updatePhase != .downloading else { return }
        updatePanelVisible = true
        updatePhase = .checking
        Task { await runUpdateCheck(present: true) }
    }

    /// Silent launch check: once shortly after startup, throttled to one
    /// attempt per 4 h; failures never nag.
    func autoCheckForUpdates() {
        Task {
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            await runUpdateCheck(present: false)
        }
    }

    @MainActor
    private func runUpdateCheck(present: Bool) async {
        guard updatePhase != .downloading else { return }
        if !present {
            guard Self.updateThrottleElapsed() else { return }
        }
        do {
            // Constructed per call: the service only carries a URLSession,
            // and a @MainActor class instance must be built on the actor.
            let result = try await UpdateService().check()
            Self.recordUpdateCheck()
            switch result {
            case .available(let manifest):
                pendingUpdateManifest = manifest
                updateAvailableVersion = manifest.version
                updatePhase = .available
                updatePanelVisible = true
            case .upToDate:
                updatePhase = present ? .upToDate : .idle
            }
        } catch {
            Self.recordUpdateCheck()
            guard present else {
                updatePhase = .idle
                return
            }
            if case UpdateService.UpdateError.notInstalled = error {
                updateErrorMessage = "This copy is not an installed app — download the DMG from the releases page."
            } else {
                updateErrorMessage = "Update check failed: \(Self.cleanError(error))"
            }
            updatePhase = .failed
            updatePanelVisible = true
        }
    }

    private static func updateThrottleElapsed() -> Bool {
        let last = UserDefaults.standard.double(forKey: lastUpdateCheckKey)
        return Date().timeIntervalSince1970 - last >= updateThrottleInterval
    }

    private static func recordUpdateCheck() {
        UserDefaults.standard.set(
            Date().timeIntervalSince1970, forKey: lastUpdateCheckKey)
    }

    /// Offer accepted: download (progress in the panel) → sha256 + Ed25519
    /// already verified → in-place swap → relaunch. UpdateService terminates
    /// the app on success; any earlier throw leaves this version running.
    @MainActor
    func installUpdate() {
        guard updatePhase == .available, let manifest = pendingUpdateManifest else { return }
        updatePhase = .downloading
        updateProgressPercent = 0
        updateErrorMessage = ""
        Task { [weak self] in
            guard let self else { return }
            do {
                try await UpdateService().downloadAndInstall(manifest) { [weak self] percent in
                    // The download runs off the main actor; hop the write
                    // back so SwiftUI sees it.
                    Task { @MainActor [weak self] in
                        self?.updateProgressPercent = percent
                    }
                }
            } catch {
                await MainActor.run {
                    self.updateErrorMessage = "Update failed: \(Self.cleanError(error))"
                    self.updatePhase = .failed
                }
            }
        }
    }

    @MainActor
    func dismissUpdatePanel() {
        updatePanelVisible = false
        if updatePhase != .downloading {
            updatePhase = .idle
            updateErrorMessage = ""
            pendingUpdateManifest = nil
        }
    }

    static func cleanError(_ error: Error) -> String {
        let text = error.localizedDescription
        // RVT1 failures arrive as "...error: <message>"; keep the message.
        if let range = text.range(of: "error: ") {
            return String(text[range.upperBound...])
        }
        return text
    }

    func start() {
        guard !started else { return }
        started = true
        do {
            let config = try Self.runtimeConfiguration()
            let backend = EmbeddedRacketBackend(configuration: config)
            self.backend = backend
            try backend.start(onEvent: { [weak self] name, value in
                guard let event = try? RivetEvent.decode(name: name, value: value) else { return }
                Task { @MainActor [weak self] in
                    switch event {
                    case .progress(let payload):
                        guard case .string(let text) = payload else { return }
                        self?.progressLabel = text.hasSuffix("100%") ? nil : text
                    }
                }
            })
            api = RivetAPI(client: backend.client)
            statusMessage = "Backend ready — open a delivery to begin"
            // AUTARX_OPEN=<path> pre-opens a workspace (dev convenience)
            if let preset = ProcessInfo.processInfo.environment["AUTARX_OPEN"], !preset.isEmpty {
                let url = URL(fileURLWithPath: preset)
                Task { await openWorkspace(at: url) }
            }
        } catch {
            statusMessage = "Backend error: \(error)"
        }
    }

    // MARK: - Workspace

    @MainActor
    func openWorkspace(at url: URL) async {
        guard let api else { return }
        do {
            let s = try await api.open_workspace(path: url.path)
            summary = s
            selectedSection = .workspace
            statusMessage = "\(s.source_path) — \(s.identifiable_count) objects, \(s.unresolved_count) unresolved"
            await reloadAll()
        } catch {
            statusMessage = "Open failed: \(error)"
        }
    }

    @MainActor
    func closeWorkspace() async {
        guard let api else { return }
        _ = try? await api.close_workspace()
        summary = nil
        objects = []
        ecus = []
        unresolved = []
        comm = nil
        statusMessage = "Workspace closed"
    }

    @MainActor
    func reloadAll() async {
        guard let api, isConnected else { return }
        objects = (try? await api.list_objects(kind: "all")) ?? []
        ecus = (try? await api.list_ecus()) ?? []
        unresolved = (try? await api.unresolved_references()) ?? []
        comm = try? await api.communication()
    }

    @MainActor
    func search() async {
        guard let api, isConnected, !searchText.isEmpty else {
            await reloadAll()
            return
        }
        objects = (try? await api.find_objects(pattern: searchText)) ?? []
    }

    // MARK: - Queries used by detail views

    @MainActor
    func refs(for path: String) async -> [RivetTypes.ReferenceDto] {
        guard let api else { return [] }
        return (try? await api.get_refs(name_or_path: path)) ?? []
    }

    @MainActor
    func trace(from path: String, depth: Int64) async -> RivetTypes.TraceDto? {
        guard let api else { return nil }
        return try? await api.trace_from(name_or_path: path, depth: depth)
    }

    // MARK: - Runtime layout (same staged/packaged discovery as taskly)

    private static func runtimeConfiguration() throws -> EmbeddedRacketConfiguration {
        let executable = Bundle.main.executableURL
            ?? URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL

        let roots = [
            Bundle.main.resourceURL,
            executable.deletingLastPathComponent()
        ].compactMap { $0 }

        for root in roots {
            let runtime = root.appendingPathComponent("runtime", isDirectory: true)
            let core = root.appendingPathComponent("res/core.zo")
            let required = [
                runtime.appendingPathComponent("petite.boot"),
                runtime.appendingPathComponent("scheme.boot"),
                runtime.appendingPathComponent("racket.boot"),
                core
            ]
            if required.allSatisfy({ FileManager.default.fileExists(atPath: $0.path) }) {
                return EmbeddedRacketConfiguration(
                    executable: executable,
                    petiteBoot: required[0],
                    schemeBoot: required[1],
                    racketBoot: required[2],
                    core: core,
                    moduleName: RivetGeneratedConfig.moduleName,
                    entryName: RivetGeneratedConfig.entryName
                )
            }
        }

        throw HostError.missingRuntimeLayout(
            roots.map(\.path).joined(separator: ", "))
    }
}

enum HostError: Error, CustomStringConvertible {
    case missingRuntimeLayout(String)

    var description: String {
        switch self {
        case .missingRuntimeLayout(let roots):
            return "missing Rivet runtime/res layout under: \(roots)"
        }
    }
}
