import Foundation
import UbitProtocol

/// Collects the host inventory: installed app bundle IDs and default handlers.
/// Reads only Info.plist metadata; never document contents.
public enum InventoryScanner {
    /// Folders scanned for .app bundles (one level of subfolders too, e.g. Utilities).
    public static var defaultRoots: [(url: URL, source: String)] {
        var roots: [(URL, String)] = [
            (URL(fileURLWithPath: "/Applications"), "applications"),
            (URL(fileURLWithPath: "/System/Applications"), "system-applications"),
        ]
        let home = FileManager.default.homeDirectoryForCurrentUser
        roots.append((home.appendingPathComponent("Applications"), "user-applications"))
        return roots
    }

    /// Finds app bundles under `root`, deduplicated by bundle id and sorted by name.
    public static func apps(in root: URL, source: String, fileManager: FileManager = .default) -> [App] {
        var found: [App] = []
        for url in appBundles(in: root, depth: 1, fileManager: fileManager) {
            guard let data = try? Data(contentsOf: url.appendingPathComponent("Contents/Info.plist")),
                  let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
                  let id = info["CFBundleIdentifier"] as? String, !id.isEmpty
            else { continue }
            let name = (info["CFBundleDisplayName"] as? String)
                ?? (info["CFBundleName"] as? String)
                ?? url.deletingPathExtension().lastPathComponent
            found.append(App(id: id, name: name, source: source))
        }
        return found
    }

    private static func appBundles(in dir: URL, depth: Int, fileManager: FileManager) -> [URL] {
        guard let entries = try? fileManager.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])
        else { return [] }
        var bundles: [URL] = []
        for entry in entries {
            if entry.pathExtension == "app" {
                bundles.append(entry)
            } else if depth > 0, (try? entry.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true {
                bundles += appBundles(in: entry, depth: depth - 1, fileManager: fileManager)
            }
        }
        return bundles
    }

    /// Merges app lists: first occurrence of a bundle id wins; result sorted by name.
    public static func merge(_ lists: [[App]]) -> [App] {
        var seen = Set<String>()
        var out: [App] = []
        for app in lists.joined() where seen.insert(app.id.lowercased()).inserted {
            out.append(app)
        }
        return Array(out.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
            .prefix(UbitProtocol.maxApps))
    }
}

#if canImport(AppKit)
import AppKit

extension InventoryScanner {
    /// Full scan of this Mac.
    public static func scan() -> Inventory {
        let found = merge(defaultRoots.map { Self.apps(in: $0.url, source: $0.source) })
        return Inventory(apps: found,
                         defaultBrowser: defaultHandler(for: "https://example.com"),
                         defaultMail: defaultHandler(for: "mailto:someone@example.com"))
    }

    private static func defaultHandler(for urlString: String) -> String? {
        guard let url = URL(string: urlString),
              let app = NSWorkspace.shared.urlForApplication(toOpen: url)
        else { return nil }
        return Bundle(url: app)?.bundleIdentifier
    }
}
#endif
