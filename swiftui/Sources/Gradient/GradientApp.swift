import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WebKit

@main
struct GradientApp: App {
    var body: some Scene {
        Window("Gradient Launcher", id: "launcher") {
            LauncherView()
                .frame(minWidth: 760, minHeight: 560)
        }
        .defaultSize(width: 1120, height: 740)
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

private struct LauncherView: View {
    @State private var core = CoreClient()
    private let tint = Color(red: 0.66, green: 0.87, blue: 0.84)
    private let ink = Color(red: 0.07, green: 0.17, blue: 0.17)

    @State private var profiles: [Profile] = []
    @State private var jobs: [Job] = []
    @State private var selectedID: String?
    @State private var coverKey: String?
    @State private var cover: NSImage?
    @State private var log = ""
    @State private var status = ""
    @State private var busy = false
    @State private var queueVisible = false
    @State private var search = ""
    @State private var showLogin = false
    @State private var nickname = ""
    @State private var loginURL: URL?
    @State private var showMicrosoft = false

    private var profile: Profile? { profiles.first { $0.id == selectedID } }
    private var filteredProfiles: [Profile] {
        search.isEmpty ? profiles : profiles.filter { $0.name.localizedCaseInsensitiveContains(search) }
    }

    var body: some View {
        NavigationSplitView {
            List(selection: $selectedID) {
                Section("Профілі") {
                    ForEach(filteredProfiles) { entry in
                        Label(entry.name, systemImage: entry.running ? "play.circle.fill" : "cube")
                            .tag(entry.id)
                    }
                }
            }
            .navigationSplitViewColumnWidth(min: 180, ideal: 218)
            .safeAreaInset(edge: .bottom) {
                VStack(alignment: .leading, spacing: 8) {
                    Button("Увійти в Microsoft", systemImage: "person.crop.circle", action: beginMicrosoft)
                    Button("Офлайн-акаунт", systemImage: "person") { showLogin = true }
                }
                    .padding(12)
            }
        } detail: {
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    hero
                    VStack(alignment: .leading, spacing: 14) {
                        Divider()
                        Text(profile.map {
                            "Minecraft \($0.version)   ·   \($0.loader)   ·   Останній запуск  \($0.last_played ?? "—")"
                        } ?? "Виберіть профіль або імпортуйте модпак.")
                        .font(.callout)
                        Divider()
                        if !status.isEmpty { Text(status).font(.callout) }
                        Picker("Деталі", selection: $queueVisible) {
                            Text("Живий лог").tag(false)
                            Text("Завантаження").tag(true)
                        }
                        .labelsHidden()
                        .pickerStyle(.segmented)
                        if queueVisible {
                            if jobs.isEmpty { Text("Черга порожня.") }
                            ForEach(jobs) { job in jobRow(job) }
                        } else {
                            Text(log.isEmpty ? "Тут з’явиться вивід Minecraft." : log)
                                .font(.system(.callout, design: .monospaced))
                                .textSelection(.enabled)
                        }
                    }
                    .padding(.horizontal, 30)
                    .padding(.bottom, 30)
                }
            }
            .background(tint)
            .foregroundStyle(ink)
            .preferredColorScheme(.light)
            .toolbar {
                Button("Імпортувати .mrpack", systemImage: "plus", action: importPack)
                Button("Оновити", systemImage: "arrow.clockwise") {
                    coverKey = nil
                    Task { await perform(refresh) }
                }
            }
        }
        .searchable(text: $search, prompt: "Пошук профілів")
        .task {
            while !Task.isCancelled {
                await perform(refresh)
                do { try await Task.sleep(for: .seconds(2)) } catch { return }
            }
        }
        .onChange(of: selectedID) {
            Task { await perform(loadSelection) }
        }
        .alert("Офлайн-акаунт", isPresented: $showLogin) {
            TextField("Ім’я гравця", text: $nickname)
            Button("Зберегти") {
                mutate("offline_login", ["name": nickname], message: "Офлайн-акаунт вибрано.")
            }
            Button("Скасувати", role: .cancel) {}
        }
        .sheet(isPresented: $showMicrosoft, onDismiss: cancelMicrosoft) {
            if let loginURL {
                VStack {
                    MicrosoftWebView(url: loginURL, complete: completeMicrosoft)
                    Button("Скасувати") { showMicrosoft = false }
                        .padding(.bottom, 12)
                }
                .frame(width: 680, height: 550)
            }
        }
    }

    private var hero: some View {
        GeometryReader { geometry in
            ZStack(alignment: .bottomLeading) {
                if let artwork = cover ?? Bundle.module.url(forResource: "Cover", withExtension: "png").flatMap(NSImage.init(contentsOf:)) {
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
                    Text(profile?.name ?? "Бібліотека")
                        .font(.system(size: 42, weight: .bold))
                        .lineLimit(2)
                    Button {
                        guard let profile else { return }
                        mutate(profile.running ? "stop" : "play", ["id": profile.id])
                    } label: {
                        Label(profile?.running == true ? "Зупинити" : "Грати",
                              systemImage: profile?.running == true ? "stop.fill" : "play.fill")
                        .padding(.horizontal, 12)
                        .padding(.vertical, 5)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(ink)
                    .buttonBorderShape(.capsule)
                    .disabled(busy || profile == nil || (profile?.quarantined == true && profile?.running != true))
                }
                .padding(30)
            }
        }
        .frame(height: 350)
    }

    private func jobRow(_ job: Job) -> some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text("\(job.instance_id ?? "Модпак") · \(job.status) · \(job.phase)")
                    .frame(maxWidth: .infinity, alignment: .leading)
                if job.can_pause {
                    jobAction(job, job.paused ? "resume" : "pause", job.paused ? "Продовжити" : "Пауза")
                }
                if job.can_cancel { jobAction(job, "cancel", "Скасувати") }
                if ["failed", "interrupted", "canceled"].contains(job.status) {
                    jobAction(job, "retry", "Повторити")
                }
            }
            if let progress = job.progress { ProgressView(value: progress) }
            if let error = job.error { Text(error).font(.callout) }
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
            return
        }
        let key = "\(profile.id)/\(profile.running)/\(profile.last_played ?? "")"
        if coverKey != key {
            let path = try await core.call("cover", ["id": profile.id], as: String?.self)
            guard selectedID == profile.id else { return }
            cover = path.flatMap { NSImage(contentsOfFile: $0) }
            coverKey = key
        }
        let next = try await core.call("log", ["id": profile.id], as: String.self)
        if selectedID == profile.id { log = next }
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
                if method == "play" { status = "Готуємо Minecraft…" }
                _ = try await core.call(method, values, as: EmptyReply.self)
                status = message
                try await refresh()
            }
        }
    }

    private func importPack() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [UTType(filenameExtension: "mrpack") ?? .data]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        mutate("import", ["path": url.path], message: "Модпак додано до черги.")
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
                status = "Вхід у Microsoft виконано."
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
