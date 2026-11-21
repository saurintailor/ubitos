// swift-tools-version:5.10
// ubit for macOS: runs the ubit guest in Virtualization.framework (Apple Silicon, macOS 14+).
//
// UbitProtocol  - messages + framing, Foundation only (also builds and tests on Linux)
// UbitCore      - VM configuration, disk bundle, host inventory, agent link (Apple-only)
// UbitMac       - the SwiftUI app
import PackageDescription

let package = Package(
    name: "UbitMac",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "UbitMac", targets: ["UbitMac"]),
    ],
    targets: [
        .target(name: "UbitProtocol"),
        .target(name: "UbitCore", dependencies: ["UbitProtocol"]),
        .executableTarget(name: "UbitMac", dependencies: ["UbitCore", "UbitProtocol"]),
        .testTarget(name: "UbitProtocolTests", dependencies: ["UbitProtocol"]),
        .testTarget(name: "UbitCoreTests", dependencies: ["UbitCore"]),
    ]
)
