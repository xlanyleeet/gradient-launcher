import AppKit
import SwiftUI

struct ProfileFilesView: View {
    let core: CoreClient
    let profileID: String
    let refresh: Int
    let editableProfile: Bool

    private struct Entry: Decodable, Identifiable {
        let name: String
        let path: String
        let type: String
        let readOnly: Bool
        var id: String { path }
    }

    @State private var entries: [Entry] = []
    @State private var directory = ""
    @State private var selected: Entry?
    @State private var preview = ""
    @State private var original: String?
    @State private var editStatus = ""
    @State private var status = ""
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Button(tr("На рівень вище"), systemImage: "arrow.up") {
                    let parent = directory.split(separator: "/").dropLast().joined(separator: "/")
                    Task { await load(parent) }
                }
                .disabled(directory.isEmpty || busy)
                Text("/" + directory)
                    .textSelection(.enabled)
            }
            if busy { ProgressView().controlSize(.small) }
            if entries.isEmpty && !busy && status.isEmpty { Text(tr("Порожня папка.")) }
            if !status.isEmpty { Text(status) }
            ForEach(entries) { entry in
                Button {
                    Task { await open(entry) }
                } label: {
                    Label(entry.name, systemImage: entry.type == "directory" ? "folder" : "doc")
                }
                .buttonStyle(.borderless)
                .disabled(busy || entry.type == "symlink")
            }
        }
        .task(id: refresh) { await load(directory) }
        .sheet(item: $selected) { entry in
            VStack(alignment: .leading, spacing: 12) {
                Text(entry.name).font(.headline)
                if original != nil && !entry.readOnly && editableProfile {
                    TextEditor(text: $preview)
                        .font(.system(.body, design: .monospaced))
                        .frame(height: 260)
                        .accessibilityLabel(Text(tr("Вміст файлу")))
                } else {
                    ScrollView {
                        Text(preview).font(.system(.body, design: .monospaced)).textSelection(.enabled)
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }
                    .frame(minHeight: 200, maxHeight: 380)
                }
                if !editStatus.isEmpty { Text(editStatus) }
                HStack {
                    Spacer()
                    Button(tr("Закрити")) { selected = nil }.keyboardShortcut(.cancelAction)
                    Button(tr("Зберегти копію")) { save(entry) }.disabled(busy)
                    if let original, !entry.readOnly && editableProfile {
                        Button(tr("Зберегти зміни")) { saveText(entry, before: original) }
                            .keyboardShortcut(.defaultAction)
                            .disabled(busy || preview == original)
                    }
                }
            }
            .padding(24)
            .frame(width: 540)
        }
    }

    @MainActor private func load(_ path: String) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do {
            entries = try await core.call("files", ["id": profileID, "path": path], as: [Entry].self)
            directory = path
            status = ""
        } catch { status = error.localizedDescription }
    }

    @MainActor private func open(_ entry: Entry) async {
        if entry.type == "directory" {
            await load(entry.path)
            return
        }
        guard !busy, entry.type == "file" else { return }
        busy = true
        defer { busy = false }
        original = nil
        editStatus = ""
        do {
            preview = try await core.call("preview_file", ["id": profileID, "path": entry.path], as: String.self)
            original = preview
        } catch { preview = error.localizedDescription }
        selected = entry
    }

    private func save(_ entry: Entry) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = entry.name
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        Task {
            busy = true
            defer { busy = false }
            do {
                _ = try await core.call("copy_file", ["id": profileID, "path": entry.path,
                                                     "destination": destination.path], as: EmptyReply.self)
                selected = nil
                status = tr("Копію файлу збережено.")
            } catch { editStatus = error.localizedDescription }
        }
    }

    private func saveText(_ entry: Entry, before: String) {
        Task {
            busy = true
            defer { busy = false }
            do {
                _ = try await core.call("save_profile_text", [
                    "id": profileID, "path": entry.path, "before": before, "text": preview
                ], as: EmptyReply.self)
                selected = nil
                status = tr("Файл збережено.")
            } catch { editStatus = error.localizedDescription }
        }
    }
}
