import Foundation

/// ~/Library/Application Support/WinPods/agent.json
struct AgentConfig: Codable {
    /// Name shown on the Windows side.
    var name: String = Host.current().localizedName ?? "Mac"
    /// Same pairing code as in WinPods on Windows.
    var secret: String = ""
    /// AirPods Bluetooth address, e.g. "A4:C6:F0:12:34:56" (run `WinPodsAgent list`).
    var airpodsAddress: String = ""
    /// Windows PC host name or IP, e.g. "MI-PC.local" or "192.168.1.30".
    var peerHost: String = ""
    var peerPort: UInt16 = 47821
    var listenPort: UInt16 = 47821

    init() {}

    /// Tolerates missing keys so a hand-edited file never silently resets to defaults.
    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = AgentConfig()
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? d.name
        secret = try c.decodeIfPresent(String.self, forKey: .secret) ?? d.secret
        airpodsAddress = try c.decodeIfPresent(String.self, forKey: .airpodsAddress) ?? d.airpodsAddress
        peerHost = try c.decodeIfPresent(String.self, forKey: .peerHost) ?? d.peerHost
        peerPort = try c.decodeIfPresent(UInt16.self, forKey: .peerPort) ?? d.peerPort
        listenPort = try c.decodeIfPresent(UInt16.self, forKey: .listenPort) ?? d.listenPort
    }

    static var fileURL: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("WinPods", isDirectory: true)
            .appendingPathComponent("agent.json")
    }

    static func load() -> AgentConfig {
        guard let data = try? Data(contentsOf: fileURL),
              let config = try? JSONDecoder().decode(AgentConfig.self, from: data) else {
            return AgentConfig()
        }
        return config
    }

    func save() throws {
        try FileManager.default.createDirectory(at: AgentConfig.fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(self).write(to: AgentConfig.fileURL, options: .atomic)
    }

    var problems: [String] {
        var p: [String] = []
        if secret.trimmingCharacters(in: .whitespaces).isEmpty { p.append("falta \"secret\" (código de enlace de WinPods en Windows)") }
        if AirPodsController(address: airpodsAddress) == nil { p.append("\"airpodsAddress\" no es válida (usa `WinPodsAgent list`)") }
        if peerHost.trimmingCharacters(in: .whitespaces).isEmpty { p.append("falta \"peerHost\" (nombre o IP del PC con Windows)") }
        return p
    }
}
