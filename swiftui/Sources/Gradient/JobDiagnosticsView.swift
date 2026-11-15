import AppKit
import SwiftUI

struct JobDiagnosticsView: View {
    let core: CoreClient
    let jobID: String
    @Environment(\.dismiss) private var dismiss
    @State private var report: String?
    @State private var errorMessage: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Діагностика встановлення").font(.title2.bold())
            if let report {
                ScrollView {
                    Text(report)
                        .font(.system(.body, design: .monospaced))
                        .textSelection(.enabled)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            } else if let errorMessage {
                Text(errorMessage)
            } else {
                ProgressView()
            }
            HStack {
                Spacer()
                Button("Закрити") { dismiss() }.keyboardShortcut(.cancelAction)
                Button("Копіювати") {
                    guard let report else { return }
                    NSPasteboard.general.clearContents()
                    NSPasteboard.general.setString(report, forType: .string)
                }
                .disabled(report == nil)
            }
        }
        .padding(24)
        .frame(width: 600, height: 440)
        .task {
            do { report = try await core.call("job_diagnostics", ["id": jobID], as: String.self) }
            catch { errorMessage = error.localizedDescription }
        }
    }
}
