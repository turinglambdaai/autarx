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
    var summary: WorkspaceSummary?
    var objects: [SemanticObjectDto] = []
    var ecus: [SemanticObjectDto] = []
    var unresolved: [ReferenceDto] = []
    var comm: CommunicationDto?
    var searchText = ""
    var selectedSection: Section = .workspace

    private(set) var api: RivetAPI?
    private var backend: EmbeddedRacketBackend?
    private var started = false

    var isConnected: Bool { summary != nil }

    func start() {
        guard !started else { return }
        started = true
        do {
            let config = try Self.runtimeConfiguration()
            let backend = EmbeddedRacketBackend(configuration: config)
            self.backend = backend
            try backend.start(onEvent: { [weak self] name, value in
                guard let event = try? RivetEvent.decode(name: name, value: value) else { return }
                Swift.Task { @MainActor [weak self] in
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
                Swift.Task { await openWorkspace(at: url) }
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
    func refs(for path: String) async -> [ReferenceDto] {
        guard let api else { return [] }
        return (try? await api.get_refs(name_or_path: path)) ?? []
    }

    @MainActor
    func trace(from path: String, depth: Int64) async -> TraceDto? {
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
