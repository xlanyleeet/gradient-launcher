import Foundation

actor CoreClient {
    private var process: Process?
    private var input: FileHandle?
    private var output: FileHandle?
    private var buffer = Data()
    private(set) var isSuspendedForUpdate = false

    func call<T: Decodable>(_ method: String, _ values: [String: String] = [:], as _: T.Type) throws -> T {
        let request = values.merging(["method": method]) { _, method in method }
        return try send(request, as: T.self)
    }

    func send<T: Decodable, Request: Encodable>(_ request: Request, as _: T.Type) throws -> T {
        guard !isSuspendedForUpdate else { throw CoreError(tr("Дочекайтеся завершення оновлення Gradient.")) }
        if process?.isRunning != true {
            try connect()
        }
        var line = try JSONEncoder().encode(request)
        line.append(10)
        guard let input else { throw CoreError(tr("Канал ядра закрито.")) }
        try input.write(contentsOf: line)
        return try JSONDecoder().decode(Reply<T>.self, from: readLine()).value()
    }

    func suspendForUpdate() throws {
        _ = try call("prepare_launcher_update", as: EmptyReply.self)
        guard let child = process else { return }
        try input?.close()
        child.waitUntilExit()
        try? output?.close()
        process = nil
        input = nil
        output = nil
        buffer.removeAll()
        isSuspendedForUpdate = true
    }

    func resumeAfterUpdate() {
        isSuspendedForUpdate = false
    }

    private func connect() throws {
        let environment = ProcessInfo.processInfo.environment
        let embedded = Bundle.main.executableURL?.deletingLastPathComponent()
            .appendingPathComponent("gradient-core")
#if DEBUG
        let executable = environment["GRADIENT_CORE"].map { URL(fileURLWithPath: $0) } ?? embedded
#else
        let executable = embedded
#endif
        guard let executable else { throw CoreError(tr("Вкажіть шлях до ядра в GRADIENT_CORE.")) }

        var childEnvironment = environment
        if childEnvironment["THESEUS_DB_BACKUP_DIR"] == nil,
           let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first {
            childEnvironment["THESEUS_DB_BACKUP_DIR"] = support.appendingPathComponent("Gradient/Backups/app-db").path
        }
        let child = Process()
        child.environment = childEnvironment
        let toCore = Pipe()
        let fromCore = Pipe()
        child.executableURL = executable
        child.standardInput = toCore
        child.standardOutput = fromCore
        try child.run()
        process = child
        input = toCore.fileHandleForWriting
        output = fromCore.fileHandleForReading
        buffer.removeAll()
        do {
            let hello = try JSONDecoder().decode(Hello.self, from: readLine())
            guard hello.version == 1 else { throw CoreError(tr("Несумісна версія ядра.")) }
        } catch {
            try? input?.close()
            process = nil
            throw error
        }
    }

    private func readLine() throws -> Data {
        guard let output else { throw CoreError(tr("Канал ядра закрито.")) }
        while true {
            if let newline = buffer.firstIndex(of: 10) {
                let line = Data(buffer[..<newline])
                buffer.removeSubrange(...newline)
                return line
            }
            let data = output.availableData
            guard !data.isEmpty else { throw CoreError(tr("Ядро завершило роботу. Перевірте каталог даних.")) }
            buffer.append(data)
        }
    }
}

private struct Hello: Decodable {
    let version: Int
    enum CodingKeys: String, CodingKey { case version = "protocol" }
}

private struct Reply<T: Decodable>: Decodable {
    var result: T?
    var error: String?

    enum CodingKeys: String, CodingKey { case result = "ok", error }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        error = try container.decodeIfPresent(String.self, forKey: .error)
        if container.contains(.result) {
            result = try container.decode(T.self, forKey: .result)
        }
    }

    func value() throws -> T {
        if let error { throw CoreError(error) }
        guard let result else { throw CoreError(tr("Порожня відповідь ядра.")) }
        return result
    }
}

struct CoreError: LocalizedError {
    let errorDescription: String?
    init(_ message: String) { errorDescription = message }
}

struct EmptyReply: Decodable {
    init(from decoder: Decoder) throws {
        _ = try decoder.singleValueContainer()
    }
}
