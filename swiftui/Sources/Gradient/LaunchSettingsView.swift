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
    @AppStorage(InterfacePreferences.themeKey) private var theme = "system"
    @AppStorage(InterfacePreferences.languageKey) private var language = "uk"

    private struct UpdateInfo: Decodable {
        struct Release: Decodable {
            let version: String
            let download_url: URL
        }
        let installed: String
        let available: Release?
    }

    @State private var memory = ""
    @State private var width = ""
    @State private var height = ""
    @State private var fullscreen = false
    @State private var arguments = ""
    @State private var loaded = false
    @State private var busy = false
    @State private var status = ""
    @State private var showJava = false
    @State private var updateInfo: UpdateInfo?
    @State private var updateError: String?
    @State private var checkingUpdate = false

    private var values: LaunchSettings? {
        guard let memory = UInt32(memory), memory > 0,
              let width = UInt16(width), width > 0,
              let height = UInt16(height), height > 0 else { return nil }
        return LaunchSettings(memory_mib: memory, fullscreen: fullscreen, width: width, height: height,
                              arguments: arguments.components(separatedBy: .newlines).filter { !$0.isEmpty })
    }

    var body: some View {
        Form {
            Section(tr("Вигляд і мова")) {
                Picker(tr("Тема"), selection: $theme) {
                    Text(tr("Системна")).tag("system")
                    Text(tr("Світла")).tag("light")
                    Text(tr("Темна")).tag("dark")
                }
                Picker(tr("Мова"), selection: $language) {
                    Text(verbatim: "Українська").tag("uk")
                    Text(verbatim: "English").tag("en")
                }
                if language != InterfacePreferences.language {
                    Text(tr("Перезапустіть Gradient, щоб змінити мову."))
                        .foregroundStyle(.secondary)
                }
            }
            Section("Java") {
                Button(tr("Інсталяції Java…")) { showJava = true }
            }
            Section(tr("Оновлення Gradient")) {
                Button(tr("Перевірити оновлення")) { Task { await checkForUpdate() } }
                    .disabled(checkingUpdate)
                if checkingUpdate {
                    ProgressView(tr("Перевіряємо оновлення…")).controlSize(.small)
                } else if let updateInfo {
                    Text(tr("Встановлено {0}.", String(describing: updateInfo.installed)))
                    if let release = updateInfo.available {
                        Link(tr("Завантажити {0}", String(describing: release.version)), destination: release.download_url)
                    } else {
                        Text(tr("Новішого релізу для цієї платформи немає."))
                    }
                } else if let updateError {
                    Text(updateError)
                }
            }
            Section(tr("Пам’ять і вікно гри")) {
                TextField(tr("Максимум пам’яті (МіБ)"), text: $memory)
                Toggle(tr("На весь екран"), isOn: $fullscreen)
                TextField(tr("Ширина (пкс)"), text: $width)
                TextField(tr("Висота (пкс)"), text: $height)
            }
            Section(tr("Аргументи JVM")) {
                TextEditor(text: $arguments)
                    .font(.system(.body, design: .monospaced))
                    .frame(minHeight: 90)
                    .accessibilityLabel(Text(tr("Аргументи JVM")))
                Text(tr("Один аргумент на рядок.")).font(.caption).foregroundStyle(.secondary)
            }
            HStack {
                Button(tr("Перечитати")) { Task { await load() } }
                    .disabled(busy)
                Button(tr("Зберегти")) { Task { await save() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(!loaded || busy || values == nil)
            }
            if !status.isEmpty { Text(status) }
        }
        .formStyle(.grouped)
        .frame(width: 480)
        .task { await load() }
        .sheet(isPresented: $showJava) { JavaRuntimesView(core: core) }
    }

    @MainActor private func checkForUpdate() async {
        guard !checkingUpdate else { return }
        checkingUpdate = true
        updateInfo = nil
        updateError = nil
        defer { checkingUpdate = false }
        do {
            if SparkleUpdates.shared.isConfigured {
                try await SparkleUpdates.shared.check(using: core)
            } else {
                updateInfo = try await core.call("check_update", as: UpdateInfo.self)
            }
        } catch {
            updateError = error.localizedDescription
        }
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
            status = tr("Налаштування збережено.")
        } catch {
            status = error.localizedDescription
        }
    }
}
