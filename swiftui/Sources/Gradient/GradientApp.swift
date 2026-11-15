import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WebKit

@main
struct GradientApp: App {
    @State private var core = CoreClient()
    @AppStorage(InterfacePreferences.themeKey) private var theme = "system"

    var body: some Scene {
        Window("Gradient Launcher", id: "launcher") {
            LauncherView(core: core)
                .frame(minWidth: 760, minHeight: 560)
                .preferredColorScheme(InterfacePreferences.colorScheme(for: theme))
        }
        .defaultSize(width: 1120, height: 740)

        Settings {
            LaunchSettingsView(core: core)
                .preferredColorScheme(InterfacePreferences.colorScheme(for: theme))
        }
    }
}

private struct Profile: Decodable, Identifiable {
    let id: String
    let name: String
    let version: String
    let loader: String
    let last_played: String?
    let running: Bool
    let quarantined: Bool
    let icon: String?
}

private struct Account: Decodable, Identifiable {
    let id: String
    let name: String
    let selected: Bool
}

private struct ShortcutProfile: Decodable {
    let id: String
    let name: String
    let quarantined: Bool
}

private enum LibrarySheet: String, Identifiable {
    case create, rename, accounts, launch, groups
    var id: String { rawValue }
}

private struct Job: Decodable, Identifiable {
    let job_id: String
    let instance_id: String?
    let status: String
    let phase: String
    let paused: Bool
    let can_pause: Bool
    let can_cancel: Bool
    let progress: Double?
    let error: String?
    var id: String { job_id }
}

private enum ProfileTab: String, CaseIterable {
    case log = "Логи"
    case queue = "Завантаження"
    case content = "Контент"
    case worlds = "Світи"
    case screenshots = "Скриншоти"
    case files = "Файли"
    case servers = "Сервери"
}

private struct ContentEntry: Decodable, Identifiable {
    let path: String
    let name: String
    let file: String
    let version: String?
    let kind: String
    let enabled: Bool
    let locked: Bool
    let synced: Bool
    let has_update: Bool
    let can_choose_version: Bool
    var id: String { path }
}

private struct WorldEntry: Decodable, Identifiable {
    let id: String
    let name: String
    let last_played: String?
}

private struct ArchivedLog: Decodable, Hashable {
    let filename: String
    let log_type: String

    var label: String {
        log_type == "CrashReport" ? tr("{0} · Звіт про збій", String(describing: filename)) : filename
    }
}

private struct Screenshot: Decodable, Identifiable {
    let filename: String
    let path: String
    var id: String { filename }
}

private struct LauncherView: View {
    let core: CoreClient
    @Environment(\.colorScheme) private var colorScheme
    private var tint: Color {
        colorScheme == .dark ? Color(red: 0.09, green: 0.18, blue: 0.20)
            : Color(red: 0.66, green: 0.87, blue: 0.84)
    }

    @State private var profiles: [Profile] = []
    @State private var jobs: [Job] = []
    @State private var selectedID: String?
    @State private var showDiscover = false
    @State private var pendingPack: URL?
    @State private var catalogLink: String?
    @State private var shortcutProfile: ShortcutProfile?
    @State private var transfer: ProfileTransferView.Operation?
    @State private var fileRefresh = 0
    @State private var coverKey: String?
    @State private var cover: NSImage?
    @State private var log = ""
    @State private var archivedLogs: [ArchivedLog] = []
    @State private var selectedLog: ArchivedLog?
    @State private var displayedLog: ArchivedLog?
    @State private var logProfile: String?
    @State private var logWasRunning = false
    @State private var status = ""
    @State private var busy = false
    @State private var tab = ProfileTab.log
    @State private var loadedTab: String?
    @State private var content: [ContentEntry] = []
    @State private var worlds: [WorldEntry] = []
    @State private var screenshots: [Screenshot] = []
    @State private var selectedScreenshot = ""
    @State private var screenshotPreview: NSImage?
    @State private var pendingRemoval: (profileID: String, entry: ContentEntry)?
    @State private var modVersion: ModVersionView.Target?
    @State private var diagnosticsJob: Job?
    @State private var skinSettings = false
    @State private var search = ""
    @State private var showLogin = false
    @State private var nickname = ""
    @State private var loginURL: URL?
    @State private var showMicrosoft = false
    @State private var librarySheet: LibrarySheet?
    @State private var editedProfile: Profile?
    @State private var profileName = ""
    @State private var versions: [String] = []
    @State private var version = ""
    @State private var loader = "vanilla"
    @State private var accounts: [Account] = []
    @State private var profileToRemove: Profile?
    @State private var accountToRemove: Account?

