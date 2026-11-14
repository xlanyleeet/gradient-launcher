import SwiftUI

struct ProfileGroupsView: View {
    let core: CoreClient
    let profileID: String

    private struct Group: Decodable, Identifiable {
        let id: String
        let name: String
        let selected: Bool?
    }

    private struct Save: Encodable {
        let method = "set_groups"
        let id: String
        let groups: [String]
    }

    @Environment(\.dismiss) private var dismiss
    @State private var groups: [Group] = []
    @State private var selected: Set<String> = []
    @State private var name = ""
    @State private var status = ""
    @State private var busy = true
    @State private var loaded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Групи профілю").font(.title2.bold())
            List(groups) { group in
                Toggle(group.name, isOn: Binding(
                    get: { selected.contains(group.id) },
                    set: { if $0 { selected.insert(group.id) } else { selected.remove(group.id) } }
                ))
            }
            .frame(minHeight: 180)
            TextField("Нова група", text: $name)
            if !status.isEmpty { Text(status) }
            HStack {
                Spacer()
                Button("Скасувати") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Зберегти") { Task { await save() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!loaded || busy)
            }
        }
        .padding(24)
        .frame(width: 460)
        .task {
            defer { busy = false }
            do {
                groups = try await core.call("groups", ["id": profileID], as: [Group].self)
                selected = Set(groups.filter { $0.selected == true }.map(\.id))
                loaded = true
            } catch { status = error.localizedDescription }
        }
    }

    @MainActor private func save() async {
        guard loaded, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            if !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                let group = try await core.call("create_group", ["name": name], as: Group.self)
                groups.append(group)
                selected.insert(group.id)
                name = ""
            }
            _ = try await core.send(Save(id: profileID, groups: selected.sorted()), as: EmptyReply.self)
            dismiss()
        } catch { status = error.localizedDescription }
    }
}
