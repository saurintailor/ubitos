import Foundation

/// ubit host <-> agent protocol, version 1. Source of truth: protocol/v1.schema.json.
public enum UbitProtocol {
    public static let version = 1
    public static let agentVsockPort: UInt32 = 7710
    public static let maxFrameLength = 1 << 20
    public static let maxApps = 5000
    public static let maxUnknownApps = 200
}

public enum HostOS: String, Codable, Sendable {
    case macos, windows, linux
}

/// An installed app: identifiers only, never file contents.
public struct App: Codable, Equatable, Hashable, Sendable {
    /// macOS: bundle identifier.
    public var id: String
    public var name: String
    /// Where it was found, e.g. "applications".
    public var source: String

    public init(id: String, name: String, source: String) {
        self.id = id
        self.name = name
        self.source = source
    }
}

public struct Inventory: Equatable, Sendable {
    public var apps: [App]
    public var defaultBrowser: String?
    public var defaultMail: String?

    public init(apps: [App], defaultBrowser: String? = nil, defaultMail: String? = nil) {
        self.apps = apps
        self.defaultBrowser = defaultBrowser
        self.defaultMail = defaultMail
    }
}

public enum ItemStatus: String, Codable, Sendable {
    case native, alternative, workaround, blocker
}

public struct ScoreItem: Codable, Equatable, Sendable, Identifiable {
    public var catalogId: String
    public var category: String
    public var from: String
    public var to: String
    public var status: ItemStatus

    public var id: String { catalogId }

    enum CodingKeys: String, CodingKey {
        case catalogId = "catalog_id", category, from, to, status
    }

    public init(catalogId: String, category: String, from: String, to: String, status: ItemStatus) {
        self.catalogId = catalogId
        self.category = category
        self.from = from
        self.to = to
        self.status = status
    }
}

public struct ScoreSummary: Equatable, Sendable {
    /// 0-100, or nil when nothing matched the catalog.
    public var score: Int?
    public var items: [ScoreItem]
    public var unknownApps: [String]
    public var catalogVersion: String

    public init(score: Int?, items: [ScoreItem], unknownApps: [String], catalogVersion: String) {
        self.score = score
        self.items = items
        self.unknownApps = unknownApps
        self.catalogVersion = catalogVersion
    }
}

public enum Message: Equatable, Sendable {
    case hello(hostOS: HostOS, shellVersion: String)
    case helloAck(agentVersion: String, distroVersion: String)
    case hostInventory(Inventory)
    case scoreResult(ScoreSummary)
    case ping(nonce: UInt64)
    case pong(nonce: UInt64)
    case error(code: String, message: String)
}

public enum ProtocolError: Error, Equatable, CustomStringConvertible {
    case unsupportedVersion(Int)
    case unknownType(String)
    case invalid(String)
    case frameTooLarge(Int)
    case closed

    public var description: String {
        switch self {
        case .unsupportedVersion(let v): return "protocol version \(v) is not supported"
        case .unknownType(let t): return "unknown message type '\(t)'"
        case .invalid(let why): return "invalid message: \(why)"
        case .frameTooLarge(let n): return "frame of \(n) bytes is over the 1 MiB limit"
        case .closed: return "the agent closed the connection"
        }
    }
}

/// One wire message: version + body. JSON keys are flat, as in the schema.
public struct Envelope: Equatable, Sendable {
    public var v: Int
    public var message: Message

    public init(_ message: Message, v: Int = UbitProtocol.version) {
        self.v = v
        self.message = message
    }

    /// Rules JSON decoding alone cannot express (same as the Rust and C# sides).
    public func validate() throws {
        guard v == UbitProtocol.version else { throw ProtocolError.unsupportedVersion(v) }
        switch message {
        case .hostInventory(let inv) where inv.apps.count > UbitProtocol.maxApps:
            throw ProtocolError.invalid("more than \(UbitProtocol.maxApps) apps")
        case .scoreResult(let s) where s.score.map { !(0...100).contains($0) } ?? false:
            throw ProtocolError.invalid("score must be between 0 and 100")
        case .scoreResult(let s) where s.unknownApps.count > UbitProtocol.maxUnknownApps:
            throw ProtocolError.invalid("too many unknown apps")
        default:
            return
        }
    }
}