    private var profile: Profile? { profiles.first { $0.id == selectedID } }
    private var filteredProfiles: [Profile] {
        search.isEmpty ? profiles : profiles.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        NavigationSplitView {
            List(selection: $selectedID) {
                Section {
                    Button(tr("Каталог Modrinth"), systemImage: "magnifyingglass") { showDiscover = true }
                        .buttonStyle(.plain)
                }
                Section(tr("Профілі")) {
                    ForEach(filteredProfiles) { entry in
                        Label {
                            Text(entry.name)
                        } icon: {
                            if let icon = entry.icon, let image = NSImage(contentsOfFile: icon) {
                                Image(nsImage: image).resizable().scaledToFit()
                                    .frame(width: 19, height: 19)
                            } else {
                                Image(systemName: entry.running ? "play.circle.fill" : "cube")
                            }
                        }
                            .tag(entry.id)
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 218)
            .safeAreaInset(edge: .bottom) {
                VStack(alignment: .leading, spacing: 8) {
                    Button(tr("Акаунти"), systemImage: "person.2", action: showAccounts)
                    Button(tr("Скін Minecraft"), systemImage: "person.crop.square") { skinSettings = true }
                    Button(tr("Увійти в Microsoft"), systemImage: "person.crop.circle", action: beginMicrosoft)
                    Button(tr("Офлайн-акаунт"), systemImage: "person") { showLogin = true }
                }
                    .padding(12)
            }
        } detail: {
            if showDiscover {
                DiscoverView(core: core, profileID: selectedID, incomingLink: $catalogLink) {
                    loadedTab = nil
                }
                .toolbar {
                    Button(tr("До бібліотеки"), systemImage: "chevron.left") { showDiscover = false }
                }
            } else {
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    hero
                    VStack(alignment: .leading, spacing: 14) {
                        Divider()
                        Text(profile.map {
                            tr("Minecraft {0}   ·   {1}   ·   Останній запуск  {2}", String(describing: $0.version), String(describing: $0.loader), String(describing: $0.last_played ?? "—"))
                        } ?? tr("Виберіть профіль або імпортуйте модпак."))
                        .font(.callout)
                        Divider()
                        if !status.isEmpty { Text(status).font(.callout) }
                        Picker(tr("Деталі"), selection: $tab) {
                            ForEach(ProfileTab.allCases, id: \.self) { item in
                                Text(tr(item.rawValue)).tag(item)
                            }
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                        tabContent
                    }
                    .padding(.horizontal, 30)
                    .padding(.bottom, 30)
                }
            }
            .background(tint)
            .toolbar {
                Button(tr("Новий профіль"), systemImage: "plus.square", action: beginCreateProfile)
                Button(tr("Імпортувати .mrpack"), systemImage: "plus", action: importPack)
                Button(tr("Імпорт з лаунчера"), systemImage: "folder.badge.plus") { transfer = .importLauncher }
                Button(tr("Оновити"), systemImage: "arrow.clockwise") {
                    coverKey = nil
                    loadedTab = nil
                    logProfile = nil
                    fileRefresh += 1
                    Task { await perform(refresh) }
                }
            }
            }
        }
        .searchable(text: $search, prompt: Text(tr("Пошук профілів")))
        .task {
            while !Task.isCancelled {
                if !(await core.isSuspendedForUpdate) { await perform(refresh) }
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
            }
        }
        .task {
            if let argument = CommandLine.arguments.dropFirst().first(where: {
                URL(fileURLWithPath: $0).pathExtension.lowercased() == "mrpack" &&
                FileManager.default.fileExists(atPath: $0)
            }) {
                pendingPack = URL(fileURLWithPath: argument)
            }
        }
        .onOpenURL { url in
            if url.isFileURL && url.pathExtension.lowercased() == "mrpack" {
                pendingPack = url
            } else if url.scheme == "gradient-native", url.host == "launch" {
                Task { await openProfileShortcut(url) }
            } else if let scheme = url.scheme, ["gradient-native", "gradient", "modrinth"].contains(scheme) {
                catalogLink = url.absoluteString
                showDiscover = true
            }
        }
        .confirmationDialog(tr("Запустити Minecraft?"), isPresented: Binding(
            get: { shortcutProfile != nil },
            set: { if !$0 { shortcutProfile = nil } }
        ), presenting: shortcutProfile) { entry in
            Button(tr("Грати")) {
                Task {
                    status = tr("Готуємо Minecraft…")
                    do {
                        _ = try await core.call("play", ["id": entry.id], as: EmptyReply.self)
                        status = ""
                        await perform(refresh)
                    } catch { status = error.localizedDescription }
                }
            }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: { Text($0.name) }
        .confirmationDialog(tr("Імпортувати модпак?"), isPresented: Binding(
            get: { pendingPack != nil },
            set: { if !$0 { pendingPack = nil } }
        ), presenting: pendingPack) { url in
            Button(tr("Імпортувати")) {
                Task {
                    do {
                        _ = try await core.call("import", ["path": url.path], as: EmptyReply.self)
                        status = tr("Модпак додано до черги.")
                        await perform(refresh)
                    } catch { status = error.localizedDescription }
                }
            }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: { Text($0.lastPathComponent) }
        .onChange(of: selectedID) {
            if catalogLink == nil { showDiscover = false }
            Task { await perform(loadSelection) }
        }
        .onChange(of: tab) {
            Task { await perform(loadSelection) }
        }
        .onChange(of: selectedLog) {
            Task { await perform(loadSelection) }
        }
        .onChange(of: selectedScreenshot) { updateScreenshotPreview() }
        .confirmationDialog(tr("Видалити контент?"), isPresented: Binding(
            get: { pendingRemoval != nil },
            set: { if !$0 { pendingRemoval = nil } }
        ), presenting: pendingRemoval) { removal in
            Button(tr("Видалити"), role: .destructive) {
                mutate("remove_content", ["id": removal.profileID, "path": removal.entry.path])
            }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: { removal in
            Text(removal.entry.name)
        }
        .alert(tr("Офлайн-акаунт"), isPresented: $showLogin) {
            TextField(tr("Ім’я гравця"), text: $nickname)
            Button(tr("Зберегти")) {
                mutate("offline_login", ["name": nickname], message: tr("Офлайн-акаунт вибрано."))
            }
            Button(tr("Скасувати"), role: .cancel) {}
        }
        .sheet(isPresented: $showMicrosoft, onDismiss: cancelMicrosoft) {
            if let loginURL {
                VStack {
                    MicrosoftWebView(url: loginURL, complete: completeMicrosoft)
                    Button(tr("Скасувати")) { showMicrosoft = false }
                        .padding(.bottom, 12)
                }
                .frame(width: 680, height: 550)
            }
        }
        .sheet(item: $librarySheet) { sheet in
            if sheet == .launch, let editedProfile {
                ProfileLaunchSettingsView(core: core, profileID: editedProfile.id) {
                    status = tr("Налаштування профілю збережено.")
                }
            } else if sheet == .groups, let editedProfile {
                ProfileGroupsView(core: core, profileID: editedProfile.id)
            } else {
                libraryEditor(sheet)
            }
        }
        .sheet(item: $transfer) { operation in
            ProfileTransferView(core: core, operation: operation) { status = $0 }
        }
        .sheet(item: $modVersion) { target in
            ModVersionView(core: core, target: target) {
                loadedTab = nil
                status = tr("Версію мода змінено.")
            }
        }
        .sheet(item: $diagnosticsJob) { job in
            JobDiagnosticsView(core: core, jobID: job.id)
        }
        .sheet(isPresented: $skinSettings) { SkinSettingsView(core: core) }
        .alert(tr("Видалити профіль?"), isPresented: Binding(
            get: { profileToRemove != nil },
            set: { if !$0 { profileToRemove = nil } }
        ), presenting: profileToRemove) { entry in
            Button(tr("Видалити"), role: .destructive) { mutate("remove_profile", ["id": entry.id]) }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: { entry in
            Text(tr("«{0}» і його локальні файли буде видалено.", String(describing: entry.name)))
        }
    }

    private func tabKey(_ profile: Profile, _ tab: ProfileTab) -> String {
        "\(profile.id)/\(profile.running)/\(profile.quarantined)/\(tab)"
    }

    @ViewBuilder private var tabContent: some View {
        switch tab {
        case .queue:
            if jobs.isEmpty { Text(tr("Черга порожня.")) }
            ForEach(jobs) { job in jobRow(job) }
        case .log:
            if profile == nil {
                Text(tr("Виберіть профіль."))
            } else {
                Picker(tr("Джерело логу"), selection: $selectedLog) {
                    Text(tr("Живий лог")).tag(nil as ArchivedLog?)
                    ForEach(archivedLogs, id: \.self) { source in
                        Text(source.label).tag(Optional(source))
                    }
                }
                Text(log.isEmpty ? tr("Лог порожній.") : log)
                    .font(.system(.callout, design: .monospaced))
                    .textSelection(.enabled)
            }
        case .content, .worlds:
            if let profile {
                if tab == .content {
                    Button(tr("Додати .jar"), systemImage: "plus") { addMod(profile) }
                        .disabled(busy || profile.running || profile.quarantined)
                }
                if loadedTab != tabKey(profile, tab) {
                    ProgressView()
                } else if tab == .content {
                    if content.isEmpty { Text(tr("Контенту ще немає.")) }
                    ForEach(content) { entry in contentRow(profile, entry) }
                } else {
                    if worlds.isEmpty { Text(tr("Світів ще немає.")) }
                    ForEach(worlds) { world in worldRow(profile, world) }
                }
            } else {
                Text(tr("Виберіть профіль."))
            }
        case .screenshots:
            if let profile {
                if loadedTab != tabKey(profile, tab) {
                    ProgressView()
                } else {
                    screenshotGallery(profile)
                }
            } else {
                Text(tr("Виберіть профіль."))
            }
        case .files:
            if let profile {
                ProfileFilesView(core: core, profileID: profile.id, refresh: fileRefresh,
                                 editableProfile: !profile.running && !profile.quarantined).id(profile.id)
            } else {
                Text(tr("Виберіть профіль."))
            }
        case .servers:
            if let profile {
                ProfileServersView(core: core, profileID: profile.id,
                                   canPlay: !profile.running && !profile.quarantined,
                                   refresh: fileRefresh).id(profile.id)
            } else {
                Text(tr("Виберіть профіль."))
            }
        }
    }

    @ViewBuilder private func screenshotGallery(_ profile: Profile) -> some View {
        if screenshots.isEmpty {
            Text(tr("Скриншотів ще немає."))
        } else {
            HStack {
                Picker(tr("Скриншот"), selection: $selectedScreenshot) {
                    ForEach(screenshots) { shot in Text(shot.filename).tag(shot.id) }
                }
                Button(tr("Експортувати всі у ZIP"), systemImage: "square.and.arrow.up") {
                    let panel = NSSavePanel()
                    panel.allowedContentTypes = [.zip]
                    panel.nameFieldStringValue = "screenshots.zip"
                    guard panel.runModal() == .OK, let url = panel.url else { return }
                    mutate("export_screenshots", ["id": profile.id, "path": url.path],
                           message: tr("Архів збережено."))
                }
                .disabled(busy)
            }
            if let screenshotPreview {
                Image(nsImage: screenshotPreview)
                    .resizable()
                    .scaledToFit()
                    .frame(maxHeight: 440)
                    .accessibilityLabel(Text(selectedScreenshot))
            } else {
                Text(tr("Файл скриншота недоступний."))
            }
        }
    }

    private func updateScreenshotPreview() {
        let selected = screenshots.first { $0.id == selectedScreenshot }
        screenshotPreview = selected.flatMap { NSImage(contentsOfFile: $0.path) }
    }

    private func contentRow(_ profile: Profile, _ entry: ContentEntry) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(entry.name).font(.headline)
            let state = entry.enabled ? tr("Увімкнено") : tr("Вимкнено")
            let managed = entry.locked || entry.synced ? tr(" · Керований") : ""
            Text("\(entry.file) · \(entry.kind) · \(entry.version ?? "—") · \(state)\(managed)")
                .font(.callout)
            if !profile.running && !profile.quarantined && !entry.locked && !entry.synced {
                HStack {
                    Button(entry.enabled ? tr("Вимкнути") : tr("Увімкнути")) {
                        mutate("set_content_enabled", [
                            "id": profile.id, "path": entry.path,
                            "action": entry.enabled ? "disable" : "enable"
                        ])
                    }
                    Button(tr("Видалити")) { pendingRemoval = (profile.id, entry) }
                    if entry.has_update {
                        Button(tr("Оновити")) { mutate("update_content", ["id": profile.id, "path": entry.path]) }
                    }
                    if entry.can_choose_version {
                        Button(tr("Змінити версію")) {
                            modVersion = .init(profileID: profile.id, path: entry.path,
                                               name: entry.name, version: entry.version)
                        }
                    }
                }
                .disabled(busy)
            }
        }
        .padding(.vertical, 4)
    }

    private func worldRow(_ profile: Profile, _ world: WorldEntry) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(world.name).font(.headline)
            Text(tr("Остання гра · {0}", String(describing: world.last_played ?? "—"))).font(.callout)
            if !profile.running && !profile.quarantined {
                HStack {
                    Button(tr("Відкрити світ")) {
                        mutate("open_world", ["id": profile.id, "world": world.id])
                    }
                    Button(tr("Резервна копія")) {
                        mutate("backup_world", ["id": profile.id, "world": world.id],
                               message: tr("Резервну копію створено."))
                    }
                }
                .disabled(busy)
            }
        }
        .padding(.vertical, 4)
    }

    private func addMod(_ profile: Profile) {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "jar") ?? .data]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        mutate("add_mod", ["id": profile.id, "path": url.path])
    }

    private var hero: some View {
        GeometryReader { geometry in
            ZStack(alignment: .bottomLeading) {
                if let artwork = cover ?? bundledCover {
                    Image(nsImage: artwork)
                        .resizable()
                        .scaledToFill()
                        .frame(width: geometry.size.width, height: geometry.size.height)
                        .clipped()
                        .accessibilityHidden(true)
                }
                LinearGradient(stops: [
                    .init(color: tint.opacity(0), location: 0.24),
                    .init(color: tint.opacity(0.88), location: 0.82),
                    .init(color: tint, location: 1)
                ], startPoint: .top, endPoint: .bottom)
                VStack(alignment: .leading, spacing: 11) {
                    Text(profile?.name ?? tr("Бібліотека"))
                        .font(.system(size: 42, weight: .bold))
                        .lineLimit(2)
                    HStack(spacing: 8) {
                        Button {
                            guard let profile else { return }
                            mutate(profile.running ? "stop" : "play", ["id": profile.id])
                        } label: {
                            Label(profile?.running == true ? tr("Зупинити") : tr("Грати"),
                                  systemImage: profile?.running == true ? "stop.fill" : "play.fill")
                            .padding(.horizontal, 12)
                            .padding(.vertical, 5)
                        }
                        .buttonStyle(.borderedProminent)
                        .buttonBorderShape(.capsule)
                        .disabled(busy || profile == nil || (profile?.quarantined == true && profile?.running != true))
                        if let profile { profileMenu(profile) }
                    }
                }
                .padding(30)
            }
        }
        .frame(height: 350)
    }

    private var bundledCover: NSImage? {
        let url = Bundle.main.url(forResource: "Cover", withExtension: "png")
            ?? Bundle.module.url(forResource: "Cover", withExtension: "png")
        return url.flatMap(NSImage.init(contentsOf:))
    }

    private func profileMenu(_ profile: Profile) -> some View {
        Menu(tr("Дії профілю")) {
            Button(tr("Перейменувати")) {
                editedProfile = profile
                profileName = profile.name
                librarySheet = .rename
            }
            Button(tr("Змінити іконку")) {
                let panel = NSOpenPanel()
                panel.allowedContentTypes = [.png, .jpeg, .webP]
                if panel.runModal() == .OK, let url = panel.url {
                    mutate("profile_icon", ["id": profile.id, "path": url.path])
                }
            }
            Button(tr("Java та пам’ять")) {
                editedProfile = profile
                librarySheet = .launch
            }
            .disabled(profile.running)
            Button(tr("Групи профілю")) {
                editedProfile = profile
                librarySheet = .groups
            }
            Button(tr("Дублювати")) {
                mutate("duplicate_profile", ["id": profile.id], message: tr("Дублювання додано до черги."))
            }
            .disabled(profile.running)
            Button(tr("Експортувати .mrpack")) { transfer = .export(profile.id) }
                .disabled(profile.running)
            Button(tr("Відкрити папку профілю")) {
                Task {
                    await perform {
                        let path = try await core.call("profile_folder", ["id": profile.id], as: String.self)
                        if !NSWorkspace.shared.open(URL(fileURLWithPath: path, isDirectory: true)) {
                            status = tr("Не вдалося відкрити папку профілю.")
                        }
                    }
                }
            }
            Button(tr("Створити ярлик")) { createProfileShortcut(profile) }
            Divider()
            Button(tr("Видалити профіль"), role: .destructive) { profileToRemove = profile }
                .disabled(profile.running)
        }
        .fixedSize()
        .disabled(busy)
    }

    private func createProfileShortcut(_ profile: Profile) {
        let panel = NSSavePanel()
        panel.directoryURL = FileManager.default.urls(for: .desktopDirectory, in: .userDomainMask).first
        panel.allowedContentTypes = [UTType(filenameExtension: "webloc") ?? .data]
        panel.nameFieldStringValue = "Gradient Minecraft.webloc"
        guard panel.runModal() == .OK, let destination = panel.url else { return }
        Task {
            do {
                let link = try await core.call("create_profile_shortcut", ["id": profile.id], as: String.self)
                let data = try PropertyListSerialization.data(fromPropertyList: ["URL": link], format: .xml, options: 0)
                try data.write(to: destination, options: .atomic)
                status = tr("Ярлик профілю збережено.")
            } catch { status = error.localizedDescription }
        }
    }

    @MainActor private func openProfileShortcut(_ url: URL) async {
        showDiscover = false
        do {
            let target = try await core.call("resolve_profile_shortcut", ["url": url.absoluteString], as: ShortcutProfile.self)
            profiles = try await core.call("library", as: [Profile].self)
            search = ""
            selectedID = target.id
            if target.quarantined {
                status = tr("Цей профіль заблоковано.")
            } else if profile?.running == true {
                status = tr("Minecraft уже запущено.")
            } else {
                shortcutProfile = target
            }
        } catch { status = error.localizedDescription }
    }

    private func beginCreateProfile() {
        Task {
            await perform {
                versions = try await core.call("game_versions", as: [String].self)
                version = versions.first ?? ""
                profileName = ""
                loader = "vanilla"
                librarySheet = .create
            }
        }
    }

    private func showAccounts() {
        Task {
            await perform {
                accounts = try await core.call("accounts", as: [Account].self)
                accountToRemove = nil
                librarySheet = .accounts
            }
        }
    }

    @ViewBuilder private func libraryEditor(_ sheet: LibrarySheet) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            if sheet == .accounts {
                Text(tr("Акаунти Minecraft")).font(.title2.bold())
                if accounts.isEmpty { Text(tr("Увійдіть у Microsoft або додайте офлайн-акаунт.")) }
                ForEach(accounts) { account in
                    HStack {
                        Text(account.name)
                        Spacer()
                        if account.selected {
                            Label(tr("Основний"), systemImage: "checkmark")
                        } else {
                            Button(tr("Зробити основним")) {
                                librarySheet = nil
                                mutate("use_account", ["id": account.id], message: tr("Основний акаунт · {0}", String(describing: account.name)))
                            }
                        }
                        Button(tr("Видалити")) { accountToRemove = account }
                    }
                }
                Button(tr("Готово")) { librarySheet = nil }.keyboardShortcut(.defaultAction)
            } else {
                Text(sheet == .create ? tr("Новий профіль") : tr("Перейменувати профіль")).font(.title2.bold())
                Form {
                    TextField(tr("Назва"), text: $profileName)
                    if sheet == .create {
                        Picker("Minecraft", selection: $version) {
                            if versions.isEmpty { Text(tr("Версій немає")).tag("") }
                            ForEach(versions, id: \.self) { Text($0).tag($0) }
                        }
                        Picker(tr("Завантажувач"), selection: $loader) {
                            Text("Vanilla").tag("vanilla")
                            Text("Fabric").tag("fabric")
                            Text("Forge").tag("forge")
                            Text("NeoForge").tag("neoforge")
                            Text("Quilt").tag("quilt")
                        }
                    }
                }
                HStack {
                    Spacer()
                    Button(tr("Скасувати")) { librarySheet = nil }.keyboardShortcut(.cancelAction)
                    Button(sheet == .create ? tr("Створити") : tr("Зберегти")) {
                        librarySheet = nil
                        if sheet == .create {
                            mutate("create_profile", ["name": profileName, "version": version, "loader": loader],
                                   message: tr("Профіль додано до черги."))
                        } else if let editedProfile {
                            mutate("rename_profile", ["id": editedProfile.id, "name": profileName])
                        }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(profileName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
                              (sheet == .create && version.isEmpty))
                }
            }
        }
        .padding(24)
        .frame(minWidth: 450)
        .alert(tr("Видалити акаунт?"), isPresented: Binding(
            get: { accountToRemove != nil },
            set: { if !$0 { accountToRemove = nil } }
        ), presenting: accountToRemove) { account in
            Button(tr("Видалити"), role: .destructive) {
                librarySheet = nil
                mutate("remove_account", ["id": account.id])
            }
            Button(tr("Скасувати"), role: .cancel) {}
        } message: { account in
            Text(account.name)
        }
    }

    private func jobRow(_ job: Job) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text([job.instance_id ?? tr("Модпак"),
                      InterfacePreferences.installLabel(job.status), InterfacePreferences.installLabel(job.phase)]
                    .joined(separator: " · "))
                    .frame(maxWidth: .infinity, alignment: .leading)
                if job.can_pause {
                    jobAction(job, job.paused ? "resume" : "pause", job.paused ? tr("Продовжити") : tr("Пауза"))
                }
                if job.can_cancel { jobAction(job, "cancel", tr("Скасувати")) }
                if ["failed", "interrupted"].contains(job.status) {
                    jobAction(job, "retry", tr("Повторити"))
                }
            }
            if let progress = job.progress { ProgressView(value: progress) }
            if let error = job.error { Text(error).font(.callout) }
            if ["failed", "interrupted", "canceled"].contains(job.status) {
                Button(tr("Діагностика")) { diagnosticsJob = job }
            }
        }
        .padding(.vertical, 4)
    }

    private func jobAction(_ job: Job, _ action: String, _ label: String) -> some View {
        Button(label) { mutate("job", ["id": job.id, "action": action]) }
            .disabled(busy)
    }

    @MainActor private func refresh() async throws {
        profiles = try await core.call("library", as: [Profile].self)
        if profile == nil { selectedID = profiles.first?.id }
        jobs = try await core.call("queue", as: [Job].self)
        try await loadSelection()
    }

    @MainActor private func loadSelection() async throws {
        guard let profile else {
            cover = nil
            coverKey = nil
            log = ""
            logProfile = nil
            archivedLogs = []
            selectedLog = nil
            loadedTab = nil
            return
        }
        let requestedCoverKey = "\(profile.id)/\(profile.running)/\(profile.last_played ?? "")"
        if coverKey != requestedCoverKey {
            let path = try await core.call("cover", ["id": profile.id], as: String?.self)
            guard selectedID == profile.id else { return }
            cover = path.flatMap { NSImage(contentsOfFile: $0) }
            coverKey = requestedCoverKey
        }
        let requestedTab = tab
        let key = tabKey(profile, requestedTab)
        switch requestedTab {
        case .content where key != loadedTab:
            let entries = try await core.call("content", ["id": profile.id], as: [ContentEntry].self)
            guard selectedID == profile.id, tab == requestedTab else { return }
            content = entries
            loadedTab = key
        case .worlds where key != loadedTab:
            let entries = try await core.call("worlds", ["id": profile.id], as: [WorldEntry].self)
            guard selectedID == profile.id, tab == requestedTab else { return }
            worlds = entries
            loadedTab = key
        case .screenshots where key != loadedTab:
            let entries = try await core.call("screenshots", ["id": profile.id], as: [Screenshot].self)
            guard selectedID == profile.id, tab == requestedTab else { return }
            screenshots = entries
            selectedScreenshot = entries.first?.id ?? ""
            updateScreenshotPreview()
            loadedTab = key
        case .log:
            try await loadLog(profile)
        default:
            break
        }
    }

    @MainActor private func loadLog(_ profile: Profile) async throws {
        if logProfile != profile.id || logWasRunning != profile.running {
            let previous = logProfile == profile.id ? selectedLog : nil
            log = ""
            let entries = try await core.call("log_history", ["id": profile.id], as: [ArchivedLog].self)
            guard selectedID == profile.id, tab == .log else { return }
            archivedLogs = entries
            selectedLog = previous.flatMap { entries.contains($0) ? $0 : nil }
            displayedLog = nil
            logProfile = profile.id
            logWasRunning = profile.running
        }

        let source = selectedLog
        let output: String
        if let source {
            if source == displayedLog { return }
            output = try await core.call("read_log", [
                "id": profile.id, "kind": source.log_type, "filename": source.filename
            ], as: String.self)
        } else {
            output = try await core.call("log", ["id": profile.id], as: String.self)
        }
        if selectedID == profile.id, selectedLog == source {
            log = output
            displayedLog = source
        }
    }

    @MainActor private func perform(_ action: () async throws -> Void) async {
        guard !busy else { return }
        busy = true
        defer { busy = false }
        do { try await action() } catch { status = error.localizedDescription }
    }

    private func mutate(_ method: String, _ values: [String: String], message: String = "") {
        Task {
            await perform {
                if method == "play" || method == "open_world" { status = tr("Готуємо Minecraft…") }
                _ = try await core.call(method, values, as: EmptyReply.self)
                loadedTab = nil
                status = message
                try await refresh()
            }
        }
    }

    private func importPack() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "mrpack") ?? .data]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        mutate("import", ["path": url.path], message: tr("Модпак додано до черги."))
    }

    private func beginMicrosoft() {
        Task {
            await perform {
                let url = try await core.call("begin_login", as: URL.self)
                loginURL = url
                showMicrosoft = true
            }
        }
    }

    private func completeMicrosoft(_ code: String?) {
        guard let code else {
            showMicrosoft = false
            return
        }
        loginURL = nil
        showMicrosoft = false
        Task {
            do {
                _ = try await core.call("finish_login", ["code": code], as: EmptyReply.self)
                status = tr("Вхід у Microsoft виконано.")
            } catch { status = error.localizedDescription }
        }
    }

    private func cancelMicrosoft() {
        guard loginURL != nil else { return }
        loginURL = nil
        Task {
            do { _ = try await core.call("cancel_login", as: EmptyReply.self) }
            catch { status = error.localizedDescription }
        }
    }
}

private struct MicrosoftWebView: NSViewRepresentable {
    let url: URL
    let complete: (String?) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(complete: complete) }

    func makeNSView(context: Context) -> WKWebView {
        let web = WKWebView()
        web.navigationDelegate = context.coordinator
        web.load(URLRequest(url: url))
        return web
    }

    func updateNSView(_ web: WKWebView, context: Context) {}

    final class Coordinator: NSObject, WKNavigationDelegate {
        let complete: (String?) -> Void

        init(complete: @escaping (String?) -> Void) { self.complete = complete }

        func webView(
            _ webView: WKWebView,
            decidePolicyFor navigationAction: WKNavigationAction,
            decisionHandler: @escaping (WKNavigationActionPolicy) -> Void
        ) {
            guard let url = navigationAction.request.url,
                  url.scheme == "https", url.host == "login.live.com",
                  url.path == "/oauth20_desktop.srf" else {
                decisionHandler(.allow)
                return
            }
            let code = URLComponents(url: url, resolvingAgainstBaseURL: false)?
                .queryItems?.first { $0.name == "code" }?.value
            decisionHandler(.cancel)
            complete(code)
        }
    }
}
