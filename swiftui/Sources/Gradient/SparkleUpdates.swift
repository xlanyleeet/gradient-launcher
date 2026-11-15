import AppKit
import Sparkle

@MainActor
final class SparkleUpdates: NSObject, SPUUpdaterDelegate {
    static let shared = SparkleUpdates()

    private lazy var controller = SPUStandardUpdaterController(
        startingUpdater: true, updaterDelegate: self, userDriverDelegate: nil
    )
    private var suspendedCore: CoreClient?

    var isConfigured: Bool {
        Bundle.main.object(forInfoDictionaryKey: "SUPublicEDKey") is String
            && Bundle.main.object(forInfoDictionaryKey: "SUFeedURL") is String
    }

    func check(using core: CoreClient) async throws {
        guard suspendedCore == nil, controller.updater.canCheckForUpdates else {
            throw CoreError("Перевірка оновлень уже відкрита.")
        }
        suspendedCore = core
        do {
            try await core.suspendForUpdate()
        } catch {
            suspendedCore = nil
            throw error
        }
        controller.checkForUpdates(nil)
    }

    func updater(
        _ updater: SPUUpdater,
        didFinishUpdateCycleFor updateCheck: SPUUpdateCheck,
        error: Error?
    ) {
        guard let core = suspendedCore else { return }
        suspendedCore = nil
        Task { await core.resumeAfterUpdate() }
    }
}
