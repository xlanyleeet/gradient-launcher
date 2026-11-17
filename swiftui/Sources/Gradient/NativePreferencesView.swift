import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct NativePreferences: Codable {
    struct Hooks: Codable {
        var pre_launch: String?
        var wrapper: String?
        var post_exit: String?
    }
    var compact_instance_cards: Bool
    var show_play_time: Bool
    var show_jump_in: Bool
    var hide_installed_modpacks: Bool
    var show_worlds_tab_in_instances: Bool
    var show_files_tab_in_instances: Bool
    var show_screenshots_tab_in_instances: Bool
    var show_skin_selector_in_sidebar: Bool
    var hide_on_process_start: Bool
    var refocus_on_game_close: Bool
    var warn_on_unknown_modpacks: Bool
    var always_show_copy_details: Bool
    var max_concurrent_downloads: Int
    var max_concurrent_writes: Int
    var custom_env_vars: [[String]]
    var hooks: Hooks
}

enum PreferenceBridge {
    static let didSave = Notification.Name("GradientNativePreferencesDidSave")

    struct Request: Encodable {
        let method = "preferences"
        let request: Payload
    }

    struct Payload: Encodable {
        let action: String
        var values: NativePreferences? = nil
        var path: String? = nil
        var bytes: UInt64? = nil
    }

    static func call<T: Decodable>(_ core: CoreClient, _ action: String,
                                   values: NativePreferences? = nil,
                                   path: String? = nil, bytes: UInt64? = nil,
                                   as type: T.Type) async throws -> T {
        try await core.send(Request(request: Payload(action: action, values: values, path: path, bytes: bytes)), as: type)
    }
}

struct NativePreferencesView: View {
    let core: CoreClient
    @State private var values: NativePreferences?
    @State private var environment = ""
    @State private var status = ""
    @State private var busy = false

    var body: some View {
        Form {
            if let draft = Binding($values) {
                library(draft)
                behavior(draft)
                advanced(draft)
                Button(tr("Зберегти")) { Task { await save() } }
                    .disabled(busy)
            }
            if !status.isEmpty { Text(status) }
        }
        .formStyle(.grouped)
        .task {
            guard values == nil else { return }
            do {
                let loaded = try await PreferenceBridge.call(core, "read", as: NativePreferences.self)
                environment = loaded.custom_env_vars.map { $0.joined(separator: "=") }.joined(separator: "\n")
                values = loaded
            } catch { status = error.localizedDescription }
        }
    }

    private func library(_ draft: Binding<NativePreferences>) -> some View {
        Group {
            Section(tr("Вигляд бібліотеки")) {
                Toggle(tr("Компактна бібліотека"), isOn: draft.compact_instance_cards)
                Toggle(tr("Показувати час гри"), isOn: draft.show_play_time)
                Toggle(tr("Показувати швидкий перехід"), isOn: draft.show_jump_in)
                Toggle(tr("Приховувати встановлені збірки в каталозі"), isOn: draft.hide_installed_modpacks)
            }
            Section(tr("Вкладки профілю")) {
                Toggle(tr("Показувати вкладку «Світи»"), isOn: draft.show_worlds_tab_in_instances)
                Toggle(tr("Показувати вкладку «Файли»"), isOn: draft.show_files_tab_in_instances)
                Toggle(tr("Показувати вкладку «Скриншоти»"), isOn: draft.show_screenshots_tab_in_instances)
                Toggle(tr("Показувати кнопку вибору скіна в бічній панелі"), isOn: draft.show_skin_selector_in_sidebar)
            }
        }
    }

    private func behavior(_ draft: Binding<NativePreferences>) -> some View {
        Section(tr("Поведінка")) {
            Toggle(tr("Згортати лаунчер після запуску Minecraft"), isOn: draft.hide_on_process_start)
            Toggle(tr("Повертати вікно після виходу з Minecraft"), isOn: draft.refocus_on_game_close)
            Toggle(tr("Підтверджувати імпорт локальних модпаків"), isOn: draft.warn_on_unknown_modpacks)
            Toggle(tr("Показувати відомості для копіювання"), isOn: draft.always_show_copy_details)
        }
    }

    private func advanced(_ draft: Binding<NativePreferences>) -> some View {
        Group {
            Section(tr("Розширений запуск")) {
                Text(tr("Змінні середовища · NAME=VALUE"))
                TextEditor(text: $environment).font(.system(.body, design: .monospaced)).frame(minHeight: 65)
                TextField(tr("Перед запуском"), text: command(draft.hooks.pre_launch))
                TextField(tr("Команда-обгортка"), text: command(draft.hooks.wrapper))
                TextField(tr("Після виходу"), text: command(draft.hooks.post_exit))
            }
            Section(tr("Сховище й діагностика")) {
                Stepper("\(tr("Одночасні завантаження")) · \(draft.wrappedValue.max_concurrent_downloads)",
                        value: draft.max_concurrent_downloads, in: 1...128)
                Stepper("\(tr("Одночасні записи")) · \(draft.wrappedValue.max_concurrent_writes)",
                        value: draft.max_concurrent_writes, in: 1...128)
                Text(tr("Ліміти застосовуються після перезапуску Gradient.")).foregroundStyle(.secondary)
            }
        }
    }

