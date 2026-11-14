import SwiftUI

struct ProfileLaunchSettingsView: View {
    let core: CoreClient
    let profileID: String
    let saved: () -> Void

    private struct Values: Decodable {
        let memory: String?
        let java: String?
    }

    @Environment(\.dismiss) private var dismiss
    @State private var memory = ""
    @State private var java = ""
    @State private var status = ""
    @State private var busy = true
    @State private var loaded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Java та пам’ять профілю").font(.title2.bold())
            Form {
                TextField("Пам’ять (МіБ)", text: $memory, prompt: Text("З налаштувань лаунчера"))
                TextField("Шлях до Java", text: $java, prompt: Text("Автоматично"))
            }
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
                let values = try await core.call("profile_launch_settings", ["id": profileID], as: Values.self)
                memory = values.memory ?? ""
                java = values.java ?? ""
                loaded = true
            } catch { status = error.localizedDescription }
        }
    }

    @MainActor private func save() async {
        guard loaded, !busy else { return }
        busy = true
        defer { busy = false }
        do {
            _ = try await core.call("save_profile_launch_settings", [
                "id": profileID, "memory": memory, "java": java
            ], as: EmptyReply.self)
            saved()
            dismiss()
        } catch { status = error.localizedDescription }
    }
}
