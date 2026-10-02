import SwiftUI

/// Sidebar + detail workbench. Everything renders through the RPC surface;
/// no ARXML is parsed on the Swift side.
struct MainWindowView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        NavigationSplitView {
            List(AppModel.Section.allCases) { section in
                Button {
                    model.selectedSection = section
                } label: {
                    Label(section.rawValue, systemImage: icon(for: section))
                }
                .buttonStyle(.plain)
            }
            .navigationTitle("Autarx")
            .frame(minWidth: 190)
        } detail: {
            switch model.selectedSection {
            case .workspace: WorkspaceSummaryView()
            case .objects: ObjectListView()
            case .ecus: EcuListView()
            case .unresolved: UnresolvedView()
            case .comm: CommunicationView()
            }
        }
        .overlay(alignment: .bottom) {
            if let progress = model.progressLabel {
                Label(progress, systemImage: "hourglass")
                    .padding(8)
                    .background(.bar)
                    .cornerRadius(8)
                    .padding(.bottom, 8)
            }
        }
    }

    private func icon(for section: AppModel.Section) -> String {
        switch section {
        case .workspace: return "folder"
        case .objects: return "square.stack.3d.up"
        case .ecus: return "cpu"
        case .unresolved: return "exclamationmark.triangle"
        case .comm: return "antenna.radiowaves.left.and.right"
        }
    }
}

struct WorkspaceSummaryView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let s = model.summary {
                List(summaryRows(s), id: \.0) { row in
                    HStack {
                        Text(row.0).foregroundStyle(.secondary)
                        Spacer()
                        Text(row.1).textSelection(.enabled)
                    }
                }
            } else {
                ContentUnavailableView(
                    "No workspace",
                    systemImage: "shippingbox",
                    description: Text("Open an OEM delivery (⌘O) to index its ARXML."))
            }
        }
        .navigationTitle("Workspace")
    }

    private func summaryRows(_ s: WorkspaceSummary) -> [(String, String)] {
        [
            ("Source", s.source_path),
            ("Files", "\(s.file_count)"),
            ("Identifiables", "\(s.identifiable_count)"),
            ("Packages", "\(s.package_count)"),
            ("References", "\(s.reference_count)"),
            ("Unresolved", "\(s.unresolved_count)"),
            ("Duplicate paths", "\(s.duplicate_count)"),
            ("File errors", "\(s.file_error_count)"),
            ("AUTOSAR release", s.autosar_release ?? "unknown"),
            ("Schema", s.mixed_schema ? "mixed flavours!" : "uniform"),
        ]
    }
}

struct ObjectListView: View {
    @Environment(AppModel.self) private var model
    @State private var filterText = ""
    @State private var selectedPath: String?
    @State private var selectedObject: SemanticObjectDto?
    @State private var detail: [ReferenceDto] = []

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "magnifyingglass")
                TextField("Filter by SHORT-NAME", text: $filterText)
                    .onSubmit { runSearch() }
                Button("Find") { runSearch() }
            }
            .padding(8)

            List(model.objects, id: \.absolute_path) { object in
                ObjectRow(object: object)
                    .contentShape(Rectangle())
                    .onTapGesture {
                        selectedObject = object
                        selectedPath = object.absolute_path
                        Task { detail = await model.refs(for: object.absolute_path) }
                    }
                    .listRowBackground(
                        object.absolute_path == selectedPath
                            ? Color.accentColor.opacity(0.15) : Color.clear)
            }
        }
        .inspector(isPresented: .constant(selectedObject != nil)) {
            RefInspector(object: selectedObject, refs: detail)
        }
        .navigationTitle("Objects")
    }

    private func runSearch() {
        model.searchText = filterText
        Task { await model.search() }
    }
}

struct ObjectRow: View {
    let object: SemanticObjectDto

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(object.short_name).bold()
                Text(object.semantic_kind)
                    .font(.caption2)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 1)
                    .background(Color.accentColor.opacity(0.2))
                    .cornerRadius(4)
                if object.unresolved_references > 0 {
                    Image(systemName: "exclamationmark.triangle")
                        .foregroundStyle(.orange)
                        .help("\(object.unresolved_references) unresolved outgoing references")
                }
            }
            Text(object.absolute_path)
                .font(.system(.caption, design: .monospaced))
                .foregroundStyle(.secondary)
        }
        .padding(.vertical, 2)
    }
}