    private func command(_ binding: Binding<String?>) -> Binding<String> {
        Binding(get: { binding.wrappedValue ?? "" }, set: {
            binding.wrappedValue = $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : $0
        })
    }

    @MainActor private func save() async {
        guard var values, !busy else { return }
        values.custom_env_vars = environment.split(whereSeparator: \.isNewline).map {
            $0.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
        }
        guard values.custom_env_vars.allSatisfy({ $0.count == 2 }) else {
            status = tr("Змінні середовища · NAME=VALUE")
            return
        }
        busy = true
        defer { busy = false }
        do {
            _ = try await PreferenceBridge.call(core, "save", values: values, as: EmptyReply.self)
            NotificationCenter.default.post(name: PreferenceBridge.didSave, object: nil)
            status = tr("Налаштування збережено.")
        } catch { status = error.localizedDescription }
    }
}

struct StoragePreferencesView: View {
    let core: CoreClient

    private struct Locations: Decodable { let data: String; let backups: String }
    private struct Usage: Decodable {
        let unique_bytes: UInt64
        let shared_bytes: UInt64
        let unused_cache_bytes: UInt64
        let cache_limit_bytes: UInt64
    }
    private struct Repair: Decodable {
        struct Issue: Decodable { let message: String }
        let issues: [Issue]
    }

    @State private var locations: Locations?
    @State private var usage: Usage?
    @State private var limit = ""
    @State private var status = ""
    @State private var busy = false

    var body: some View {
        Form {
            if let locations {
                Section(tr("Папка даних")) {
                    Text(locations.data).textSelection(.enabled)
                    Button(tr("Відкрити папку даних")) { NSWorkspace.shared.open(URL(fileURLWithPath: locations.data)) }
                    Button(tr("Змінити папку даних…"), action: moveDirectory)
                    Button(tr("Резервні копії бази")) { NSWorkspace.shared.open(URL(fileURLWithPath: locations.backups)) }
                }
            }
            storage
            Section(tr("Сховище й діагностика")) {
                Button(tr("Очистити кеш каталогу")) {
                    run { _ = try await PreferenceBridge.call(core, "clear_cache", as: EmptyReply.self) }
                }
                Button(tr("Експорт діагностики…"), action: exportDebug)
                Text(tr("Архів містить логи лаунчера й Minecraft."))
                    .foregroundStyle(.secondary)
            }
            if busy { ProgressView().controlSize(.small) }
            if !status.isEmpty { Text(status) }
        }
        .formStyle(.grouped)
        .disabled(busy)
        .task {
            do {
                locations = try await PreferenceBridge.call(core, "locations", as: Locations.self)
                try await refreshUsage()
            } catch { status = error.localizedDescription }
        }
    }

    private var storage: some View {
        Section(tr("Використання сховища")) {
            if let usage {
                Text(tr("Унікальний контент · {0} МіБ", String(usage.unique_bytes >> 20)))
                Text(tr("Спільний контент · {0} МіБ", String(usage.shared_bytes >> 20)))
                Text(tr("Невикористано · {0} МіБ", String(usage.unused_cache_bytes >> 20)))
            }
            TextField(tr("Ліміт кешу (ГіБ)"), text: $limit)
            Button(tr("Зберегти ліміт")) {
                guard let gib = Double(limit), gib >= 0, gib <= 1_048_576 else { return }
                run {
                    _ = try await PreferenceBridge.call(core, "store_limit", bytes: UInt64(gib * 1_073_741_824), as: EmptyReply.self)
                    try await refreshUsage()
                }
            }
            Button(tr("Очистити невикористаний контент")) {
                run {
                    _ = try await PreferenceBridge.call(core, "store_cleanup", as: UInt64.self)
                    try await refreshUsage()
                }
            }
            Button(tr("Перевірити й відновити файли")) {
                run {
                    let report = try await PreferenceBridge.call(core, "store_repair", as: Repair.self)
                    try await refreshUsage()
                    status = tr("Помилок під час перевірки: {0}", String(report.issues.count))
                }
            }
        }
    }

    @MainActor private func refreshUsage() async throws {
        let next = try await PreferenceBridge.call(core, "store_usage", as: Usage.self)
        usage = next
        limit = String(Double(next.cache_limit_bytes) / 1_073_741_824)
    }

    private func moveDirectory() {
        let picker = NSOpenPanel()
        picker.canChooseDirectories = true
        picker.canChooseFiles = false
        guard picker.runModal() == .OK, let url = picker.url else { return }
        run {
            if try await PreferenceBridge.call(core, "move_directory", path: url.path, as: Bool.self) {
                status = tr("Нова папка застосовується після перезапуску Gradient.")
            }
        }
    }

    private func exportDebug() {
        let picker = NSSavePanel()
        picker.allowedContentTypes = [.zip]
        picker.nameFieldStringValue = "gradient-diagnostics.zip"
        guard picker.runModal() == .OK, let url = picker.url else { return }
        run { _ = try await PreferenceBridge.call(core, "export_debug", path: url.path, as: Bool.self) }
    }

    private func run(_ operation: @escaping @MainActor () async throws -> Void) {
        guard !busy else { return }
        busy = true
        status = ""
        Task { @MainActor in
            defer { busy = false }
            do {
                try await operation()
                if status.isEmpty { status = tr("Дію виконано.") }
            } catch { status = error.localizedDescription }
        }
    }
}
