import SwiftUI

struct ProfileServersView: View {
    let core: CoreClient
    let profileID: String
    let canPlay: Bool
    let refresh: Int

    private struct Entry: Decodable, Identifiable {
        let id: String
        let name: String
        let address: String
        let editable: Bool
    }

    private struct Edit: Identifiable {
        let id = UUID()
        let server: String?
        var name: String
        var address: String
    }

    @State private var entries: [Entry] = []
    @State private var editor: Edit?
    @State private var pendingRemoval: Entry?
    @State private var status = ""
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button(tr("Додати сервер"), systemImage: "plus") { editor = Edit(server: nil, name: "", address: "") }
                .disabled(busy || !canPlay)
            if entries.isEmpty && !busy { Text(tr("Серверів ще немає.")) }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
            ForEach(entries) { server in
                VStack(alignment: .leading, spacing: 5) {
                    Text(server.name).font(.headline)
                    Text(server.address).foregroundStyle(.secondary).textSelection(.enabled)
                    HStack {
                        Button(tr("Приєднатися")) { send("join_server", ["server": server.id]) }
                        if server.editable {
                            Button(tr("Редагувати")) {
                                editor = Edit(server: server.id, name: server.name, address: server.address)
                            }
                            Button(tr("Видалити")) { pendingRemoval = server }
                        }
                    }
                    .disabled(busy || !canPlay)
                }
            }
        }
        .task(id: refresh) { await run { try await load() } }
        .sheet(item: $editor) { entry in
            ServerEditor(name: entry.name, address: entry.address) { name, address in
                editor = nil
                var arguments = ["name": name, "address": address]
                arguments["server"] = entry.server
                send("save_server", arguments)
            }
        }
        .alert(tr("Видалити сервер?"), isPresented: Binding(
            get: { pendingRemoval != nil },
            set: { if !$0 { pendingRemoval = nil } }
        ), presenting: pendingRemoval) { entry in
            Button(tr("Видалити"), role: .destructive) { send("remove_server", ["server": entry.id]) }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: { Text($0.name) }
    }

    @MainActor private func load() async throws {
        entries = try await core.call("servers", ["id": profileID], as: [Entry].self)
    }

    private func send(_ method: String, _ arguments: [String: String]) {
        Task {
            await run {
                if method == "join_server" { status = tr("Готуємо Minecraft…") }
                var values = arguments
                values["id"] = profileID
                _ = try await core.call(method, values, as: EmptyReply.self)
                status = ""
                if method != "join_server" { try await load() }
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

private struct ServerEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State var name: String
    @State var address: String
    let save: (String, String) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(tr("Сервер Minecraft")).font(.title2.bold())
            Form {
                TextField(tr("Назва"), text: $name)
                TextField(tr("Адреса"), text: $address)
            }
            HStack {
                Spacer()
                Button(tr("Скасувати")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(tr("Зберегти")) { save(name, address) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                              address.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .padding(24)
        .frame(width: 420)
    }
}
