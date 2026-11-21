import Foundation

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
    /// Replaces any existing disk and resets the UEFI variables.
    public func importImage(from source: URL, size: UInt64 = VMBundle.diskSize) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: root, withIntermediateDirectories: true)
        for url in [disk, efiVariables] where fm.fileExists(atPath: url.path) {
            try fm.removeItem(at: url)
        }
        try fm.copyItem(at: source, to: disk)
        let current = (try fm.attributesOfItem(atPath: disk.path)[.size] as? NSNumber)?.uint64Value ?? 0
        if current < size {
            let handle = try FileHandle(forWritingTo: disk)
            defer { try? handle.close() }
            try handle.truncate(atOffset: size)
        }
    }

    /// Deletes the VM's disk and settings.
    public func remove() throws {
        if FileManager.default.fileExists(atPath: root.path) {
            try FileManager.default.removeItem(at: root)
        }
    }
}
