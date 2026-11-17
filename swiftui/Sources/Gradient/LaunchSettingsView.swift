import SwiftUI

private struct LaunchSettings: Codable {
    var memory_mib: UInt32
    var fullscreen: Bool
    var width: UInt16
    var height: UInt16
    var arguments: [String]
    var discord_rpc: Bool? = nil
}

private struct SaveLaunchSettings: Encodable {
    let method = "save_launch_settings"
    let values: LaunchSettings
}

struct LaunchSettingsView: View {
    let core: CoreClient
    @AppStorage(InterfacePreferences.themeKey) private var theme = "system"
    @AppStorage(InterfacePreferences.languageKey) private var language = "uk"
    @AppStorage("interface.sidebarProfiles") private var sidebarLimit = 6

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
    @State private var discordPresence: Bool?
    @State private var savingDiscordPresence = false
    @State private var discordError: String?

    private struct SetDiscordPresence: Encodable {
        let method = "set_discord_presence"
        let enabled: Bool
    }

    private var values: LaunchSettings? {
        guard let memory = UInt32(memory), memory > 0,
              let width = UInt16(width), width > 0,
              let height = UInt16(height), height > 0 else { return nil }
        return LaunchSettings(memory_mib: memory, fullscreen: fullscreen, width: width, height: height,
                              arguments: arguments.components(separatedBy: .newlines).filter { !$0.isEmpty })
    }

    var body: some View {
        TabView {
            Tab(LocalizedStringKey(tr("Вигляд і мова")), systemImage: "paintbrush") {
                appearanceSettings
            }
            Tab(LocalizedStringKey(tr("Запуск")), systemImage: "play.circle") {
                gameSettings
            }
            Tab(LocalizedStringKey(tr("Поведінка")), systemImage: "slider.horizontal.3") {
                NativePreferencesView(core: core)
            }
            Tab(LocalizedStringKey(tr("Сховище й діагностика")), systemImage: "internaldrive") {
                StoragePreferencesView(core: core)
            }
            Tab(LocalizedStringKey(tr("Інтеграції")), systemImage: "puzzlepiece.extension") {
                integrationSettings
            }
            Tab(LocalizedStringKey(tr("Оновлення Gradient")), systemImage: "arrow.down.circle") {
                updateSettings
            }
        }
        .frame(width: 720, height: 530)
        .task { if !loaded { await load() } }
        .sheet(isPresented: $showJava) { JavaRuntimesView(core: core) }
    }

    private var appearanceSettings: some View {
        Form {
            Section(tr("Вигляд і мова")) {
                Stepper("\(tr("Профілів у бічній панелі")) · \(sidebarLimit)", value: $sidebarLimit, in: 1...30)
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
        }
        .formStyle(.grouped)
    }

    private var gameSettings: some View {
        Form {
            Section(tr("Пам’ять і вікно гри")) {
                TextField(tr("Максимум пам’яті (МіБ)"), text: $memory)
                Toggle(tr("На весь екран"), isOn: $fullscreen)
                TextField(tr("Ширина (пкс)"), text: $width)
                TextField(tr("Висота (пкс)"), text: $height)
            }
            Section("Java") {
                Button(tr("Інсталяції Java…")) { showJava = true }
            }
            Section(tr("Аргументи JVM")) {
                Menu(tr("Пресет JVM")) {
                    Button(tr("Застосувати G1GC")) { arguments = "-XX:+UseG1GC" }
                    Button(tr("Застосувати ZGC (Java 21+)")) { arguments = "-XX:+UseZGC" }
                    Button(tr("Застосувати Shenandoah (сумісна Java)")) { arguments = "-XX:+UseShenandoahGC" }
                    Button(tr("Очистити аргументи JVM")) { arguments = "" }
                }
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
    }

    private var integrationSettings: some View {
        Form {
            Section("Discord") {
                Toggle("Discord Rich Presence", isOn: Binding(
                    get: { discordPresence == true },
                    set: { enabled in Task { await saveDiscordPresence(enabled) } }
                ))
                .disabled(discordPresence == nil || savingDiscordPresence)
                Text(tr("Показувати в Discord, коли відкрито лаунчер або запущено профіль Minecraft."))
                    .foregroundStyle(.secondary)
                if let discordError {
                    Text(discordError)
                } else if loaded && discordPresence == nil {
                    Text(tr("Для Discord Rich Presence потрібне оновлення ядра."))
                }
            }
        }
        .formStyle(.grouped)
    }

    @MainActor private func saveDiscordPresence(_ enabled: Bool) async {
        guard let previous = discordPresence, previous != enabled, !savingDiscordPresence else { return }
        savingDiscordPresence = true
        discordError = nil
        discordPresence = enabled
        defer { savingDiscordPresence = false }
        do {
            _ = try await core.send(SetDiscordPresence(enabled: enabled), as: EmptyReply.self)
        } catch {
            discordPresence = previous
            discordError = error.localizedDescription
        }
    }

    private var updateSettings: some View {
        Form {
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
        }
        .formStyle(.grouped)
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
            if !savingDiscordPresence { discordPresence = settings.discord_rpc }
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
