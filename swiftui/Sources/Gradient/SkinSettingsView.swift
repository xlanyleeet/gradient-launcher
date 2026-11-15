import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct SkinSettingsView: View {
    let core: CoreClient
    @Environment(\.dismiss) private var dismiss
    @State private var variant = "classic"
    @State private var confirmReset = false
    @State private var busy = false
    @State private var status = ""

    var body: some View {
        Form {
            Text("Скін активного Minecraft-акаунта").font(.title2.bold())
            Picker("Модель", selection: $variant) {
                Text("Класична").tag("classic")
                Text("Тонкі руки").tag("slim")
            }
            .disabled(busy)
            HStack {
                Button("Вибрати PNG…", action: selectSkin).disabled(busy)
                Button("Скинути скін і плащ", role: .destructive) { confirmReset = true }
                    .disabled(busy)
            }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
            Button("Закрити") { dismiss() }.keyboardShortcut(.cancelAction)
        }
        .padding(24)
        .frame(width: 470)
        .confirmationDialog("Скинути скін і плащ?", isPresented: $confirmReset) {
            Button("Скинути", role: .destructive) {
                Task { await apply("reset_skin", [:], success: "Скін Minecraft скинуто.") }
            }
            Button("Скасувати", role: .cancel) {}
        } message: {
            Text("Minecraft повернеться до стандартного скіна.")
        }
    }

    private func selectSkin() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task {
            await apply("upload_skin", ["path": url.path, "variant": variant],
                        success: "Скін Minecraft застосовано.")
        }
    }

    @MainActor private func apply(_ method: String, _ arguments: [String: String], success: String) async {
        guard !busy else { return }
        busy = true
        status = ""
        defer { busy = false }
        do {
            _ = try await core.call(method, arguments, as: EmptyReply.self)
            status = success
        } catch { status = error.localizedDescription }
    }
}
