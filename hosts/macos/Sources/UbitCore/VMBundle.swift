import Foundation
#if canImport(Darwin)
import Darwin
#elseif canImport(Glibc)
import Glibc
#endif

/// Where the VM's files live: ~/Library/Application Support/ubit/vm/
///   disk.img       raw disk (imported from the release image, then enlarged)
///   efi-vars.fd    UEFI variable store
///   mac-address    stable MAC for the NAT interface
public struct VMBundle: Sendable {
    public let root: URL
    public var disk: URL { root.appendingPathComponent("disk.img") }
    public var efiVariables: URL { root.appendingPathComponent("efi-vars.fd") }
    public var macAddressFile: URL { root.appendingPathComponent("mac-address") }

    /// Size the disk is grown to after import. The guest grows its root partition on boot.
    public static let diskSize: UInt64 = 64 << 30

    public init(root: URL) {
        self.root = root
    }

    public static var standard: VMBundle {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return VMBundle(root: support.appendingPathComponent("ubit/vm", isDirectory: true))
    }

    public var hasDisk: Bool {
        FileManager.default.fileExists(atPath: disk.path)
    }

    /// Copies a raw image into the bundle (an APFS clone when on the same volume),
    /// then makes the file sparse-large so the guest has room to grow.
    /// The new disk is staged first; the old disk and UEFI variables are replaced only
    /// once staging succeeds, so a failed import leaves the existing VM intact.
    public func importImage(from source: URL, size: UInt64 = VMBundle.diskSize) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        let staged = root.appendingPathComponent("disk.img.importing")
        if fm.fileExists(atPath: staged.path) { try fm.removeItem(at: staged) }
        do {
            try fm.copyItem(at: source, to: staged)
            let current = (try fm.attributesOfItem(atPath: staged.path)[.size] as? NSNumber)?.uint64Value ?? 0
            if current < size {
                let handle = try FileHandle(forWritingTo: staged)
                defer { try? handle.close() }
                try handle.truncate(atOffset: size)
            }
        } catch {
            try? fm.removeItem(at: staged)
            throw error
        }
        // Staging succeeded; only now drop the old disk. rename(2) over it is atomic.
        if rename(staged.path, disk.path) != 0 {
            throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
        }
        if fm.fileExists(atPath: efiVariables.path) { try fm.removeItem(at: efiVariables) }
    }

    /// Deletes the VM's disk and settings.
    public func remove() throws {
        if FileManager.default.fileExists(atPath: root.path) {
            try FileManager.default.removeItem(at: root)
        }
    }
}
