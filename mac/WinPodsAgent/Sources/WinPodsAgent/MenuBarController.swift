import AppKit

/// Menu-bar UI: status line, "bring audio here", "send audio to Windows".
final class MenuBarController: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let statusLine = NSMenuItem(title: "Iniciando…", action: nil, keyEquivalent: "")
    private var agent: Agent?
    private var busy = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = NSImage(systemSymbolName: "airpodspro", accessibilityDescription: "WinPods")
        if statusItem.button?.image == nil { statusItem.button?.title = "WP" }

        let menu = NSMenu()
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(item("Traer audio a este Mac", #selector(pullHere), "m"))
        menu.addItem(item("Enviar audio a Windows", #selector(pushToPeer), "w"))
        menu.addItem(item("Probar conexión con el PC", #selector(ping), ""))
        menu.addItem(.separator())
        menu.addItem(item("Abrir configuración…", #selector(openConfig), ","))
        menu.addItem(item("Recargar configuración", #selector(reload), "r"))
        menu.addItem(.separator())
        menu.addItem(item("Salir", #selector(quit), "q"))
        statusItem.menu = menu

        reload()
    }

    private func item(_ title: String, _ action: Selector, _ key: String) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = self
        return item
    }

    private func setStatus(_ text: String) {
        DispatchQueue.main.async { self.statusLine.title = text }
    }

    @objc private func reload() {
        agent?.stop()
        agent = nil
        let config = AgentConfig.load()
        if !FileManager.default.fileExists(atPath: AgentConfig.fileURL.path) { try? config.save() }
        guard let agent = Agent(config: config) else {
            setStatus("Configura: " + config.problems.joined(separator: "; "))
            return
        }
        agent.log = { [weak self] in self?.setStatus($0) }
        do {
            try agent.startServer()
            self.agent = agent
            setStatus("Listo · \(agent.airpods.name) · PC: \(config.peerHost)")
        } catch {
            setStatus("No se pudo escuchar en el puerto \(config.listenPort): \(error.localizedDescription)")
        }
    }

    private func runInBackground(_ label: String, _ work: @escaping (Agent) -> String) {
        guard let agent = agent else { setStatus("Configuración incompleta"); return }
        guard !busy else { return }
        busy = true
        setStatus(label)
        DispatchQueue.global(qos: .userInitiated).async {
            let message = work(agent)
            DispatchQueue.main.async {
                self.busy = false
                self.statusLine.title = message
            }
        }
    }

    @objc private func pullHere() { runInBackground("Cambiando…") { $0.pullHere().message } }
    @objc private func pushToPeer() { runInBackground("Cambiando…") { $0.pushToPeer().message } }
    @objc private func ping() { runInBackground("Probando…") { $0.ping() } }

    @objc private func openConfig() {
        if !FileManager.default.fileExists(atPath: AgentConfig.fileURL.path) { try? AgentConfig.load().save() }
        NSWorkspace.shared.open(AgentConfig.fileURL)
    }

    @objc private func quit() {
        agent?.stop()
        NSApp.terminate(nil)
    }
}
