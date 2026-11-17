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
    @State private var image: NSImage?
    @State private var draftURL: URL?
    @State private var draftReady = false

    private struct Preview: Decodable {
        let preview: Data
        let variant: String
    }

    var body: some View {
        Form {
            Text(tr("Скін активного Minecraft-акаунта")).font(.title2.bold())
            if let image {
                Image(nsImage: image).resizable().interpolation(.none).scaledToFit()
                    .frame(height: 180)
                    .frame(maxWidth: .infinity)
                    .accessibilityLabel(Text(tr("Передперегляд скіна")))
            }
            Picker(tr("Модель"), selection: $variant) {
                Text(tr("Класична")).tag("classic")
                Text(tr("Тонкі руки")).tag("slim")
            }
            .disabled(busy)
            HStack {
                Button(tr("Вибрати PNG…"), action: selectSkin).disabled(busy)
                Button(tr("Застосувати")) {
                    guard let draftURL else { return }
                    Task {
                        await apply("upload_skin", ["path": draftURL.path, "variant": variant],
                                    success: tr("Скін Minecraft застосовано."))
                    }
                }
                .disabled(busy || !draftReady)
                Button(tr("Скинути скін і плащ"), role: .destructive) { confirmReset = true }
                    .disabled(busy)
            }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
            Button(tr("Закрити")) { dismiss() }.keyboardShortcut(.cancelAction)
        }
        .padding(24)
        .frame(width: 470)
        .task { await loadCurrent() }
        .onChange(of: variant) { if draftURL != nil { Task { await loadDraft() } } }
        .confirmationDialog(tr("Скинути скін і плащ?"), isPresented: $confirmReset) {
            Button(tr("Скинути"), role: .destructive) {
                Task { await apply("reset_skin", [:], success: tr("Скін Minecraft скинуто.")) }
            }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: {
            Text(tr("Minecraft повернеться до стандартного скіна."))
        }
    }

    private func selectSkin() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.png]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        draftURL = url
        draftReady = false
        Task { await loadDraft() }
    }

    @MainActor private func loadCurrent() async {
        busy = true
        defer { busy = false }
        do { try await refreshCurrent() }
        catch { status = error.localizedDescription }
    }

    @MainActor private func refreshCurrent() async throws {
        let active = try await core.call("current_skin_preview", as: Preview?.self)
        image = active.flatMap { NSImage(data: $0.preview) }
        if let active { variant = active.variant }
    }

    @MainActor private func loadDraft() async {
        guard !busy, let draftURL else { return }
        busy = true
        draftReady = false
        defer { busy = false }
        do {
            let preview = try await core.call("preview_skin", ["path": draftURL.path, "variant": variant], as: Preview.self)
            image = NSImage(data: preview.preview)
            draftReady = image != nil
            status = draftURL.lastPathComponent
        } catch {
            image = nil
            status = error.localizedDescription
        }
    }

    @MainActor private func apply(_ method: String, _ arguments: [String: String], success: String) async {
        guard !busy else { return }
        busy = true
        status = ""
        defer { busy = false }
        do {
            _ = try await core.call(method, arguments, as: EmptyReply.self)
            draftURL = nil
            draftReady = false
            try await refreshCurrent()
            status = success
        } catch { status = error.localizedDescription }
    }
}
