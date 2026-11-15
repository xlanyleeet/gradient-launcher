import SwiftUI

struct DiscoverView: View {
    let core: CoreClient
    let profileID: String?
    @Binding var incomingLink: String?
    let installed: () -> Void

    private struct Hit: Decodable, Identifiable {
        let id: String
        let name: String
        let description: String
        let author: String
        let icon: URL?
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
    @State private var linkPrompt = false
    @State private var linkText = ""
    @State private var projectDetails: ProjectDetails?
    @State private var versions: [Version] = []
    @State private var version = ""
    @State private var status = ""
    @State private var busy = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Picker("Тип контенту", selection: $kind) {
                Text("Модпаки").tag("modpack")
                Text("Моди для вибраного профілю").tag("mod")
            }
            .disabled(busy)
            .onChange(of: kind) { beginSearch() }
            HStack {
                TextField("Знайти на Modrinth", text: $query)
                    .onSubmit { beginSearch() }
                Button("Пошук", systemImage: "magnifyingglass", action: beginSearch)
                    .disabled(busy)
                Button("Відкрити посилання", systemImage: "link") { linkPrompt = true }
                    .disabled(busy)
            }
            DisclosureGroup("Фільтри та сортування") {
                Form {
                    Picker("Сортувати", selection: $sort) {
                        Text("Автоматично").tag("")
                        Text("За завантаженнями").tag("downloads")
                        Text("Найновіші").tag("newest")
                        Text("Нещодавно оновлені").tag("updated")
                    }
                    if kind == "modpack" {
                        TextField("Версія Minecraft", text: $gameVersion, prompt: Text("1.21.1"))
                        Picker("Завантажувач", selection: $loader) {
                            Text("Усі").tag("")
                            Text("Fabric").tag("fabric")
                            Text("Forge").tag("forge")
                            Text("NeoForge").tag("neoforge")
                            Text("Quilt").tag("quilt")
                        }
                    }
                    Button("Застосувати") { beginSearch() }
                }
                .disabled(busy)
            }
            List(results) { hit in
                Button { showVersions(hit) } label: {
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
                    Button("Попередня сторінка") {
                        let offset = currentOffset >= 20 ? currentOffset - 20 : 0
                        Task { await loadPage(offset) }
                    }
                    .disabled(busy)
                }
                if let nextOffset {
                    Button("Наступна сторінка") { Task { await loadPage(nextOffset) } }
                        .disabled(busy)
                }
            }
        }
        .padding()
        .navigationTitle("Каталог Modrinth")
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
        .alert("Відкрити посилання Modrinth", isPresented: $linkPrompt) {
            TextField("https://modrinth.com/modpack/…", text: $linkText)
            Button("Відкрити") {
                let url = linkText
                linkText = ""
                Task { await openLink(url) }
            }
            Button("Скасувати", role: .cancel) {}
        }
        .sheet(item: $selected) { hit in
            VStack(alignment: .leading, spacing: 16) {
                Text(hit.name).font(.title2.bold())
                if let details = projectDetails {
                    Text(details.description)
                    Text("\(details.downloads.formatted()) завантажень · \(details.license) · Оновлено \(details.updated)")
                        .font(.callout).foregroundStyle(.secondary)
                    Link("Відкрити на Modrinth", destination: details.url)
                    if let gallery = details.gallery, gallery.scheme == "https" {
                        AsyncImage(url: gallery) { image in
                            image.resizable().scaledToFit()
                        } placeholder: {
                            ProgressView().controlSize(.small)
                        }
                        .frame(maxHeight: 180)
                        .accessibilityLabel("Зображення проєкту")
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
                    Text(hit.kind == "mod" && hit.profile == nil ?
                         "Виберіть профіль у бібліотеці, щоб установити мод." : "Сумісних версій немає.")
                } else {
                    Picker("Версія", selection: $version) {
                        if version.isEmpty { Text("Виберіть іншу версію").tag("") }
                        ForEach(versions) { entry in Text("\(entry.name) · \(entry.channel)").tag(entry.id) }
                    }
                }
                HStack {
                    Spacer()
                    Button("Закрити") { selected = nil }.keyboardShortcut(.cancelAction)
                    Button("Встановити") {
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

    private func beginSearch() {
        guard !busy else { return }
        results = []
        nextOffset = nil
        currentOffset = 0
        guard kind != "mod" || profileID != nil else {
            status = "Спочатку виберіть профіль у бібліотеці."
            return
        }
        submittedQuery = query
        submittedKind = kind
        target = kind == "mod" ? profileID : nil
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
        await run("Шукаємо…") {
            let filters = appliedFilters.merging([
                "query": submittedQuery, "offset": String(offset)
            ]) { _, current in current }
            let page = try await core.call("discover", request("search", filters), as: Page.self)
            results = page.hits
            currentOffset = offset
            nextOffset = page.next_offset
            status = results.isEmpty ? "Нічого не знайдено." : ""
        }
    }

    private func showVersions(_ hit: Hit) {
        Task {
            await run("Завантажуємо проєкт…") {
                try await loadProject(.init(id: hit.id, name: hit.name, kind: submittedKind,
                                            profile: target, preferredVersion: nil))
            }
        }
    }

    @MainActor private func openLink(_ url: String) async {
        await run("Відкриваємо посилання…") {
            let linked = try await core.call("discover", ["action": "resolve_link", "url": url], as: LinkedProject.self)
            try await loadProject(.init(id: linked.id, name: linked.name, kind: linked.kind,
                                        profile: linked.kind == "mod" ? profileID : nil,
                                        preferredVersion: linked.preferred_version))
        }
    }

    @MainActor private func loadProject(_ project: ProjectSelection) async throws {
        var values = ["project": project.id, "kind": project.kind, "action": "project"]
        projectDetails = try await core.call("discover", values, as: ProjectDetails.self)
        values["action"] = "versions"
        values["profile"] = project.profile
        if project.kind == "mod" && project.profile == nil {
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
        await run("Встановлюємо…") {
            var values = ["action": "install", "project": hit.id, "version": version, "kind": hit.kind]
            values["profile"] = hit.profile
            _ = try await core.call("discover", values, as: EmptyReply.self)
            installed()
            status = hit.kind == "modpack" ? "Модпак додано до черги." : "Мод встановлено."
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
