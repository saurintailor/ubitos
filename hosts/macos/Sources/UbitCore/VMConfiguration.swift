#if canImport(Virtualization)
import Foundation
import Virtualization
import UbitProtocol

public enum VMConfigurationError: Error, CustomStringConvertible {
    case noDisk(URL)

    public var description: String {
        switch self {
        case .noDisk(let url): return "No disk image at \(url.path). Import an image first."
        }
    }
}

/// Builds the Virtualization.framework configuration for the ubit guest.
public enum VMConfigurationBuilder {
    public static func make(bundle: VMBundle) throws -> VZVirtualMachineConfiguration {
        guard bundle.hasDisk else { throw VMConfigurationError.noDisk(bundle.disk) }
        let config = VZVirtualMachineConfiguration()

        // Half the cores (at least 2) and a quarter of RAM between 4 and 8 GiB.
        let cores = max(2, ProcessInfo.processInfo.processorCount / 2)
        config.cpuCount = min(max(cores, VZVirtualMachineConfiguration.minimumAllowedCPUCount),
                              VZVirtualMachineConfiguration.maximumAllowedCPUCount)
        let quarter = ProcessInfo.processInfo.physicalMemory / 4
        let memory = min(max(quarter, 4 << 30), 8 << 30)
        config.memorySize = min(max(memory, VZVirtualMachineConfiguration.minimumAllowedMemorySize),
                                VZVirtualMachineConfiguration.maximumAllowedMemorySize)

        config.platform = VZGenericPlatformConfiguration()
        let boot = VZEFIBootLoader()
        boot.variableStore = try efiVariableStore(at: bundle.efiVariables)
        config.bootLoader = boot

        let disk = try VZDiskImageStorageDeviceAttachment(
            url: bundle.disk, readOnly: false, cachingMode: .automatic, synchronizationMode: .full)
        config.storageDevices = [VZVirtioBlockDeviceConfiguration(attachment: disk)]

        let network = VZVirtioNetworkDeviceConfiguration()
        network.attachment = VZNATNetworkDeviceAttachment()
        network.macAddress = macAddress(stored: bundle.macAddressFile)
        config.networkDevices = [network]

        let graphics = VZVirtioGraphicsDeviceConfiguration()
        graphics.scanouts = [VZVirtioGraphicsScanoutConfiguration(widthInPixels: 1920, heightInPixels: 1200)]
        config.graphicsDevices = [graphics]
        config.keyboards = [VZUSBKeyboardConfiguration()]
        config.pointingDevices = [VZUSBScreenCoordinatePointingDeviceConfiguration()]

        // Sound out only. No microphone: the guest never hears the room.
        let sound = VZVirtioSoundDeviceConfiguration()
        let output = VZVirtioSoundDeviceOutputStreamConfiguration()
        output.sink = VZHostAudioOutputStreamSink()
        sound.streams = [output]
        config.audioDevices = [sound]

        // Clipboard: SPICE agent port, served by spice-vdagent in the guest.
        let console = VZVirtioConsoleDeviceConfiguration()
        let spicePort = VZVirtioConsolePortConfiguration()
        spicePort.name = VZSpiceAgentPortAttachment.spiceAgentPortName
        let spice = VZSpiceAgentPortAttachment()
        spice.sharesClipboard = true
        spicePort.attachment = spice
        console.ports[0] = spicePort
        config.consoleDevices = [console]

        // Agent channel: vsock; the app connects to port 7710 in the guest.
        config.socketDevices = [VZVirtioSocketDeviceConfiguration()]
        config.entropyDevices = [VZVirtioEntropyDeviceConfiguration()]
        config.memoryBalloonDevices = [VZVirtioTraditionalMemoryBalloonDeviceConfiguration()]

        try config.validate()
        return config
    }

    private static func efiVariableStore(at url: URL) throws -> VZEFIVariableStore {
        if FileManager.default.fileExists(atPath: url.path) {
            return VZEFIVariableStore(url: url)
        }
        return try VZEFIVariableStore(creatingVariableStoreAt: url, options: [])
    }

    private static func macAddress(stored file: URL) -> VZMACAddress {
        if let text = try? String(contentsOf: file, encoding: .utf8),
           let address = VZMACAddress(string: text.trimmingCharacters(in: .whitespacesAndNewlines)) {
            return address
        }
        let address = VZMACAddress.randomLocallyAdministered()
        try? address.string.write(to: file, atomically: true, encoding: .utf8)
        return address
    }
}

/// Connects to ubit-agent inside a running VM over vsock.
@MainActor
public final class AgentLink {
    /// Keeps the vsock connection (and so its file descriptor) alive.
    private var connection: VZVirtioSocketConnection?

    public init() {}

    /// Retries every 2 seconds until the agent answers or `timeout` passes.
    public func connect(to vm: VZVirtualMachine, timeout: Duration = .seconds(180)) async throws -> AgentSession {
        guard let device = vm.socketDevices.first as? VZVirtioSocketDevice else {
            throw ProtocolError.invalid("the VM has no vsock device")
        }
        let clock = ContinuousClock()
        let deadline = clock.now + timeout
        while true {
            do {
                let conn: VZVirtioSocketConnection = try await withCheckedThrowingContinuation { cont in
                    device.connect(toPort: UbitProtocol.agentVsockPort) { cont.resume(with: $0) }
                }
                connection = conn
                return AgentSession(channel: FileDescriptorChannel(fileDescriptor: conn.fileDescriptor))
            } catch {
                if clock.now >= deadline { throw error }
                try await Task.sleep(for: .seconds(2))
            }
        }
    }

    public func close() {
        connection?.close()
        connection = nil
    }
}
#endif
