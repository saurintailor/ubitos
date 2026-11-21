import Foundation
import XCTest
@testable import UbitProtocol

/// Runs the shared fixtures in protocol/fixtures through the Swift implementation.
final class ProtocolTests: XCTestCase {
    private static let fixtures = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()  // UbitProtocolTests
        .deletingLastPathComponent()  // Tests
        .deletingLastPathComponent()  // macos
        .deletingLastPathComponent()  // hosts
        .deletingLastPathComponent()  // repo root
        .appendingPathComponent("protocol/fixtures")

    private func files(_ kind: String) throws -> [URL] {
        let dir = Self.fixtures.appendingPathComponent(kind)
        let urls = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        XCTAssertFalse(urls.isEmpty, "no fixtures in \(dir.path)")
        return urls
    }

    func testValidFixturesDecodeAndRoundTrip() throws {
        for url in try files("valid") {
            let envelope = try Frame.decode(Data(contentsOf: url))
            let again = try Frame.decode(JSONEncoder().encode(envelope))
            XCTAssertEqual(envelope, again, url.lastPathComponent)
        }
    }

    func testInvalidFixturesAreRejected() throws {
        for url in try files("invalid") {
            XCTAssertThrowsError(try Frame.decode(Data(contentsOf: url)), url.lastPathComponent)
        }
    }

    func testInventoryFixtureFields() throws {
        let url = Self.fixtures.appendingPathComponent("valid/host_inventory-macos.json")
        guard case let .hostInventory(inv) = try Frame.decode(Data(contentsOf: url)).message else {
            return XCTFail("not an inventory")
        }
        XCTAssertEqual(inv.apps.first?.id, "com.google.Chrome")
        XCTAssertEqual(inv.defaultBrowser, "com.google.chrome")
        XCTAssertNil(inv.defaultMail)
    }

    func testFramesSplitAcrossReads() throws {
        let a = try Frame.encode(Envelope(.ping(nonce: 1)))
        let b = try Frame.encode(Envelope(.pong(nonce: 2)))
        var decoder = FrameDecoder()
        let all = a + b
        decoder.append(all.prefix(3))
        XCTAssertNil(try decoder.next())
        decoder.append(all.dropFirst(3))
        XCTAssertEqual(try Frame.decode(XCTUnwrap(decoder.next())).message, .ping(nonce: 1))
        XCTAssertEqual(try Frame.decode(XCTUnwrap(decoder.next())).message, .pong(nonce: 2))
        XCTAssertNil(try decoder.next())
    }

    func testOversizedFrameIsRejected() {
        var decoder = FrameDecoder()
        decoder.append(Data([0x00, 0x20, 0x00, 0x00]))  // 2 MiB
        XCTAssertThrowsError(try decoder.next())
    }

    func testSessionHandshakeAndScoreOverAScriptedChannel() throws {
        let channel = ScriptedChannel(replies: [
            .helloAck(agentVersion: "0.1.0", distroVersion: "Ubuntu 26.04 LTS"),
            .scoreResult(ScoreSummary(score: 80, items: [], unknownApps: [], catalogVersion: "apps@t")),
        ])
        let session = AgentSession(channel: channel)
        let (agent, distro) = try session.handshake(shellVersion: "test")
        XCTAssertEqual(agent, "0.1.0")
        XCTAssertEqual(distro, "Ubuntu 26.04 LTS")
        let summary = try session.submit(Inventory(apps: [App(id: "a", name: "A", source: "test")]))
        XCTAssertEqual(summary.score, 80)

        var sent = FrameDecoder()
        sent.append(channel.written)
        guard case .hello(.macos, "test") = try Frame.decode(XCTUnwrap(sent.next())).message else {
            return XCTFail("first message must be hello")
        }
        guard case .hostInventory = try Frame.decode(XCTUnwrap(sent.next())).message else {
            return XCTFail("second message must be the inventory")
        }
    }

    /// Regression: reads must return what is available, not wait for a full buffer.
    func testFileDescriptorChannelReturnsPartialReadsOnASocket() throws {
        var fds: [Int32] = [0, 0]
        #if canImport(Darwin)
        let streamType = SOCK_STREAM
        #else
        let streamType = Int32(SOCK_STREAM.rawValue)
        #endif
        XCTAssertEqual(socketpair(AF_UNIX, streamType, 0, &fds), 0)
        defer { close(fds[0]); close(fds[1]) }

        let host = FileDescriptorChannel(fileDescriptor: fds[0])
        let guest = FileDescriptorChannel(fileDescriptor: fds[1])
        try guest.write(Frame.encode(Envelope(.pong(nonce: 11))))
        let session = AgentSession(channel: host)
        XCTAssertEqual(try session.receive(), .pong(nonce: 11))  // would hang with FileHandle.read(upToCount:)
    }

    func testAgentErrorSurfacesAsAnError() {
        let session = AgentSession(channel: ScriptedChannel(replies: [.error(code: "bad_message", message: "nope")]))
        XCTAssertThrowsError(try session.handshake(shellVersion: "test"))
    }
}

/// Replies with one scripted message per message written.
private final class ScriptedChannel: ByteChannel {
    private var replies: [Message]
    private var pending = Data()
    private(set) var written = Data()

    init(replies: [Message]) {
        self.replies = replies
    }

    func write(_ data: Data) throws {
        written.append(data)
        if !replies.isEmpty {
            pending.append(try Frame.encode(Envelope(replies.removeFirst())))
        }
    }

    func read(maxLength: Int) throws -> Data {
        let chunk = pending.prefix(min(maxLength, 7))  // small chunks exercise the decoder
        pending = Data(pending.dropFirst(chunk.count))
        return Data(chunk)
    }
}
