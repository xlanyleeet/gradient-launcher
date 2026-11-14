import SwiftUI

struct DiscoverView: View {
    let core: CoreClient
    let profileID: String?
    let installed: () -> Void

    private struct Hit: Decodable, Identifiable {
        let id: String
        let name: String
        let description: String
        let author: String
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

    @State private var query = ""
    @State private var kind = "modpack"
    @State private var submittedQuery = ""
    @State private var submittedKind = "modpack"
    @State private var target: String?
    @State private var results: [Hit] = []
    @State private var nextOffset: UInt32?
    @State private var selected: Hit?
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
            HStack {
                TextField("Знайти на Modrinth", text: $query)
                    .onSubmit { beginSearch() }
                Button("Пошук", systemImage: "magnifyingglass", action: beginSearch)
                    .disabled(busy)
            }
            List(results) { hit in
                Button { showVersions(hit) } label: {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(hit.name).font(.headline)
                        Text(hit.author).foregroundStyle(.secondary)
                        Text(hit.description)
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
                if let nextOffset {
                    Button("Наступна сторінка") { Task { await loadPage(nextOffset) } }
                        .disabled(busy)
                }
            }
        }
        .padding()
        .navigationTitle("Каталог Modrinth")
        .sheet(item: $selected) { hit in
            VStack(alignment: .leading, spacing: 16) {
                Text(hit.name).font(.title2.bold())
                Text(hit.description)
                if versions.isEmpty {
                    Text("Сумісних версій немає.")
                } else {
                    Picker("Версія", selection: $version) {
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
        guard kind != "mod" || profileID != nil else {
            status = "Спочатку виберіть профіль у бібліотеці."
            return
        }
        submittedQuery = query
        submittedKind = kind
        target = kind == "mod" ? profileID : nil
        results = []
        nextOffset = nil
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
            let page = try await core.call("discover", request("search", [
                "query": submittedQuery, "offset": String(offset)
            ]), as: Page.self)
            results = page.hits
            nextOffset = page.next_offset
            status = results.isEmpty ? "Нічого не знайдено." : ""
        }
    }

    private func showVersions(_ hit: Hit) {
        Task {
            await run("Завантажуємо версії…") {
                versions = try await core.call("discover", request("versions", ["project": hit.id]), as: [Version].self)
                version = versions.first?.id ?? ""
                selected = hit
                status = ""
            }
        }
    }

    @MainActor private func install(_ hit: Hit, version: String) async {
        await run("Встановлюємо…") {
            _ = try await core.call("discover", request("install", [
                "project": hit.id, "version": version
            ]), as: EmptyReply.self)
            installed()
            status = submittedKind == "modpack" ? "Модпак додано до черги." : "Мод встановлено."
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
