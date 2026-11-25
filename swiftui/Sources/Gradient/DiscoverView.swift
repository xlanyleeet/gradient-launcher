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
        var author: String? = nil
    }

    private struct Version: Decodable, Identifiable {
        let id: String
        let name: String
        let channel: String
        let game_versions: [String]?

        var label: String {
            guard let game_versions, !game_versions.isEmpty else { return "\(name) · \(channel)" }
            return "\(name) · Minecraft \(game_versions.joined(separator: ", ")) · \(channel)"
        }
    }

    private struct Page: Decodable {
        let hits: [Hit]
        let next_offset: UInt32?
    }

    private struct ProjectDetails: Decodable {
        let description: String
        let body: String
        let license: String
        let license_id: String?
        let downloads: UInt32
        let updated: String
        let url: URL
        let gallery: URL?
        let gallery_images: [URL]?
        let gallery_originals: [URL]?
        let icon: URL?
    }

    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.openURL) private var openURL
    @State private var source = "modrinth"
    @State private var submittedSource = "modrinth"
    @State private var query = ""
    @State private var kind = "modpack"
    @State private var sort = ""
    @State private var gameVersion = ""
    @State private var loader = ""
    @State private var appliedFilters: [String: String] = [:]
    @State private var submittedQuery = ""
    @State private var submittedKind = "modpack"
    @State private var target: String?
    @State private var resultsRevision = 0
    @State private var installing = false
    @State private var showingCompactHeader = false
    @State private var screenshotGallery: ScreenshotGallery?
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

    private var showingProject: Bool { selected != nil || selectedServer != nil }

    var body: some View {
        ZStack {
            catalog
                .opacity(showingProject ? 0 : 1)
                .allowsHitTesting(!showingProject)
                .accessibilityHidden(showingProject)
            if let selected {
                projectPage(selected).transition(.opacity)
            } else if let selectedServer {
                ScrollView { serverDetails(selectedServer).padding(24) }
                    .transition(.opacity)
            }
        }
        .animation(reduceMotion ? nil : .default, value: showingProject)
        .sheet(item: $screenshotGallery) { gallery in
            ScreenshotViewer(urls: gallery.urls, position: gallery.position)
        }
        .toolbar {
            ToolbarItem(placement: .navigation) {
                Button(tr("Назад"), systemImage: "chevron.left") {
                    if selected != nil || selectedServer != nil {
                        selected = nil
                        selectedServer = nil
                        status = ""
                    } else { back() }
                }
                .labelStyle(.iconOnly)
            }
        }
        .task(id: incomingLink) {
            if let incomingLink {
                while busy && !Task.isCancelled { try? await Task.sleep(for: .milliseconds(75)) }
                guard !Task.isCancelled else { return }
                selectedServer = nil
                await openLink(incomingLink)
                if self.incomingLink == incomingLink { self.incomingLink = nil }
            } else if selected == nil && results.isEmpty && status.isEmpty {
                await loadPage(0)
            }
        }
    }

    private var catalog: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text(tr("Каталог")).font(.title2.bold())
                Picker(tr("Джерело каталогу"), selection: $source) {
                    Text("Modrinth").tag("modrinth")
                    Text("CurseForge").tag("curseforge")
                }
                .pickerStyle(.segmented)
                .disabled(busy)
                .onChange(of: source) {
                    if source == "curseforge", kind != "mod", kind != "modpack" { kind = "modpack" }
                    beginSearch()
                }
            }
            categoryBar
            HStack {
                TextField(tr(source == "curseforge" ? "Знайти на CurseForge" : "Знайти на Modrinth"), text: $query)
                    .onSubmit { beginSearch() }
                Button(tr("Пошук"), systemImage: "magnifyingglass", action: beginSearch)
                    .disabled(busy)
                if source == "modrinth" {
                    Button(tr("Відкрити посилання"), systemImage: "link") { linkPrompt = true }
                        .disabled(busy)
                }
            }
            DisclosureGroup(tr("Фільтри та сортування")) {
                Form {
                    Picker(tr("Сортувати"), selection: $sort) {
                        Text(tr("Автоматично")).tag("")
                        Text(tr(kind == "server" ? "За активністю гравців" : "За завантаженнями")).tag("downloads")
                        Text(tr("Найновіші")).tag("newest")
                        Text(tr("Нещодавно оновлені")).tag("updated")
                    }
                    if source == "curseforge" || kind == "modpack" {
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
            ZStack {
                resultList
                    .id(resultsRevision)
                    .transition(.opacity)
            }
            .animation(reduceMotion ? nil : .default, value: resultsRevision)
            HStack {
                if busy { ProgressView().controlSize(.small) }
                if !status.isEmpty { Text(status) }
                Spacer()
                pagination
            }
        }
        .padding()
        .alert(tr("Відкрити посилання Modrinth"), isPresented: $linkPrompt) {
            TextField("https://modrinth.com/modpack/…", text: $linkText)
            Button(tr("Відкрити")) {
                let url = linkText
                linkText = ""
                Task { await openLink(url) }
            }
            Button(tr("Скасувати"), role: .cancel) {}
        }
    }

    private var pagination: some View {
        let number = currentOffset == 0 ? 1 : (currentOffset + 19) / 20 + 1
        return HStack(spacing: 6) {
            if currentOffset > 0 {
                Button((number - 1).formatted()) {
                    let offset = currentOffset >= 20 ? currentOffset - 20 : 0
                    Task { await loadPage(offset) }
                }
                    .accessibilityLabel(Text(tr("Сторінка {0}", (number - 1).formatted())))
            }
            if !results.isEmpty || currentOffset > 0 || nextOffset != nil {
                Text(number.formatted()).fontWeight(.semibold).frame(minWidth: 28)
                    .accessibilityLabel(Text(tr("Поточна сторінка {0}", number.formatted())))
            }
            if let nextOffset {
                Button((number + 1).formatted()) { Task { await loadPage(nextOffset) } }
                    .accessibilityLabel(Text(tr("Сторінка {0}", (number + 1).formatted())))
            }
        }
        .disabled(busy)
    }

    private func projectPage(_ hit: ProjectSelection) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                ZStack(alignment: .bottomLeading) {
                    if let cover = projectDetails?.gallery, cover.scheme == "https" {
                        GeometryReader { geometry in
                            AsyncImage(url: cover) { image in image.resizable().scaledToFill() } placeholder: { Color.clear }
                                .frame(width: geometry.size.width, height: geometry.size.height)
                                .clipped()
                        }
                    }
                    VStack(alignment: .leading, spacing: 10) {
                        if let icon = projectDetails?.icon, icon.scheme == "https" {
                            AsyncImage(url: icon) { image in image.resizable().scaledToFit() } placeholder: {
                                Image(systemName: "shippingbox")
                            }
                            .frame(width: 68, height: 68)
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                        }
                        titleWithModrinthLink(hit.name, url: projectDetails?.url, font: .largeTitle.bold())
                        if let details = projectDetails {
                            Text(details.description)
                            projectMetadata(details)
                        }
                        if versions.isEmpty {
                            Text(hit.kind != "modpack" && hit.profile == nil ?
                                 tr("Виберіть профіль для встановлення контенту.") : tr("Сумісних версій немає."))
                        } else {
                            projectReleaseControls(hit)
                        }
                        if !status.isEmpty { Text(status).font(.callout) }
                    }
                    .padding(18)
                    .frame(maxWidth: 440, alignment: .leading)
                    .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 16))
                    .padding(16)
                }
                .frame(minHeight: 320)
                .background(.quaternary)
                .clipShape(RoundedRectangle(cornerRadius: 18))
                .onScrollVisibilityChange(threshold: 0.1) { visible in
                    showingCompactHeader = !visible
                }
                if let details = projectDetails {
                    let originals = details.gallery_originals ?? []
                    let gallery = (details.gallery_images ?? []).enumerated().compactMap { index, thumbnail -> (URL, URL)? in
                        guard thumbnail.scheme == "https" else { return nil }
                        let original = originals.indices.contains(index) ? originals[index] : thumbnail
                        return (thumbnail, original.scheme == "https" ? original : thumbnail)
                    }
                    if !gallery.isEmpty {
                        Text(tr("Скриншоти")).font(.title2.bold())
                        ScrollView(.horizontal) {
                            HStack(spacing: 12) {
                                ForEach(Array(gallery.enumerated()), id: \.offset) { index, item in
                                    Button {
                                        screenshotGallery = ScreenshotGallery(urls: gallery.map { $0.1 }, position: index)
                                    } label: {
                                        AsyncImage(url: item.0) { image in image.resizable().scaledToFill() } placeholder: {
                                            Color.secondary.opacity(0.12)
                                        }
                                        .frame(width: 280, height: 158)
                                        .clipShape(RoundedRectangle(cornerRadius: 12))
                                    }
                                    .buttonStyle(.plain)
                                    .accessibilityLabel(Text(tr("Відкрити скриншот {0}", String(index + 1))))
                                }
                            }
                        }
                    }
                    if !details.body.isEmpty {
                        Text(tr("Опис")).font(.title2.bold())
                        ProjectDescription(details.body)
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(24)
        }
        .overlay(alignment: .top) {
            if showingCompactHeader {
                compactProjectHeader(hit)
                    .padding(.horizontal, 24)
                    .padding(.top, 8)
                    .transition(.opacity)
            }
        }
        .animation(reduceMotion ? nil : .default, value: showingCompactHeader)
    }

    private func projectReleaseControls(_ hit: ProjectSelection) -> some View {
        HStack(spacing: 8) {
            Picker(tr("Версія"), selection: $version) {
                if version.isEmpty { Text(tr("Виберіть іншу версію")).tag("") }
                ForEach(versions) { entry in Text(entry.label).tag(entry.id) }
            }
            .labelsHidden()
            .help(versions.first(where: { $0.id == version })?.label ?? "")
            .disabled(busy)
            ZStack(alignment: .leading) {
                Button(tr("Встановити")) {
                    let chosenVersion = version
                    Task { await install(hit, version: chosenVersion) }
                }
                .buttonStyle(.borderedProminent)
                .disabled(busy || version.isEmpty)
                .opacity(installing ? 0 : 1)
                .accessibilityHidden(installing)
                if installing {
                    ProgressView().controlSize(.small)
                        .accessibilityLabel(Text(tr("Встановлюємо…")))
                        .transition(.opacity)
                }
            }
            .animation(reduceMotion ? nil : .default, value: installing)
        }
        .padding(.top, 20)
    }

    private func projectMetadata(_ details: ProjectDetails) -> some View {
        let locale = Locale(identifier: InterfacePreferences.language)
        let downloads = details.downloads.formatted(.number.locale(locale))
        let compactDownloads = details.downloads.formatted(
            .number.notation(.compactName).precision(.fractionLength(0...1)).locale(locale)
        )
        return ScrollView(.horizontal) {
            HStack(spacing: 14) {
                metadataLabel(compactDownloads, symbol: "arrow.down.to.line", help: tr("{0} завантажень", downloads))
                metadataLabel(details.license_id ?? tr("Ліцензія"), symbol: "doc.text",
                              help: tr("Ліцензія: {0}", details.license))
                metadataLabel(details.updated, symbol: "calendar", help: tr("Оновлено {0}", details.updated))
            }
            .font(.callout)
            .foregroundStyle(.secondary)
        }
        .scrollIndicators(.hidden)
    }

    private func metadataLabel(_ text: String, symbol: String, help: String) -> some View {
        Label(text, systemImage: symbol)
            .lineLimit(1)
            .help(help)
            .accessibilityLabel(Text(help))
    }

    private func titleWithModrinthLink(_ title: String, url: URL?, font: Font, lineLimit: Int? = nil) -> some View {
        HStack(spacing: 8) {
            Text(title).font(font).lineLimit(lineLimit)
            if let url, url.scheme == "https" {
                Link(destination: url) {
                    Image(systemName: "arrow.up.right.square")
                        .frame(width: 28, height: 28)
                }
                .help(tr("Відкрити на Modrinth"))
                .accessibilityLabel(Text(tr("Відкрити на Modrinth")))
            }
        }
    }

    private func compactProjectHeader(_ hit: ProjectSelection) -> some View {
        HStack(spacing: 12) {
            if let icon = projectDetails?.icon, icon.scheme == "https" {
                AsyncImage(url: icon) { image in image.resizable().scaledToFit() } placeholder: {
                    Image(systemName: "shippingbox")
                }
                .frame(width: 44, height: 44)
                .clipShape(RoundedRectangle(cornerRadius: 8))
            }
            VStack(alignment: .leading) {
                titleWithModrinthLink(hit.name, url: projectDetails?.url, font: .headline, lineLimit: 1)
                if let author = hit.author { Text(author).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                if !status.isEmpty { Text(status).font(.caption).lineLimit(1) }
            }
            Spacer()
            if installing {
                ProgressView().controlSize(.small)
            } else if !versions.isEmpty {
                Button(tr("Встановити")) {
                    let chosenVersion = version
                    Task { await install(hit, version: chosenVersion) }
                }
                .buttonStyle(.borderedProminent)
                .disabled(busy || version.isEmpty)
            }
        }
        .padding(12)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
    }

    private struct ScreenshotGallery: Identifiable {
        let id = UUID()
        let urls: [URL]
        let position: Int
    }

    private struct ScreenshotViewer: View {
        let urls: [URL]
        @State var position: Int
        @Environment(\.dismiss) private var dismiss

        var body: some View {
            VStack(spacing: 14) {
                AsyncImage(url: urls[position]) { phase in
                    if let image = phase.image {
                        image.resizable().scaledToFit()
                    } else if phase.error != nil {
                        Text(tr("Не вдалося завантажити скриншот."))
                    } else {
                        ProgressView()
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                HStack {
                    Button(tr("Попередній скриншот"), systemImage: "chevron.left") { position -= 1 }
                        .labelStyle(.iconOnly)
                        .disabled(position == 0)
                    Text(tr("{0} із {1}", String(position + 1), String(urls.count)))
                    Button(tr("Наступний скриншот"), systemImage: "chevron.right") { position += 1 }
                        .labelStyle(.iconOnly)
                        .disabled(position + 1 == urls.count)
                    Spacer()
                    Button(tr("Закрити")) { dismiss() }.keyboardShortcut(.cancelAction)
                }
            }
            .padding(20)
            .frame(minWidth: 520, idealWidth: 820, minHeight: 360, idealHeight: 540)
        }
    }

    private var resultList: some View {
        List(results) { hit in
            Button {
                if submittedSource == "curseforge" {
                    if let url = hit.url, url.scheme == "https", url.host == "www.curseforge.com" { openURL(url) }
                } else if submittedKind == "server" { selectedServer = hit }
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
    }

    private var categoryBar: some View {
        let available = source == "curseforge" ? Array(categories.prefix(2)) : categories
        return ScrollView(.horizontal) {
            HStack(spacing: 2) {
                ForEach(available.indices, id: \.self) { index in
                    let (value, title) = available[index]
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
            if let icon = server.icon, icon.scheme == "https" {
                AsyncImage(url: icon) { image in image.resizable().scaledToFit() } placeholder: {
                    Image(systemName: "network")
                }
                .frame(width: 68, height: 68)
                .clipShape(RoundedRectangle(cornerRadius: 12))
            }
            titleWithModrinthLink(server.name, url: server.url, font: .largeTitle.bold())
            if !server.author.isEmpty { Text(server.author).foregroundStyle(.secondary) }
            Text(server.description)
            if let address = server.address { Text(address).textSelection(.enabled) }
            HStack {
                Spacer()
                if let address = server.address, let profileID {
                    Button(tr("Додати сервер")) {
                        Task { await saveServer(server, address: address, profile: profileID) }
                    }
                    .disabled(busy)
                }
            }
            if !status.isEmpty { Text(status) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func beginSearch() {
        guard !busy else { return }
        nextOffset = nil
        currentOffset = 0
        submittedQuery = query
        submittedKind = kind
        submittedSource = source
        target = kind == "modpack" || kind == "server" ? nil : profileID
        appliedFilters = ["source": source]
        if !sort.isEmpty { appliedFilters["sort"] = sort }
        if source == "curseforge" || kind == "modpack" {
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
            let page: Page
            do {
                page = try await core.call("discover", request("search", filters), as: Page.self)
            } catch {
                results = []
                nextOffset = nil
                currentOffset = 0
                throw error
            }
            if results.map(\.id) != page.hits.map(\.id) { resultsRevision += 1 }
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
                                            profile: target, preferredVersion: nil, author: hit.author))
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
        showingCompactHeader = false
        selected = project
        status = ""
    }

    @MainActor private func install(_ hit: ProjectSelection, version: String) async {
        await run(tr("Встановлюємо…")) {
            installing = true
            defer { installing = false }
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
