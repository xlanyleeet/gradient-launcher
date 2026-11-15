import SwiftUI

struct ModVersionView: View {
    struct Target: Identifiable {
        let profileID: String
        let path: String
        let name: String
        let version: String?
        var id: String { path }
    }

    private struct Choice: Decodable, Identifiable {
        let id: String
        let name: String
        let channel: String
    }

    private struct Versions: Decodable {
        let current_id: String?
        let options: [Choice]
    }

    let core: CoreClient
    let target: Target
    let changed: () -> Void

    @Environment(\.dismiss) private var dismiss
    @State private var versions: Versions?
    @State private var choice = ""
    @State private var status = ""
    @State private var busy = true

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(target.name).font(.title2.bold())
            Text("Встановлено · \(target.version ?? "—")")
            if let versions, !versions.options.isEmpty {
                Picker("Версія", selection: $choice) {
                    ForEach(versions.options) { option in
                        Text("\(option.name) · \(option.channel)").tag(option.id)
                    }
                }
                .disabled(busy)
            } else if versions != nil {
                Text("Сумісних версій немає.")
            }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
            HStack {
                Spacer()
                Button("Закрити") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                    .disabled(busy && versions != nil)
                Button("Змінити версію") { Task { await install() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(busy || choice.isEmpty || choice == versions?.current_id)
            }
        }
        .padding(24)
        .frame(width: 460)
        .task { await load() }
    }

    @MainActor private func load() async {
        defer { busy = false }
        do {
            let loaded = try await core.call("installed_mod_versions", [
                "id": target.profileID, "path": target.path
            ], as: Versions.self)
            versions = loaded
            choice = loaded.options.first(where: { $0.id == loaded.current_id })?.id
                ?? loaded.options.first?.id ?? ""
        } catch { status = error.localizedDescription }
    }

    @MainActor private func install() async {
        guard !busy, !choice.isEmpty, choice != versions?.current_id else { return }
        busy = true
        defer { busy = false }
        do {
            _ = try await core.call("switch_mod_version", [
                "id": target.profileID, "path": target.path, "version": choice
            ], as: EmptyReply.self)
            changed()
            dismiss()
        } catch { status = error.localizedDescription }
    }
}
