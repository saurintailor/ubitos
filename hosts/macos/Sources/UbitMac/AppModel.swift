#if os(macOS)
import AppKit
import Foundation
import UbitCore
import UbitProtocol
import UniformTypeIdentifiers
import Virtualization

/// App state: one VM, its lifecycle, and the agent conversation.
@MainActor
final class AppModel: NSObject, ObservableObject {
    enum Phase: Equatable {
        case needsImage
        case importing
        case stopped
        case starting
        case running
        case stopping
        case failed(String)
    }

    enum AgentState: Equatable {
        case idle
        case connecting
        case scoring
        case scored
        case unavailable(String)
    }

    @Published private(set) var phase: Phase
    @Published private(set) var agent: AgentState = .idle
    @Published private(set) var score: ScoreSummary?
    @Published private(set) var guestInfo: String?
    @Published private(set) var vm: VZVirtualMachine?

    let bundle = VMBundle.standard
    private let link = AgentLink()
    private var agentTask: Task<Void, Never>?

    static let shellVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0-dev"

    override init() {
        phase = VMBundle.standard.hasDisk ? .stopped : .needsImage
        super.init()
    }

    // MARK: Image

    func chooseImage() {
        let panel = NSOpenPanel()
        panel.title = "Choose the ubit image"
        panel.message = "Pick the decompressed ubit-mac-arm64 .img file."
        panel.allowedContentTypes = [UTType(filenameExtension: "img") ?? .data, UTType(filenameExtension: "raw") ?? .data]
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let url = panel.url else { return }

        phase = .importing
        let bundle = self.bundle
        Task.detached(priority: .userInitiated) {
            do {
                try bundle.importImage(from: url)
                await MainActor.run { self.phase = .stopped }
            } catch {
                await MainActor.run { self.phase = .failed("Could not import the image: \(error.localizedDescription)") }
            }
        }
    }

    // MARK: VM lifecycle

    func start() {
        guard vm == nil || phase == .stopped else { return }
        do {
            let config = try VMConfigurationBuilder.make(bundle: bundle)
            let machine = VZVirtualMachine(configuration: config)
            machine.delegate = self
            vm = machine
            phase = .starting
            score = nil
            machine.start { [weak self] result in
                Task { @MainActor in
                    guard let self else { return }
                    switch result {
                    case .success:
                        self.phase = .running
                        self.talkToAgent()
                    case .failure(let error):
                        self.phase = .failed("The VM did not start: \(error.localizedDescription)")
                        self.vm = nil
                    }
                }
            }
        } catch {
            phase = .failed("\(error)")
        }
    }

    /// Asks the guest to shut down (ACPI power button), like pressing the power key.
    func stop() {
        guard let vm, phase == .running else { return }
        phase = .stopping
        do {
            try vm.requestStop()
        } catch {
            forceStop()
        }
    }

    func forceStop() {
        guard let vm else { return }
        vm.stop { [weak self] _ in
            Task { @MainActor in self?.didStop(error: nil) }
        }
    }

    private func didStop(error: Error?) {
        agentTask?.cancel()
        link.close()
        vm = nil
        agent = .idle
        if let error {
            phase = .failed("The VM stopped with an error: \(error.localizedDescription)")
        } else {
            phase = bundle.hasDisk ? .stopped : .needsImage
        }
    }

    // MARK: Agent

    /// Connects to ubit-agent, sends this Mac's inventory, and shows the score.
    func talkToAgent() {
        guard let vm else { return }
        agentTask?.cancel()
        agent = .connecting
        let shellVersion = Self.shellVersion
        agentTask = Task { [link] in
            do {
                let session = try await link.connect(to: vm)
                let (agentVersion, distro) = try await Task.detached {
                    try session.handshake(shellVersion: shellVersion)
                }.value
                self.guestInfo = "\(distro) · agent \(agentVersion)"
                self.agent = .scoring
                let summary = try await Task.detached {
                    try session.submit(InventoryScanner.scan())
                }.value
                self.score = summary
                self.agent = .scored
            } catch is CancellationError {
                self.agent = .idle
            } catch {
                self.agent = .unavailable("\(error)")
            }
        }
    }
}

extension AppModel: VZVirtualMachineDelegate {
    nonisolated func guestDidStop(_ virtualMachine: VZVirtualMachine) {
        Task { @MainActor in self.didStop(error: nil) }
    }

    nonisolated func virtualMachine(_ virtualMachine: VZVirtualMachine, didStopWithError error: Error) {
        Task { @MainActor in self.didStop(error: error) }
    }
}
#endif