struct RefInspector: View {
    let object: SemanticObjectDto?
    let refs: [ReferenceDto]

    var body: some View {
        List {
            if let object {
                Section(object.short_name) {
                    Text("\(object.element_type) · \(object.semantic_kind)")
                        .font(.caption)
                    Text(object.absolute_path)
                        .font(.system(.caption, design: .monospaced))
                }
            }
            Section("References") {
                ForEach(Array(refs.enumerated()), id: \.offset) { _, r in
                    VStack(alignment: .leading, spacing: 2) {
                        Text(r.kind).font(.caption.bold())
                        Text("\(r.source_path) → \(r.target_path)")
                            .font(.system(.caption, design: .monospaced))
                            .lineLimit(2)
                        if !r.resolved {
                            Text("unresolved").foregroundStyle(.orange)
                                .font(.caption2)
                        }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
        .inspectorColumnWidth(min: 260, ideal: 320, max: 420)
    }
}

struct EcuListView: View {
    @Environment(AppModel.self) private var model
    @State private var selectedObject: SemanticObjectDto?
    @State private var refs: [ReferenceDto] = []

    var body: some View {
        List(model.ecus, id: \.absolute_path) { ecu in
            ObjectRow(object: ecu)
                .contentShape(Rectangle())
                .onTapGesture {
                    selectedObject = ecu
                    Task { refs = await model.refs(for: ecu.absolute_path) }
                }
        }
        .inspector(isPresented: .constant(selectedObject != nil)) {
            RefInspector(object: selectedObject, refs: refs)
        }
        .navigationTitle("ECU instances")
    }
}

struct UnresolvedView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if model.unresolved.isEmpty {
                ContentUnavailableView("No unresolved references",
                                       systemImage: "checkmark.seal")
            } else {
                List {
                    ForEach(Array(model.unresolved.enumerated()), id: \.offset) { _, r in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(r.source_element_path)
                                .font(.system(.caption, design: .monospaced))
                            Text("-[\(r.kind)]-> \(r.target_path)")
                                .font(.system(.caption2, design: .monospaced))
                                .foregroundStyle(.secondary)
                        }
                        .padding(.vertical, 2)
                    }
                }
            }
        }
        .navigationTitle("Unresolved references")
    }
}

struct CommunicationView: View {
    @Environment(AppModel.self) private var model

    var body: some View {
        Group {
            if let comm = model.comm {
                List {
                    Section("Clusters") {
                        ForEach(comm.clusters, id: \.path) { cluster in
                            VStack(alignment: .leading) {
                                Text(cluster.path).font(.headline.monospaced())
                                if !cluster.connected_ecus.isEmpty {
                                    Text("ECUs: " + cluster.connected_ecus.joined(separator: ", "))
                                        .font(.caption)
                                }
                                ForEach(cluster.frames, id: \.self) { frame in
                                    Text("frame \(frame)")
                                        .font(.system(.caption, design: .monospaced))
                                }
                            }
                            .padding(.vertical, 2)
                        }
                    }
                    Section("Orphans") {
                        ForEach(comm.orphan_frames, id: \.self) {
                            Text("frame \($0) — not attached to any cluster")
                                .font(.system(.caption, design: .monospaced))
                        }
                        ForEach(comm.orphan_pdus, id: \.self) {
                            Text("pdu \($0) — not attached to any frame")
                                .font(.system(.caption, design: .monospaced))
                        }
                        ForEach(comm.orphan_signals, id: \.self) {
                            Text("signal \($0) — not attached to any PDU")
                                .font(.system(.caption, design: .monospaced))
                        }
                    }
                }
            } else {
                ContentUnavailableView("No communication content",
                                       systemImage: "antenna.radiowaves.left.and.right.slash")
            }
        }
        .navigationTitle("Communication")
    }
}
