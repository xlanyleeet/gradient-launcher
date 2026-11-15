import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct ProfileTransferView: View {
    enum Operation: Identifiable {
        case export(String)
        case importLauncher

        var id: String {
            switch self {
            case let .export(id): return "export/\(id)"
            case .importLauncher: return "import"
            }
        }
    }

    let core: CoreClient
    let operation: Operation
    let completed: (String) -> Void

    private struct Candidate: Decodable, Identifiable {
        let path: String
        let disabled: Bool
        let defaultSelected: Bool
        var id: String { path }
    }

    private struct ExportRequest: Encodable {
        let method = "export_profile"
        let id: String
        let path: String
        let included: [String]
    }

    @Environment(\.dismiss) private var dismiss
    @State private var candidates: [Candidate] = []
    @State private var included: Set<String> = []
    @State private var exportReady = false
    @State private var launcher = "PrismLauncher"
    @State private var base: URL?
    @State private var folders: [String] = []
    @State private var folder = ""
    @State private var busy = false
    @State private var status = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            switch operation {
            case let .export(id):
                Text(tr("Експорт модпаку")).font(.title2.bold())
                List(candidates) { candidate in
                    Toggle(candidate.path, isOn: Binding(
                        get: { included.contains(candidate.path) },
                        set: { if $0 { included.insert(candidate.path) } else { included.remove(candidate.path) } }
                    ))
                    .disabled(candidate.disabled || busy)
                }
                .frame(minHeight: 260)
                controls(tr("Експортувати"), disabled: !exportReady) { saveExport(id) }
            case .importLauncher:
                Text(tr("Імпорт з іншого лаунчера")).font(.title2.bold())
                Picker(tr("Лаунчер"), selection: $launcher) {
                    ForEach(["PrismLauncher", "MultiMC", "ATLauncher", "GDLauncher", "Curseforge"], id: \.self) {
                        Text($0).tag($0)
                    }
                }
                .disabled(busy)
                .onChange(of: launcher) { base = nil; folders = []; folder = ""; status = "" }
                Button(tr("Вибрати папку лаунчера…"), action: chooseFolder).disabled(busy)
                if !folders.isEmpty {
                    Picker(tr("Профіль"), selection: $folder) {
                        ForEach(folders, id: \.self) { Text($0).tag($0) }
                    }
                }
                controls(tr("Імпортувати"), disabled: folder.isEmpty) { importProfile() }
            }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
        }
        .padding(24)
        .frame(width: 500)
        .task {
            guard case let .export(id) = operation else { return }
            await run {
                candidates = try await core.call("export_candidates", ["id": id], as: [Candidate].self)
                included = Set(candidates.filter { !$0.disabled && $0.defaultSelected }.map(\.path))
                exportReady = true
            }
        }
    }

    private func controls(_ title: String, disabled: Bool, action: @escaping () -> Void) -> some View {
        HStack {
            Spacer()
            Button(tr("Скасувати")) { dismiss() }.keyboardShortcut(.cancelAction).disabled(busy)
            Button(title, action: action).keyboardShortcut(.defaultAction).disabled(disabled || busy)
        }
    }

    private func saveExport(_ id: String) {
        let panel = NSSavePanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "mrpack") ?? .data]
        panel.nameFieldStringValue = "modpack.mrpack"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task {
            await run {
                let request = ExportRequest(id: id, path: url.path, included: included.sorted())
                _ = try await core.send(request, as: EmptyReply.self)
                completed(tr("Модпак експортовано."))
                dismiss()
            }
        }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task {
            await run {
                base = nil
                folders = []
                folder = ""
                folders = try await core.call("import_sources", ["launcher": launcher, "base": url.path], as: [String].self)
                base = url
                folder = folders.first ?? ""
                if folders.isEmpty { status = tr("У цій папці немає профілів для імпорту.") }
            }
        }
    }

    private func importProfile() {
        guard let base else { return }
        Task {
            await run {
                _ = try await core.call("import_profile", ["launcher": launcher, "base": base.path, "folder": folder], as: EmptyReply.self)
                completed(tr("Імпорт додано до черги."))
                dismiss()
            }
        }
    }

    @MainActor private func run(_ action: () async throws -> Void) async {
        guard !busy else { return }
        busy = true
        status = ""
        defer { busy = false }
        do { try await action() } catch { status = error.localizedDescription }
    }
}
