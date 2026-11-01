import CryptoKit
import Foundation

/// Mirror of src/WinPods.Core/Handoff/HandoffMessage.cs — keep both in sync (see docs/handoff-protocol.md).
struct HandoffMessage: Codable, Equatable {
    static let protocolVersion = 1

    var v: Int = HandoffMessage.protocolVersion
    var type: String
    var from: String
    var ts: Int64
    var nonce: String
    var addr: String?
    var replyTo: String?
    var ok: Bool?
    var detail: String?
    var sig: String?

    enum CodingKeys: String, CodingKey {
        case v, type, from, ts, nonce, addr, ok, detail, sig
        case replyTo = "reply_to"
    }

    enum Kind {
        static let ping = "ping"
        static let release = "release"
        static let acquire = "acquire"
        static let reply = "reply"
    }

    static func create(type: String, from: String, addr: String?) -> HandoffMessage {
        HandoffMessage(type: type, from: from, ts: Int64(Date().timeIntervalSince1970), nonce: randomNonce(), addr: addr)
    }

    func reply(from: String, ok: Bool, detail: String?) -> HandoffMessage {
        HandoffMessage(type: Kind.reply, from: from, ts: Int64(Date().timeIntervalSince1970), nonce: HandoffMessage.randomNonce(),
                       addr: addr, replyTo: nonce, ok: ok, detail: detail)
    }

    /// The exact string that is HMAC'd.
    var canonicalString: String {
        let okText: String
        switch ok {
        case .some(true): okText = "1"
        case .some(false): okText = "0"
        case .none: okText = ""
        }
        return ["v\(v)", type, from, String(ts), nonce, addr ?? "", replyTo ?? "", okText].joined(separator: "\n")
    }

    func jsonLine() throws -> String {
        let data = try JSONEncoder().encode(self)
        return String(decoding: data, as: UTF8.self)
    }

    static func from(jsonLine: String) -> HandoffMessage? {
        try? JSONDecoder().decode(HandoffMessage.self, from: Data(jsonLine.utf8))
    }

    static func randomNonce() -> String {
        var bytes = [UInt8](repeating: 0, count: 12)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return bytes.map { String(format: "%02x", $0) }.joined()
    }
}

enum HandoffError: LocalizedError {
    case rejected(String)
    case network(String)

    var errorDescription: String? {
        switch self {
        case .rejected(let why): return "Mensaje rechazado: \(why)"
        case .network(let why): return why
        }
    }
}

/// HMAC-SHA256 signing with key = SHA-256("winpods-v1:" + secret), plus replay / clock-skew checks.
final class HandoffSigner {
    private let key: SymmetricKey
    private var seenNonces: [String: Int64] = [:]
    private let lock = NSLock()
    let maxClockSkew: Int64 = 60

    init(secret: String) {
        let material = "winpods-v1:" + secret.trimmingCharacters(in: .whitespacesAndNewlines)
        key = SymmetricKey(data: Data(SHA256.hash(data: Data(material.utf8))))
    }

    func signature(for message: HandoffMessage) -> String {
        let mac = HMAC<SHA256>.authenticationCode(for: Data(message.canonicalString.utf8), using: key)
        return Data(mac).map { String(format: "%02x", $0) }.joined()
    }

    func sign(_ message: HandoffMessage) -> HandoffMessage {
        var signed = message
        signed.sig = signature(for: message)
        return signed
    }

    func verify(_ message: HandoffMessage) throws {
        guard message.v == HandoffMessage.protocolVersion else { throw HandoffError.rejected("unsupported protocol version") }
        guard let sig = message.sig, let sigData = Data(hex: sig) else { throw HandoffError.rejected("missing signature") }
        guard HMAC<SHA256>.isValidAuthenticationCode(sigData, authenticating: Data(message.canonicalString.utf8), using: key) else {
            throw HandoffError.rejected("bad signature (different pairing secret?)")
        }
        let now = Int64(Date().timeIntervalSince1970)
        guard abs(now - message.ts) <= maxClockSkew else { throw HandoffError.rejected("stale message (check both clocks)") }

        lock.lock()
        defer { lock.unlock() }
        seenNonces = seenNonces.filter { $0.value >= now - maxClockSkew * 2 }
        guard seenNonces[message.nonce] == nil else { throw HandoffError.rejected("replayed message") }
        seenNonces[message.nonce] = message.ts
    }
}

extension Data {
    init?(hex: String) {
        guard hex.count % 2 == 0 else { return nil }
        var data = Data(capacity: hex.count / 2)
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
            data.append(byte)
            index = next
        }
        self = data
    }
}
