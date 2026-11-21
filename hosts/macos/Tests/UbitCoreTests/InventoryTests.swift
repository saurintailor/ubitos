import Foundation
import XCTest
@testable import UbitCore
import UbitProtocol

final class InventoryTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("ubit-inventory-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private func makeApp(_ relativePath: String, plist: [String: Any]?) throws {
        let contents = root.appendingPathComponent(relativePath).appendingPathComponent("Contents")
        try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)
        if let plist {
            let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
            try data.write(to: contents.appendingPathComponent("Info.plist"))
        }
    }

    func testFindsAppsOneLevelDeepAndPrefersDisplayName() throws {
        try makeApp("Google Chrome.app", plist: ["CFBundleIdentifier": "com.google.Chrome", "CFBundleName": "Chrome"])
        try makeApp("Utilities/Terminal.app", plist: ["CFBundleIdentifier": "com.apple.Terminal", "CFBundleDisplayName": "Terminal"])
        try makeApp("Adobe/Photoshop/Deep.app", plist: ["CFBundleIdentifier": "too.deep"])
        try makeApp("Broken.app", plist: nil)

        let apps = InventoryScanner.apps(in: root, source: "applications")
        XCTAssertEqual(Set(apps.map(\.id)), ["com.google.Chrome", "com.apple.Terminal"])
        XCTAssertEqual(apps.first { $0.id == "com.google.Chrome" }?.name, "Chrome")
        XCTAssertTrue(apps.allSatisfy { $0.source == "applications" })
    }

    func testMergeDeduplicatesByBundleIdAndSortsByName() {
        let merged = InventoryScanner.merge([
            [App(id: "com.b", name: "Bravo", source: "applications")],
            [App(id: "COM.B", name: "Bravo copy", source: "user-applications"),
             App(id: "com.a", name: "alpha", source: "user-applications")],
        ])
        XCTAssertEqual(merged.map(\.name), ["alpha", "Bravo"])
    }

    func testImportGrowsTheDiskFileSparsely() throws {
        let source = root.appendingPathComponent("image.img")
        try Data(repeating: 7, count: 4096).write(to: source)
        let bundle = VMBundle(root: root.appendingPathComponent("vm"))
        try bundle.importImage(from: source, size: 1 << 30)
        let size = try FileManager.default.attributesOfItem(atPath: bundle.disk.path)[.size] as? NSNumber
        XCTAssertEqual(size?.uint64Value, 1 << 30)
        XCTAssertTrue(bundle.hasDisk)
        try bundle.remove()
        XCTAssertFalse(bundle.hasDisk)
    }

    func testFailedImportKeepsTheExistingDiskAndReimportOfItselfWorks() throws {
        let bundle = VMBundle(root: root.appendingPathComponent("vm"))
        let first = root.appendingPathComponent("first.img")
        try Data(repeating: 1, count: 4096).write(to: first)
        try bundle.importImage(from: first, size: 8192)

        XCTAssertThrowsError(try bundle.importImage(from: root.appendingPathComponent("missing.img"), size: 8192))
        XCTAssertTrue(bundle.hasDisk, "a failed import must not delete the current disk")
        XCTAssertEqual(try Data(contentsOf: bundle.disk).first, 1)

        try bundle.importImage(from: bundle.disk, size: 8192)  // choosing the bundle's own disk
        XCTAssertEqual(try Data(contentsOf: bundle.disk).first, 1)
    }
}
