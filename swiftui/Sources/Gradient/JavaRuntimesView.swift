import AppKit
import SwiftUI

struct JavaRuntimesView: View {
    let core: CoreClient
    @Environment(\.dismiss) private var dismiss

    private struct Runtime: Decodable {
        let parsed_version: UInt32
        let version: String
        let path: String
    }

    @State private var runtimes: [Runtime] = []
    @State private var major = "21"
    @State private var busy = false
    @State private var status = ""

    var body: some View {
        Form {
            Picker("Версія Java", selection: $major) {
                ForEach(["8", "17", "21", "25"], id: \.self) { Text($0).tag($0) }
            }
            .disabled(busy)
            if let runtime = runtimes.first(where: { String($0.parsed_version) == major }) {
                Text("\(runtime.version) · \(runtime.path)").textSelection(.enabled)
            } else {
                Text("Для цієї версії шлях ще не збережено.").foregroundStyle(.secondary)
            }
            HStack {
                Button("Вибрати файл…", action: chooseFile).disabled(busy)
                Button("Завантажити") {
                    Task { await change("download_java_runtime", ["major": major]) }
                }
                .disabled(busy)
                Spacer()
                Button("Готово") { dismiss() }.keyboardShortcut(.cancelAction)
            }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
        }
        .padding(24)
        .frame(width: 500)
        .task {
            await run { try await refresh() }
        }
    }

    private func chooseFile() {
        let panel = NSOpenPanel()
        panel.message = "Виберіть виконуваний файл java всередині папки bin."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task { await change("set_java_runtime", ["major": major, "path": url.path]) }
    }

    @MainActor private func change(_ method: String, _ values: [String: String]) async {
        await run {
            _ = try await core.call(method, values, as: EmptyReply.self)
            try await refresh()
            status = "Java \(major) збережено."
        }
    }

    @MainActor private func refresh() async throws {
        runtimes = try await core.call("java_runtimes", as: [Runtime].self)
    }

    @MainActor private func run(_ action: () async throws -> Void) async {
        guard !busy else { return }
        busy = true
        status = ""
        defer { busy = false }
        do { try await action() } catch { status = error.localizedDescription }
    }
}
