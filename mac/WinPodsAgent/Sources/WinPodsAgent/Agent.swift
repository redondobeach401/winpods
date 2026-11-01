import Foundation

/// Handoff logic for the Mac side (mirror of HandoffCoordinator.cs).
final class Agent {
    let config: AgentConfig
    let airpods: AirPodsController
    let signer: HandoffSigner
    let client: HandoffClient
    private var server: HandoffServer?
    private let gate = NSLock()
    var log: ((String) -> Void)?

    static let settleDelay: TimeInterval = 1.5

    init?(config: AgentConfig) {
        guard config.problems.isEmpty, let airpods = AirPodsController(address: config.airpodsAddress) else { return nil }
        self.config = config
        self.airpods = airpods
        signer = HandoffSigner(secret: config.secret)
        client = HandoffClient(signer: signer, localName: config.name, host: config.peerHost, port: config.peerPort)
    }

    func startServer() throws {
        let server = try HandoffServer(port: config.listenPort, signer: signer, localName: config.name) { [weak self] request in
            guard let self = self else { return (false, "agent stopped") }
            let result = self.handle(request)
            if request.type != HandoffMessage.Kind.ping {
                self.log?("\(request.from) pidió '\(request.type)': \(result.detail ?? "")")
            }
            return result
        }
        server.log = { [weak self] in self?.log?($0) }
        server.start()
        self.server = server
    }

    func stop() { server?.stop() }

    func handle(_ request: HandoffMessage) -> (ok: Bool, detail: String?) {
        switch request.type {
        case HandoffMessage.Kind.ping:
            return (true, airpods.isConnected ? "connected" : "idle")
        case HandoffMessage.Kind.release:
            if !airpods.isConnected { return (true, "not connected") }
            return airpods.disconnect() ? (true, "released") : (false, "disconnect failed")
        case HandoffMessage.Kind.acquire:
            return airpods.connect() ? (true, "connected") : (false, "connect failed")
        default:
            return (false, "unknown request '\(request.type)'")
        }
    }

    /// "Traer audio a este Mac". Blocking; call from a background queue.
    func pullHere() -> (ok: Bool, message: String) {
        gate.lock(); defer { gate.unlock() }
        let peerNote: String
        switch client.request(HandoffMessage.Kind.release, addr: airpods.wireAddress) {
        case .success(let reply):
            peerNote = reply.ok == true ? "\(reply.from) liberó los AirPods" : "\(reply.from): \(reply.detail ?? "no pudo liberar")"
            if reply.ok == true { Thread.sleep(forTimeInterval: Agent.settleDelay) }
        case .failure(let error):
            peerNote = "no se pudo contactar al PC (\(error.localizedDescription))"
        }
        return airpods.connect()
            ? (true, "Audio en este Mac; \(peerNote).")
            : (false, "No se pudo conectar los AirPods; \(peerNote).")
    }

    /// "Enviar audio a Windows". Blocking; call from a background queue.
    func pushToPeer() -> (ok: Bool, message: String) {
        gate.lock(); defer { gate.unlock() }
        airpods.disconnect()
        Thread.sleep(forTimeInterval: Agent.settleDelay)
        switch client.request(HandoffMessage.Kind.acquire, addr: airpods.wireAddress, timeout: 20) {
        case .success(let reply) where reply.ok == true:
            return (true, "Audio enviado a \(reply.from).")
        case .success(let reply):
            airpods.connect()
            return (false, "\(reply.from) no pudo conectar (\(reply.detail ?? "?")); el audio vuelve aquí.")
        case .failure(let error):
            airpods.connect()
            return (false, "No se pudo contactar al PC (\(error.localizedDescription)); el audio vuelve aquí.")
        }
    }

    func ping() -> String {
        switch client.request(HandoffMessage.Kind.ping, addr: airpods.wireAddress) {
        case .success(let reply): return "✔ \(reply.from) responde (\(reply.detail == "connected" ? "tiene los AirPods" : "sin AirPods"))."
        case .failure(let error): return "✖ \(error.localizedDescription)"
        }
    }
}
