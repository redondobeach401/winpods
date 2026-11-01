import AppKit
import Foundation

// Usage:
//   WinPodsAgent            menu-bar agent (default)
//   WinPodsAgent list       list paired Bluetooth devices and their addresses
//   WinPodsAgent init       write a config template
//   WinPodsAgent selftest   check the signing implementation against the shared test vector
//   WinPodsAgent pull|push|ping   one-shot handoff from the terminal

let arguments = CommandLine.arguments.dropFirst()

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(1)
}

func loadAgentOrExit() -> Agent {
    let config = AgentConfig.load()
    guard let agent = Agent(config: config) else {
        fail("Configuración incompleta en \(AgentConfig.fileURL.path):\n  - " + config.problems.joined(separator: "\n  - "))
    }
    return agent
}

switch arguments.first {
case "list":
    for d in AirPodsController.pairedDevices() {
        print("\(d.connected ? "●" : "○") \(d.name)\t\(d.address.uppercased().replacingOccurrences(of: "-", with: ":"))")
    }
    exit(0)

case "init":
    var config = AgentConfig.load()
    if config.airpodsAddress.isEmpty,
       let airpods = AirPodsController.pairedDevices().first(where: { $0.name.localizedCaseInsensitiveContains("AirPods") }) {
        config.airpodsAddress = airpods.address.uppercased().replacingOccurrences(of: "-", with: ":")
    }
    do { try config.save() } catch { fail("No se pudo guardar: \(error)") }
    print("Configuración en \(AgentConfig.fileURL.path)")
    print(config.problems.isEmpty ? "Lista para usar." : "Pendiente:\n  - " + config.problems.joined(separator: "\n  - "))
    exit(0)

case "selftest":
    let signer = HandoffSigner(secret: "correct horse battery staple")
    let message = HandoffMessage(type: "release", from: "PC", ts: 1_700_000_000, nonce: "00112233445566778899aabb", addr: "A4:C6:F0:12:34:56")
    let expected = "f7a722d01d20327edaed7381a343a57b31ffecc1cec7e31147e5f106668e1575"
    let actual = signer.signature(for: message)
    print(actual == expected ? "OK \(actual)" : "FAIL expected \(expected) got \(actual)")
    exit(actual == expected ? 0 : 1)

case "pull":
    let r = loadAgentOrExit().pullHere(); print(r.message); exit(r.ok ? 0 : 1)
case "push":
    let r = loadAgentOrExit().pushToPeer(); print(r.message); exit(r.ok ? 0 : 1)
case "ping":
    print(loadAgentOrExit().ping()); exit(0)

case nil, "run":
    let app = NSApplication.shared
    let delegate = MenuBarController()
    app.delegate = delegate
    app.setActivationPolicy(.accessory)
    app.run()

default:
    fail("Comando desconocido. Usa: list | init | selftest | pull | push | ping")
}
