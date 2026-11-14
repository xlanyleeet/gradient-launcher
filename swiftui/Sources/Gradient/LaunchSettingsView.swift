import SwiftUI

private struct LaunchSettings: Codable {
    var memory_mib: UInt32
    var fullscreen: Bool
    var width: UInt16
    var height: UInt16
    var arguments: [String]
}

private struct SaveLaunchSettings: Encodable {
    let method = "save_launch_settings"
    let values: LaunchSettings
}

struct LaunchSettingsView: View {
    let core: CoreClient

    @State private var memory = ""
    @State private var width = ""
    @State private var height = ""
    @State private var fullscreen = false
    @State private var arguments = ""
    @State private var loaded = false
    @State private var busy = false
    @State private var status = ""

    private var values: LaunchSettings? {
        guard let memory = UInt32(memory), memory > 0,
              let width = UInt16(width), width > 0,
              let height = UInt16(height), height > 0 else { return nil }
        return LaunchSettings(memory_mib: memory, fullscreen: fullscreen, width: width, height: height,
                              arguments: arguments.components(separatedBy: .newlines).filter { !$0.isEmpty })
    }

    var body: some View {
        Form {
            Section("Пам’ять і вікно гри") {
                TextField("Максимум пам’яті (МіБ)", text: $memory)
                Toggle("На весь екран", isOn: $fullscreen)
                TextField("Ширина (пкс)", text: $width)
                TextField("Висота (пкс)", text: $height)
            }
            Section("Аргументи JVM") {
                TextEditor(text: $arguments)
                    .font(.system(.body, design: .monospaced))
                    .frame(minHeight: 90)
                    .accessibilityLabel("Аргументи JVM")
                Text("Один аргумент на рядок.").font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Button("Перечитати") { Task { await load() } }
                    .disabled(busy)
                Button("Зберегти") { Task { await save() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!loaded || busy || values == nil)
            }
            if !status.isEmpty { Text(status) }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .task { await load() }
    }

    @MainActor private func load() async {
        busy = true
        defer { busy = false }
        do {
            let settings = try await core.call("launch_settings", as: LaunchSettings.self)
            memory = String(settings.memory_mib)
            width = String(settings.width)
            height = String(settings.height)
            fullscreen = settings.fullscreen
            arguments = settings.arguments.joined(separator: "\n")
            loaded = true
            status = ""
        } catch {
            status = error.localizedDescription
        }
    }

    @MainActor private func save() async {
        guard let values else { return }
        busy = true
        defer { busy = false }
        do {
            _ = try await core.send(SaveLaunchSettings(values: values), as: EmptyReply.self)
            status = "Налаштування збережено."
        } catch {
            status = error.localizedDescription
        }
    }
}
