import Foundation
import IOBluetooth

/// Connects / disconnects the AirPods on this Mac through IOBluetooth.
/// macOS routes audio to the AirPods automatically once they connect.
final class AirPodsController {
    /// IOBluetooth format: "a4-c6-f0-12-34-56".
    let address: String

    init?(address raw: String) {
        let hex = raw.lowercased().filter { $0.isHexDigit }
        guard hex.count == 12 else { return nil }
        var pairs: [String] = []
        var i = hex.startIndex
        while i < hex.endIndex {
            let j = hex.index(i, offsetBy: 2)
            pairs.append(String(hex[i..<j]))
            i = j
        }
        address = pairs.joined(separator: "-")
    }

    /// "A4:C6:F0:12:34:56", the format used on the wire.
    var wireAddress: String { address.uppercased().replacingOccurrences(of: "-", with: ":") }

    private var device: IOBluetoothDevice? { IOBluetoothDevice(addressString: address) }

    var name: String { onMain { self.device?.name } ?? "AirPods" }

    var isConnected: Bool { onMain { self.device?.isConnected() } ?? false }

    @discardableResult
    func connect() -> Bool {
        onMain {
            guard let device = self.device else { return false }
            if device.isConnected() { return true }
            return device.openConnection() == kIOReturnSuccess
        } ?? false
    }

    @discardableResult
    func disconnect() -> Bool {
        onMain {
            guard let device = self.device else { return false }
            if !device.isConnected() { return true }
            return device.closeConnection() == kIOReturnSuccess
        } ?? false
    }

    static func pairedDevices() -> [(name: String, address: String, connected: Bool)] {
        let devices = (IOBluetoothDevice.pairedDevices() as? [IOBluetoothDevice]) ?? []
        return devices.map { ($0.name ?? "?", $0.addressString ?? "?", $0.isConnected()) }
    }

    /// IOBluetooth expects to be driven from the main run loop.
    private func onMain<T>(_ body: @escaping () -> T?) -> T? {
        if Thread.isMainThread { return body() }
        return DispatchQueue.main.sync(execute: body)
    }
}
