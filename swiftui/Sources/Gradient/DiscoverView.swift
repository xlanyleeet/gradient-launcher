import SwiftUI

struct DiscoverView: View {
    let core: CoreClient
    let profileID: String?
    @Binding var incomingLink: String?
    let back: () -> Void
    let installed: () -> Void

    private let categories = [
        ("modpack", "Збірки"), ("mod", "Моди"), ("resourcepack", "Пакети ресурсів"),
        ("datapack", "Пакети даних"), ("shader", "Шейдери"), ("server", "Сервери"),
    ]

    private struct Hit: Decodable, Identifiable {
        let id: String
        let name: String
        let description: String
        let author: String
        let icon: URL?
        let address: String?
        let url: URL?
    }

    private struct LinkedProject: Decodable {
        let id: String
        let name: String
        let kind: String
        let preferred_version: String?
    }

    private struct ProjectSelection: Identifiable {
        let id: String
        let name: String
        let kind: String
        let profile: String?
        let preferredVersion: String?
    }

    private struct Version: Decodable, Identifiable {
        let id: String
        let name: String
        let channel: String
    }

    private struct Page: Decodable {
        let hits: [Hit]
        let next_offset: UInt32?
    }

    private struct ProjectDetails: Decodable {
        let description: String
        let body: String
        let license: String
        let downloads: UInt32
        let updated: String
        let url: URL
        let gallery: URL?
    }