extension Envelope: Codable {
    enum Key: String, CodingKey {
        case v, type
        case hostOS = "host_os", shellVersion = "shell_version"
        case agentVersion = "agent_version", distroVersion = "distro_version"
        case apps, defaultBrowser = "default_browser", defaultMail = "default_mail"
        case score, items, unknownApps = "unknown_apps", catalogVersion = "catalog_version"
        case nonce, code, message
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: Key.self)
        v = try c.decode(Int.self, forKey: .v)
        let type = try c.decode(String.self, forKey: .type)
        switch type {
        case "hello":
            message = .hello(hostOS: try c.decode(HostOS.self, forKey: .hostOS),
                             shellVersion: try c.decode(String.self, forKey: .shellVersion))
        case "hello_ack":
            message = .helloAck(agentVersion: try c.decode(String.self, forKey: .agentVersion),
                                distroVersion: try c.decode(String.self, forKey: .distroVersion))
        case "host_inventory":
            message = .hostInventory(Inventory(
                apps: try c.decode([App].self, forKey: .apps),
                defaultBrowser: try c.decodeIfPresent(String.self, forKey: .defaultBrowser),
                defaultMail: try c.decodeIfPresent(String.self, forKey: .defaultMail)))
        case "score_result":
            message = .scoreResult(ScoreSummary(
                score: try c.decodeIfPresent(Int.self, forKey: .score),
                items: try c.decode([ScoreItem].self, forKey: .items),
                unknownApps: try c.decode([String].self, forKey: .unknownApps),
                catalogVersion: try c.decode(String.self, forKey: .catalogVersion)))
        case "ping":
            message = .ping(nonce: try c.decode(UInt64.self, forKey: .nonce))
        case "pong":
            message = .pong(nonce: try c.decode(UInt64.self, forKey: .nonce))
        case "error":
            message = .error(code: try c.decode(String.self, forKey: .code),
                             message: try c.decode(String.self, forKey: .message))
        default:
            throw ProtocolError.unknownType(type)
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: Key.self)
        try c.encode(v, forKey: .v)
        switch message {
        case let .hello(os, shell):
            try c.encode("hello", forKey: .type)
            try c.encode(os, forKey: .hostOS)
            try c.encode(shell, forKey: .shellVersion)
        case let .helloAck(agent, distro):
            try c.encode("hello_ack", forKey: .type)
            try c.encode(agent, forKey: .agentVersion)
            try c.encode(distro, forKey: .distroVersion)
        case let .hostInventory(inv):
            try c.encode("host_inventory", forKey: .type)
            try c.encode(inv.apps, forKey: .apps)
            try c.encode(inv.defaultBrowser, forKey: .defaultBrowser)
            try c.encode(inv.defaultMail, forKey: .defaultMail)
        case let .scoreResult(s):
            try c.encode("score_result", forKey: .type)
            try c.encode(s.score, forKey: .score)
            try c.encode(s.items, forKey: .items)
            try c.encode(s.unknownApps, forKey: .unknownApps)
            try c.encode(s.catalogVersion, forKey: .catalogVersion)
        case let .ping(nonce):
            try c.encode("ping", forKey: .type)
            try c.encode(nonce, forKey: .nonce)
        case let .pong(nonce):
            try c.encode("pong", forKey: .type)
            try c.encode(nonce, forKey: .nonce)
        case let .error(code, message):
            try c.encode("error", forKey: .type)
            try c.encode(code, forKey: .code)
            try c.encode(message, forKey: .message)
        }
    }
}
