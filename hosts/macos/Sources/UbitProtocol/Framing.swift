import Foundation

/// Frames: 4-byte big-endian length, then the JSON body (at most 1 MiB).
public enum Frame {
    public static func encode(_ envelope: Envelope) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let body = try encoder.encode(envelope)
        guard body.count <= UbitProtocol.maxFrameLength else {
            throw ProtocolError.frameTooLarge(body.count)
        }
        var length = UInt32(body.count).bigEndian
        var frame = Data(bytes: &length, count: 4)
        frame.append(body)
        return frame
    }

    /// Parses and validates one frame body.
    public static func decode(_ body: Data) throws -> Envelope {
        let envelope = try JSONDecoder().decode(Envelope.self, from: body)
        try envelope.validate()
        return envelope
    }
}

/// Splits a byte stream into frame bodies.
public struct FrameDecoder {
    private var buffer = Data()

    public init() {}

    public mutating func append(_ data: Data) {
        buffer.append(data)
    }

    /// The next complete frame body, or nil if more bytes are needed.
    public mutating func next() throws -> Data? {
        guard buffer.count >= 4 else { return nil }
        let length = buffer.prefix(4).reduce(0) { ($0 << 8) | Int($1) }
        guard length <= UbitProtocol.maxFrameLength else { throw ProtocolError.frameTooLarge(length) }
        guard buffer.count >= 4 + length else { return nil }
        let body = Data(buffer.dropFirst(4).prefix(length))
        buffer = Data(buffer.dropFirst(4 + length))
        return body
    }
}

/// A blocking, bidirectional byte stream (a vsock connection in the app, a pipe in tests).
public protocol ByteChannel: AnyObject {
    func write(_ data: Data) throws
    /// Returns an empty Data at end of stream.
    func read(maxLength: Int) throws -> Data
}

#if canImport(Darwin)
import Darwin
private let posixRead = Darwin.read
private let posixWrite = Darwin.write
#elseif canImport(Glibc)
import Glibc
private let posixRead = Glibc.read
private let posixWrite = Glibc.write
#endif

/// `ByteChannel` over a file descriptor the caller owns (e.g. a vsock connection).
///
/// Uses read(2)/write(2) directly: `FileHandle.read(upToCount:)` waits until it has the
/// full count (or EOF), which never comes on a socket that stays open.
public final class FileDescriptorChannel: ByteChannel {
    private let fd: Int32

    public init(fileDescriptor: Int32) {
        fd = fileDescriptor
    }

    public func write(_ data: Data) throws {
        try data.withUnsafeBytes { (buffer: UnsafeRawBufferPointer) in
            guard let base = buffer.baseAddress else { return }
            var offset = 0
            while offset < buffer.count {
                let n = posixWrite(fd, base + offset, buffer.count - offset)
                if n < 0 {
                    if errno == EINTR { continue }
                    throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
                }
                offset += n
            }
        }
    }

    /// Returns whatever is available (at least one byte), or empty Data at end of stream.
    public func read(maxLength: Int) throws -> Data {
        var buffer = [UInt8](repeating: 0, count: maxLength)
        while true {
            let n = buffer.withUnsafeMutableBytes { posixRead(fd, $0.baseAddress, maxLength) }
            if n < 0 {
                if errno == EINTR { continue }
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
            return Data(buffer.prefix(n))
        }
    }
}

/// The host side of one agent conversation. Calls block; run them off the main thread.
public final class AgentSession: @unchecked Sendable {
    private let channel: ByteChannel
    private var decoder = FrameDecoder()
    private let lock = NSLock()

    public init(channel: ByteChannel) {
        self.channel = channel
    }

    public func send(_ message: Message) throws {
        try channel.write(try Frame.encode(Envelope(message)))
    }

    public func receive() throws -> Message {
        while true {
            if let body = try decoder.next() {
                return try Frame.decode(body).message
            }
            let chunk = try channel.read(maxLength: 64 * 1024)
            if chunk.isEmpty { throw ProtocolError.closed }
            decoder.append(chunk)
        }
    }

    /// Sends hello and waits for hello_ack. Returns (agent version, distro).
    public func handshake(shellVersion: String) throws -> (agent: String, distro: String) {
        lock.lock(); defer { lock.unlock() }
        try send(.hello(hostOS: .macos, shellVersion: shellVersion))
        switch try receive() {
        case let .helloAck(agent, distro): return (agent, distro)
        case let .error(code, message): throw ProtocolError.invalid("agent error \(code): \(message)")
        case let other: throw ProtocolError.invalid("expected hello_ack, got \(other)")
        }
    }

    /// Sends the inventory and waits for the score.
    public func submit(_ inventory: Inventory) throws -> ScoreSummary {
        lock.lock(); defer { lock.unlock() }
        try send(.hostInventory(inventory))
        switch try receive() {
        case let .scoreResult(summary): return summary
        case let .error(code, message): throw ProtocolError.invalid("agent error \(code): \(message)")
        case let other: throw ProtocolError.invalid("expected score_result, got \(other)")
        }
    }
}