    @State private var query = ""
    @State private var kind = "modpack"
    @State private var sort = ""
    @State private var gameVersion = ""
    @State private var loader = ""
    @State private var appliedFilters: [String: String] = [:]
    @State private var submittedQuery = ""
    @State private var submittedKind = "modpack"
    @State private var target: String?
    @State private var results: [Hit] = []
    @State private var nextOffset: UInt32?
    @State private var currentOffset: UInt32 = 0
    @State private var selected: ProjectSelection?
    @State private var selectedServer: Hit?
    @State private var linkPrompt = false
    @State private var linkText = ""
    @State private var projectDetails: ProjectDetails?
    @State private var versions: [Version] = []
    @State private var version = ""
    @State private var status = ""
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Button(action: back) {
                    Image(systemName: "chevron.left").frame(width: 22, height: 22)
                }
                .accessibilityLabel(Text(tr("До бібліотеки")))
                .help(tr("До бібліотеки"))
                Text(tr("Каталог Modrinth")).font(.title2.bold())
            }
            categoryBar
            HStack {
                TextField(tr("Знайти на Modrinth"), text: $query)
                    .onSubmit { beginSearch() }
                Button(tr("Пошук"), systemImage: "magnifyingglass", action: beginSearch)
                    .disabled(busy)
                Button(tr("Відкрити посилання"), systemImage: "link") { linkPrompt = true }
                    .disabled(busy)
            }
            DisclosureGroup(tr("Фільтри та сортування")) {
                Form {
                    Picker(tr("Сортувати"), selection: $sort) {
                        Text(tr("Автоматично")).tag("")
                        Text(tr(kind == "server" ? "За активністю гравців" : "За завантаженнями")).tag("downloads")
                        Text(tr("Найновіші")).tag("newest")
                        Text(tr("Нещодавно оновлені")).tag("updated")
                    }
                    if kind == "modpack" {
                        TextField(tr("Версія Minecraft"), text: $gameVersion, prompt: Text("1.21.1"))
                        Picker(tr("Завантажувач"), selection: $loader) {
                            Text(tr("Усі")).tag("")
                            Text("Fabric").tag("fabric")
                            Text("Forge").tag("forge")
                            Text("NeoForge").tag("neoforge")
                            Text("Quilt").tag("quilt")
                        }
                    }
                    Button(tr("Застосувати")) { beginSearch() }
                }
                .disabled(busy)
            }
            List(results) { hit in
                Button {
                    if submittedKind == "server" { selectedServer = hit }
                    else { showVersions(hit) }
                } label: {
                    HStack(alignment: .top, spacing: 12) {
                        AsyncImage(url: hit.icon?.scheme == "https" ? hit.icon : nil) { image in
                            image.resizable().scaledToFit()
                        } placeholder: {
                            Image(systemName: "shippingbox").foregroundStyle(.secondary)
                        }
                        .frame(width: 44, height: 44)
                        .accessibilityHidden(true)
                        VStack(alignment: .leading, spacing: 4) {
                            Text(hit.name).font(.headline)
                            Text(hit.author).foregroundStyle(.secondary)
                            Text(hit.description)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .disabled(busy)
            }
            HStack {
                if busy { ProgressView().controlSize(.small) }
                if !status.isEmpty { Text(status) }
                Spacer()
                if currentOffset > 0 {
                    Button(tr("Попередня сторінка")) {
                        let offset = currentOffset >= 20 ? currentOffset - 20 : 0
                        Task { await loadPage(offset) }
                    }
                    .disabled(busy)
                }
                if let nextOffset {
                    Button(tr("Наступна сторінка")) { Task { await loadPage(nextOffset) } }
                        .disabled(busy)
                }
            }
        }
        .padding()
        .sheet(item: $selectedServer, content: serverDetails)
        .task(id: incomingLink) {
            if let incomingLink {
                while busy && !Task.isCancelled {
                    try? await Task.sleep(for: .milliseconds(75))
                }
                guard !Task.isCancelled else { return }
                await openLink(incomingLink)
                if self.incomingLink == incomingLink { self.incomingLink = nil }
            } else if selected == nil && results.isEmpty && status.isEmpty {
                await loadPage(0)
            }
        }
        .alert(tr("Відкрити посилання Modrinth"), isPresented: $linkPrompt) {
            TextField("https://modrinth.com/modpack/…", text: $linkText)
            Button(tr("Відкрити")) {
                let url = linkText
                linkText = ""
                Task { await openLink(url) }
            }
            Button(tr("Скасувати"), role: .cancel) {}
        }
        .sheet(item: $selected) { hit in
            VStack(alignment: .leading, spacing: 16) {
                Text(hit.name).font(.title2.bold())
                if let details = projectDetails {
                    Text(details.description)
                    Text(tr("{0} завантажень · {1} · Оновлено {2}", String(describing: details.downloads.formatted()), String(describing: details.license), String(describing: details.updated)))
                        .font(.callout).foregroundStyle(.secondary)
                    Link(tr("Відкрити на Modrinth"), destination: details.url)
                    if let gallery = details.gallery, gallery.scheme == "https" {
                        AsyncImage(url: gallery) { image in
                            image.resizable().scaledToFit()
                        } placeholder: {
                            ProgressView().controlSize(.small)
                        }
                        .frame(maxHeight: 180)
                        .accessibilityLabel(Text(tr("Зображення проєкту")))
                    }
                    if !details.body.isEmpty {
                        ScrollView {
                            Text(details.body)
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .frame(maxHeight: 180)
                    }
                }
                if versions.isEmpty {
                    Text(hit.kind != "modpack" && hit.profile == nil ?
                         tr("Виберіть профіль для встановлення контенту.") : tr("Сумісних версій немає."))
                } else {
                    Picker(tr("Версія"), selection: $version) {
                        if version.isEmpty { Text(tr("Виберіть іншу версію")).tag("") }
                        ForEach(versions) { entry in Text("\(entry.name) · \(entry.channel)").tag(entry.id) }
                    }
                }
                HStack {
                    Spacer()
                    Button(tr("Закрити")) { selected = nil }.keyboardShortcut(.cancelAction)
                    Button(tr("Встановити")) {
                        let selectedVersion = version
                        selected = nil
                        Task { await install(hit, version: selectedVersion) }
                    }
                    .keyboardShortcut(.defaultAction)
                    .disabled(version.isEmpty)
                }
            }
            .padding(24)
            .frame(width: 460)
        }
    }

    private var categoryBar: some View {
        ScrollView(.horizontal) {
            HStack(spacing: 2) {
                ForEach(categories.indices, id: \.self) { index in
                    let (value, title) = categories[index]
                    Button {
                        guard kind != value else { return }
                        kind = value
                        beginSearch()
                    } label: {
                        Text(tr(title)).font(.callout.weight(.semibold))
                            .padding(.horizontal, 12)
                            .padding(.vertical, 7)
                            .foregroundStyle(kind == value ? Color.accentColor : Color.primary)
                            .background(kind == value ? Color.accentColor.opacity(0.20) : .clear, in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(kind == value ? .isSelected : [])
                }
            }
            .padding(4)
            .background(.quaternary, in: Capsule())
        }
        .scrollIndicators(.hidden)
        .accessibilityLabel(Text(tr("Тип контенту")))
        .disabled(busy)
    }

    private func serverDetails(_ server: Hit) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(server.name).font(.title2.bold())
            Text(server.description)
            if let address = server.address { Text(address).textSelection(.enabled) }
            if let url = server.url { Link(tr("Відкрити на Modrinth"), destination: url) }
            HStack {
                Spacer()
                Button(tr("Закрити")) { selectedServer = nil }.keyboardShortcut(.cancelAction)
                if let address = server.address, let profileID {
                    Button(tr("Додати сервер")) {
                        selectedServer = nil
                        Task { await saveServer(server, address: address, profile: profileID) }
                    }
                }
            }
        }
        .padding(24)
        .frame(width: 460)
    }

    private func beginSearch() {
        guard !busy else { return }
        results = []
        nextOffset = nil
        currentOffset = 0
        submittedQuery = query
        submittedKind = kind
        target = kind == "modpack" || kind == "server" ? nil : profileID
        appliedFilters = [:]
        if !sort.isEmpty { appliedFilters["sort"] = sort }
        if kind == "modpack" {
            if !gameVersion.isEmpty { appliedFilters["game_version"] = gameVersion }
            if !loader.isEmpty { appliedFilters["loader"] = loader }
        }
        Task { await loadPage(0) }
    }

    private func request(_ action: String, _ extra: [String: String]) -> [String: String] {
        var values = extra
        values["action"] = action
        values["kind"] = submittedKind
        values["profile"] = target
        return values
    }

    @MainActor private func loadPage(_ offset: UInt32) async {
        await run(tr("Шукаємо…")) {
            let filters = appliedFilters.merging([
                "query": submittedQuery, "offset": String(offset)
            ]) { _, current in current }
            let page = try await core.call("discover", request("search", filters), as: Page.self)
            results = page.hits
            currentOffset = offset
            nextOffset = page.next_offset
            status = results.isEmpty ? tr("Нічого не знайдено.") : ""
        }
    }

    private func showVersions(_ hit: Hit) {
        Task {
            await run(tr("Завантажуємо проєкт…")) {
                try await loadProject(.init(id: hit.id, name: hit.name, kind: submittedKind,
                                            profile: target, preferredVersion: nil))
            }
        }
    }

    @MainActor private func openLink(_ url: String) async {
        await run(tr("Відкриваємо посилання…")) {
            let linked = try await core.call("discover", ["action": "resolve_link", "url": url], as: LinkedProject.self)
            try await loadProject(.init(id: linked.id, name: linked.name, kind: linked.kind,
                                        profile: linked.kind == "modpack" ? nil : profileID,
                                        preferredVersion: linked.preferred_version))
        }
    }

    @MainActor private func loadProject(_ project: ProjectSelection) async throws {
        var values = ["project": project.id, "kind": project.kind, "action": "project"]
        projectDetails = try await core.call("discover", values, as: ProjectDetails.self)
        values["action"] = "versions"
        values["profile"] = project.profile
        if project.kind != "modpack" && project.profile == nil {
            versions = []
        } else {
            versions = try await core.call("discover", values, as: [Version].self)
        }
        if let preferred = project.preferredVersion {
            version = versions.first(where: { $0.id == preferred })?.id ?? ""
        } else {
            version = versions.first?.id ?? ""
        }
        selected = project
        status = ""
    }

    @MainActor private func install(_ hit: ProjectSelection, version: String) async {
        await run(tr("Встановлюємо…")) {
            var values = ["action": "install", "project": hit.id, "version": version, "kind": hit.kind]
            values["profile"] = hit.profile
            _ = try await core.call("discover", values, as: EmptyReply.self)
            installed()
            status = hit.kind == "modpack" ? tr("Модпак додано до черги.") : tr("Контент встановлено.")
        }
    }

    @MainActor private func saveServer(_ server: Hit, address: String, profile: String) async {
        await run(tr("Зберігаємо сервер…")) {
            _ = try await core.call("save_server", ["id": profile, "name": server.name, "address": address], as: EmptyReply.self)
            installed()
            status = tr("Сервер додано.")
        }
    }

    @MainActor private func run(_ message: String, action: () async throws -> Void) async {
        guard !busy else { return }
        busy = true
        status = message
        defer { busy = false }
        do { try await action() } catch { status = error.localizedDescription }
    }
}
