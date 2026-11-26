import Foundation

enum CoreEvent: Sendable {
    case job(Data)
    case process(String)
    case instance(String)
    case log(String)
}

actor CoreClient {
    nonisolated let events: AsyncStream<CoreEvent>
    private let eventContinuation: AsyncStream<CoreEvent>.Continuation

    private var process: Process?
    private var input: FileHandle?
    private var readerTask: Task<Void, Never>?
    private var connectionID = 0
    private var pendingReply: CheckedContinuation<Data, Error>?
    private var inFlight = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    private(set) var isSuspendedForUpdate = false

    init() {
        let (stream, continuation) = AsyncStream<CoreEvent>.makeStream()
        self.events = stream
        self.eventContinuation = continuation
    }

    func call<T: Decodable>(_ method: String, _ values: [String: String] = [:], as _: T.Type) async throws -> T {
        let request = values.merging(["method": method]) { _, method in method }
        return try await send(request, as: T.self)
    }

    func send<T: Decodable, Request: Encodable>(_ request: Request, as _: T.Type) async throws -> T {
        while inFlight {
            await withCheckedContinuation { continuation in
                waiters.append(continuation)
            }
        }
        inFlight = true
        defer {
            inFlight = false
            if !waiters.isEmpty {
                waiters.removeFirst().resume()
            }
        }

        guard !isSuspendedForUpdate else { throw CoreError(tr("Дочекайтеся завершення оновлення Gradient.")) }
        if process?.isRunning != true {
            try connect()
        }
        var line = try JSONEncoder().encode(request)
        line.append(10)
        guard let input else { throw CoreError(tr("Канал ядра закрито.")) }

        let replyData: Data = try await withCheckedThrowingContinuation { continuation in
            pendingReply = continuation
            do {
                try input.write(contentsOf: line)
            } catch {
                pendingReply = nil
                continuation.resume(throwing: error)
            }
        }
        return try JSONDecoder().decode(Reply<T>.self, from: replyData).value()
    }

    func suspendForUpdate() async throws {
        _ = try await call("prepare_launcher_update", as: EmptyReply.self)
        guard let child = process else { return }
        readerTask?.cancel()
        readerTask = nil
        try input?.close()
        child.waitUntilExit()
        failPendingReply(id: connectionID)
        process = nil
        input = nil
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

        let output = fromCore.fileHandleForReading
        var buffer = Data()
        do {
            let helloData = try readLineSync(from: output, buffer: &buffer)
            let hello = try JSONDecoder().decode(Hello.self, from: helloData)
            guard hello.version == 1 else { throw CoreError(tr("Несумісна версія ядра.")) }
        } catch {
            try? input?.close()
            process = nil
            input = nil
            throw error
        }

        connectionID += 1
        let activeID = connectionID
        let initialBuffer = buffer
        let continuation = eventContinuation
        readerTask?.cancel()
        readerTask = Task.detached { [weak self] in
            var buf = initialBuffer
            while !Task.isCancelled {
                if let newline = buf.firstIndex(of: 10) {
                    let line = Data(buf[..<newline])
                    buf.removeSubrange(...newline)
                    if let parsed = Self.parseEvent(line) {
                        continuation.yield(parsed)
                    } else {
                        await self?.deliverReply(line, id: activeID)
                    }
                    continue
                }
                let chunk = output.availableData
                if chunk.isEmpty { break }
                buf.append(chunk)
            }
            try? output.close()
            await self?.failPendingReply(id: activeID)
        }
    }

    private func deliverReply(_ data: Data, id: Int) {
        guard id == connectionID, let continuation = pendingReply else { return }
        pendingReply = nil
        continuation.resume(returning: data)
    }

    private func failPendingReply(id: Int) {
        guard id == connectionID else { return }
        if let continuation = pendingReply {
            pendingReply = nil
            continuation.resume(throwing: CoreError(tr("Ядро завершило роботу. Перевірте каталог даних.")))
        }
        process = nil
        input = nil
    }

    private nonisolated func readLineSync(from output: FileHandle, buffer: inout Data) throws -> Data {
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

    private nonisolated static func parseEvent(_ line: Data) -> CoreEvent? {
        guard let object = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
              let eventName = object["event"] as? String else {
            return nil
        }
        switch eventName {
        case "job":
            guard let jobObject = object["job"],
                  let jobData = try? JSONSerialization.data(withJSONObject: jobObject) else {
                return nil
            }
            return .job(jobData)
        case "process":
            return .process(object["instance_id"] as? String ?? "")
        case "instance":
            return .instance(object["instance_id"] as? String ?? "")
        case "log":
            return .log(object["instance_id"] as? String ?? "")
        default:
            return nil
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
