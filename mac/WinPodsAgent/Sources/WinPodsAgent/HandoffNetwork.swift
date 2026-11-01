import Foundation
import Network

/// One newline-terminated JSON request, one JSON reply, then the connection is closed.
enum LineIO {
    static let maxLine = 8 * 1024

    static func readLine(_ connection: NWConnection, buffer: Data = Data(), completion: @escaping (String?) -> Void) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 4096) { data, _, isComplete, error in
            var buffer = buffer
            if let data = data { buffer.append(data) }
            if let newline = buffer.firstIndex(of: UInt8(ascii: "\n")) {
                let line = String(decoding: buffer[..<newline], as: UTF8.self).trimmingCharacters(in: .init(charactersIn: "\r"))
                completion(line)
            } else if error != nil || isComplete || buffer.count > maxLine {
                completion(buffer.isEmpty || buffer.count > maxLine ? nil : String(decoding: buffer, as: UTF8.self))
            } else {
                readLine(connection, buffer: buffer, completion: completion)
            }
        }
    }

    static func write(_ connection: NWConnection, line: String, completion: @escaping (Error?) -> Void) {
        connection.send(content: Data((line + "\n").utf8), completion: .contentProcessed { completion($0) })
    }
}

final class HandoffServer {
    typealias Handler = (HandoffMessage) -> (ok: Bool, detail: String?)

    private let listener: NWListener
    private let signer: HandoffSigner
    private let localName: String
    private let handler: Handler
    private let queue = DispatchQueue(label: "winpods.server")
    var log: ((String) -> Void)?

    init(port: UInt16, signer: HandoffSigner, localName: String, handler: @escaping Handler) throws {
        guard let nwPort = NWEndpoint.Port(rawValue: port) else { throw HandoffError.network("invalid port \(port)") }
        listener = try NWListener(using: .tcp, on: nwPort)
        self.signer = signer
        self.localName = localName
        self.handler = handler
    }

    func start() {
        listener.newConnectionHandler = { [weak self] connection in self?.handle(connection) }
        listener.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state { self?.log?("Servidor detenido: \(error)") }
        }
        listener.start(queue: queue)
    }

    func stop() { listener.cancel() }

    private func handle(_ connection: NWConnection) {
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 30) { connection.cancel() }
        LineIO.readLine(connection) { [weak self] line in
            guard let self = self, let line = line, let request = HandoffMessage.from(jsonLine: line) else {
                connection.cancel(); return
            }
            do {
                try self.signer.verify(request)
            } catch {
                self.log?("Petición rechazada: \(error.localizedDescription)")
                connection.cancel(); return
            }
            // The handler may block on Bluetooth; keep it off the network queue.
            DispatchQueue.global().async {
                let result = self.handler(request)
                let reply = self.signer.sign(request.reply(from: self.localName, ok: result.ok, detail: result.detail))
                guard let json = try? reply.jsonLine() else { connection.cancel(); return }
                LineIO.write(connection, line: json) { _ in connection.cancel() }
            }
        }
    }
}

final class HandoffClient {
    private let signer: HandoffSigner
    private let localName: String
    let host: String
    let port: UInt16
    private let queue = DispatchQueue(label: "winpods.client")

    init(signer: HandoffSigner, localName: String, host: String, port: UInt16) {
        self.signer = signer
        self.localName = localName
        self.host = host
        self.port = port
    }

    /// Blocking request; call from a background queue.
    func request(_ type: String, addr: String?, timeout: TimeInterval = 10) -> Result<HandoffMessage, Error> {
        guard let nwPort = NWEndpoint.Port(rawValue: port) else { return .failure(HandoffError.network("invalid port")) }
        let request = signer.sign(HandoffMessage.create(type: type, from: localName, addr: addr))
        let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
        let done = DispatchSemaphore(value: 0)
        var result: Result<HandoffMessage, Error> = .failure(HandoffError.network("Sin respuesta de \(host) (¿WinPods abierto y handoff activado?)"))
        var finished = false
        let lock = NSLock()

        func finish(_ r: Result<HandoffMessage, Error>) {
            lock.lock(); defer { lock.unlock() }
            guard !finished else { return }
            finished = true
            result = r
            connection.cancel()
            done.signal()
        }

        connection.stateUpdateHandler = { state in
            switch state {
            case .ready:
                guard let json = try? request.jsonLine() else { finish(.failure(HandoffError.network("encode failed"))); return }
                LineIO.write(connection, line: json) { error in
                    if let error = error { finish(.failure(error)); return }
                    LineIO.readLine(connection) { line in
                        guard let line = line, let reply = HandoffMessage.from(jsonLine: line) else {
                            finish(.failure(HandoffError.network("Respuesta inválida"))); return
                        }
                        do {
                            try self.signer.verify(reply)
                            guard reply.type == HandoffMessage.Kind.reply, reply.replyTo == request.nonce else {
                                throw HandoffError.rejected("reply does not match request")
                            }
                            finish(.success(reply))
                        } catch {
                            finish(.failure(error))
                        }
                    }
                }
            case .failed(let error):
                finish(.failure(error))
            default:
                break
            }
        }
        connection.start(queue: queue)
        if done.wait(timeout: .now() + timeout) == .timedOut { finish(result) }
        return result
    }
}
